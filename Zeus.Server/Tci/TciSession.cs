// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Zeus.Contracts;

namespace Zeus.Server.Tci;

/// <summary>
/// Per-client TCI session. Manages WebSocket send/receive loops, command
/// parsing, event broadcasting, and rate limiting. Mirrors the StreamingHub
/// pattern: parallel send + receive tasks, bounded channel with drop-oldest.
/// </summary>
public sealed class TciSession : IDisposable
{
    private const int MaxBacklogPerClient = 16;
    private const int MaxInboundTextBytes = 8 * 1024;
    private const int MaxInboundBinaryBytes = 2 * 1024 * 1024; // 2 MB for future binary frames

    private readonly Guid _id;
    private readonly WebSocket _ws;
    private readonly ILogger _log;
    private readonly RadioService _radio;
    private readonly TxService _tx;
    private readonly DspPipelineService _pipeline;
    private readonly SpotManager _spots;
    private readonly TciOptions _options;
    private readonly TciRateLimiter _rateLimiter;

    private readonly Channel<string> _sendQueue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(MaxBacklogPerClient)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    // Track current drive level so we can echo it back on query
    private int _lastDrivePercent = 50;

    public Guid Id => _id;

    public TciSession(
        Guid id,
        WebSocket ws,
        ILogger log,
        RadioService radio,
        TxService tx,
        DspPipelineService pipeline,
        SpotManager spots,
        TciOptions options)
    {
        _id = id;
        _ws = ws;
        _log = log;
        _radio = radio;
        _tx = tx;
        _pipeline = pipeline;
        _spots = spots;
        _options = options;
        _rateLimiter = new TciRateLimiter(options.RateLimitMs, Send);
    }

    /// <summary>
    /// Main session loop: send handshake, then run parallel send/receive loops.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            // Send handshake immediately after WS upgrade
            await SendHandshakeAsync(ct);

            var sendTask = SendLoopAsync(ct);
            var recvTask = ReceiveLoopAsync(ct);
            await Task.WhenAny(sendTask, recvTask);
            _sendQueue.Writer.TryComplete();
            try { await Task.WhenAll(sendTask, recvTask); } catch { /* drained */ }
        }
        finally
        {
            _rateLimiter.Dispose();
        }
    }

    /// <summary>
    /// Enqueue a TCI command for immediate send (bypass rate limiter).
    /// </summary>
    public void Send(string commandLine)
    {
        _sendQueue.Writer.TryWrite(commandLine);
    }

    /// <summary>
    /// Enqueue a rate-limited event (VFO/DDS changes during tuning).
    /// </summary>
    public void SendRateLimited(string key, string commandLine)
    {
        _rateLimiter.Enqueue(key, commandLine);
    }

    private async Task SendHandshakeAsync(CancellationToken ct)
    {
        var state = _radio.Snapshot();
        string handshake = TciHandshake.BuildHandshake(
            state,
            state.SampleRate,
            _tx.IsMoxOn,
            _tx.IsTunOn,
            _lastDrivePercent);

        var bytes = Encoding.ASCII.GetBytes(handshake);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        _log.LogInformation("tci.handshake sent client={Id}", _id);
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var line in _sendQueue.Reader.ReadAllAsync(ct))
            {
                if (_ws.State != WebSocketState.Open) break;
                var bytes = Encoding.ASCII.GetBytes(line);
                await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            _log.LogDebug(ex, "tci send loop ended client={Id}", _id);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buf = new byte[8 * 1024];
        byte[]? accum = null;
        int accumLen = 0;

        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(buf, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    // Future: handle binary IQ/audio subscription frames
                    _log.LogDebug("tci binary frame ignored (not implemented) len={Len}", result.Count);
                    continue;
                }

                // Text frame: accumulate and parse
                int chunkLen = result.Count;
                if (result.EndOfMessage && accum is null)
                {
                    // Fast path: single-fragment text message
                    string line = Encoding.ASCII.GetString(buf, 0, chunkLen);
                    HandleCommand(line);
                    continue;
                }

                // Multi-fragment message: accumulate
                if (accum is null)
                {
                    accum = ArrayPool<byte>.Shared.Rent(Math.Max(chunkLen, 4096));
                    accumLen = 0;
                }
                if (accumLen + chunkLen > MaxInboundTextBytes)
                {
                    _log.LogWarning("tci oversize text frame client={Id} len={Len}", _id, accumLen + chunkLen);
                    ArrayPool<byte>.Shared.Return(accum);
                    accum = null;
                    accumLen = 0;
                    continue;
                }
                if (accumLen + chunkLen > accum.Length)
                {
                    int newSize = Math.Min(MaxInboundTextBytes, accum.Length * 2);
                    while (newSize < accumLen + chunkLen) newSize = Math.Min(MaxInboundTextBytes, newSize * 2);
                    var grown = ArrayPool<byte>.Shared.Rent(newSize);
                    Buffer.BlockCopy(accum, 0, grown, 0, accumLen);
                    ArrayPool<byte>.Shared.Return(accum);
                    accum = grown;
                }
                Buffer.BlockCopy(buf, 0, accum, accumLen, chunkLen);
                accumLen += chunkLen;

                if (result.EndOfMessage)
                {
                    string line = Encoding.ASCII.GetString(accum, 0, accumLen);
                    HandleCommand(line);
                    ArrayPool<byte>.Shared.Return(accum);
                    accum = null;
                    accumLen = 0;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            _log.LogDebug(ex, "tci recv loop ended client={Id}", _id);
        }
        finally
        {
            if (accum is not null) ArrayPool<byte>.Shared.Return(accum);
        }
    }

    /// <summary>
    /// Parse and dispatch an inbound TCI command. May contain multiple
    /// semicolon-terminated commands in one line.
    /// </summary>
    private void HandleCommand(string line)
    {
        // TCI clients may batch multiple commands in one WebSocket frame,
        // separated by semicolons. Split and handle each.
        var commands = line.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var cmd in commands)
        {
            if (string.IsNullOrWhiteSpace(cmd)) continue;
            var parsed = TciProtocol.Parse(cmd);
            if (parsed is null)
            {
                _log.LogDebug("tci malformed command: {Cmd}", cmd);
                continue;
            }

            var (command, args) = parsed.Value;
            DispatchCommand(command, args);
        }
    }

    private void DispatchCommand(string command, string[] args)
    {
        try
        {
            switch (command.ToLowerInvariant())
            {
                // --- Frequency / Oscillator ---
                case "vfo":
                    HandleVfo(args);
                    break;
                case "dds":
                    HandleDds(args);
                    break;
                case "if":
                    HandleIf(args);
                    break;

                // --- Mode / Filter ---
                case "modulation":
                    HandleModulation(args);
                    break;
                case "rx_filter_band":
                    HandleRxFilterBand(args);
                    break;

                // --- PTT / TX ---
                case "trx":
                    HandleTrx(args);
                    break;
                case "tune":
                    HandleTune(args);
                    break;
                case "drive":
                    HandleDrive(args);
                    break;
                case "tune_drive":
                    HandleTuneDrive(args);
                    break;

                // --- Audio ---
                case "mute":
                    HandleMute(args);
                    break;
                case "rx_mute":
                    HandleRxMute(args);
                    break;
                case "volume":
                    HandleVolume(args);
                    break;
                case "mon_enable":
                    HandleMonEnable(args);
                    break;
                case "mon_volume":
                    HandleMonVolume(args);
                    break;

                // --- Split / RIT / XIT ---
                case "split_enable":
                    HandleSplitEnable(args);
                    break;
                case "rit_enable":
                    HandleRitEnable(args);
                    break;
                case "rit_offset":
                    HandleRitOffset(args);
                    break;
                case "xit_enable":
                    HandleXitEnable(args);
                    break;
                case "xit_offset":
                    HandleXitOffset(args);
                    break;
                case "lock":
                    HandleLock(args);
                    break;

                // --- Lifecycle ---
                case "start":
                    HandleStart(args);
                    break;
                case "stop":
                    HandleStop(args);
                    break;

                // --- Spots ---
                case "spot":
                    HandleSpot(args);
                    break;
                case "spot_delete":
                    HandleSpotDelete(args);
                    break;
                case "spot_clear":
                    HandleSpotClear(args);
                    break;

                // --- Binary streams (future) ---
                case "iq_start":
                case "iq_stop":
                case "audio_start":
                case "audio_stop":
                    _log.LogDebug("tci command not implemented: {Cmd}", command);
                    break;

                // --- Unknown ---
                default:
                    _log.LogDebug("tci unknown command: {Cmd}", command);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "tci command handler exception: {Cmd}", command);
        }
    }

    // --- Command Handlers ---

    private void HandleVfo(string[] args)
    {
        // vfo:<rx>,<chan>,<hz> or vfo:<rx>,<chan> (query)
        if (args.Length < 2) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;
        if (!TciProtocol.TryParseInt(args[1], out int chan)) return;

        if (args.Length == 2)
        {
            // Query: echo current VFO
            var state = _radio.Snapshot();
            Send(TciProtocol.Command("vfo", rx, chan, state.VfoHz));
        }
        else if (args.Length >= 3 && TciProtocol.TryParseLong(args[2], out long hz))
        {
            // Set VFO
            _radio.SetVfo(hz);
            // Don't echo back immediately — the StateChanged event will broadcast it
        }
    }

    private void HandleDds(string[] args)
    {
        // dds:<rx>,<hz> or dds:<rx> (query)
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            // Query
            var state = _radio.Snapshot();
            Send(TciProtocol.Command("dds", rx, state.VfoHz));
        }
        else if (args.Length >= 2 && TciProtocol.TryParseLong(args[1], out long hz))
        {
            // Set DDS (same as VFO for single-RX)
            _radio.SetVfo(hz);
        }
    }

    private void HandleIf(string[] args)
    {
        // if:<rx>,<chan>,<offset_hz> or if:<rx>,<chan> (query)
        // We don't support IF offset yet — always zero
        if (args.Length < 2) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;
        if (!TciProtocol.TryParseInt(args[1], out int chan)) return;

        if (args.Length == 2)
        {
            Send(TciProtocol.Command("if", rx, chan, 0));
        }
        // Ignore set commands — IF offset not implemented
    }

    private void HandleModulation(string[] args)
    {
        // modulation:<rx>,<MODE> or modulation:<rx> (query)
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            var state = _radio.Snapshot();
            string tciMode = TciProtocol.ModeToTci(state.Mode);
            Send(TciProtocol.Command("modulation", rx, tciMode));
        }
        else if (args.Length >= 2)
        {
            var mode = TciProtocol.TciToMode(args[1]);
            if (mode.HasValue)
            {
                _radio.SetMode(mode.Value);
            }
        }
    }

    private void HandleRxFilterBand(string[] args)
    {
        // rx_filter_band:<rx>,<lo_hz>,<hi_hz> or rx_filter_band:<rx> (query)
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            var state = _radio.Snapshot();
            Send(TciProtocol.Command("rx_filter_band", rx, state.FilterLowHz, state.FilterHighHz));
        }
        else if (args.Length >= 3 &&
                 TciProtocol.TryParseInt(args[1], out int lo) &&
                 TciProtocol.TryParseInt(args[2], out int hi))
        {
            _radio.SetFilter(lo, hi);
        }
    }

    private void HandleTrx(string[] args)
    {
        // trx:<rx>,<bool>[,tci] or trx:<rx> (query)
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            Send(TciProtocol.Command("trx", rx, _tx.IsMoxOn));
        }
        else if (args.Length >= 2 && TciProtocol.TryParseBool(args[1], out bool on))
        {
            _tx.TrySetMox(on, out _);
        }
    }

    private void HandleTune(string[] args)
    {
        // tune:<rx>,<bool> or tune:<rx> (query)
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            Send(TciProtocol.Command("tune", rx, _tx.IsTunOn));
        }
        else if (args.Length >= 2 && TciProtocol.TryParseBool(args[1], out bool on))
        {
            _tx.TrySetTun(on, out _);
        }
    }

    private void HandleDrive(string[] args)
    {
        // drive:<rx>,<0-100> or drive:<rx> (query)
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            Send(TciProtocol.Command("drive", rx, _lastDrivePercent));
        }
        else if (args.Length >= 2 && TciProtocol.TryParseInt(args[1], out int pct))
        {
            int clamped = Math.Clamp(pct, 0, _options.LimitPowerLevels ? 50 : 100);
            _lastDrivePercent = clamped;
            _radio.SetDrive(clamped);
        }
    }

    private void HandleTuneDrive(string[] args)
    {
        // tune_drive:<rx>,<0-100> or tune_drive:<rx> (query)
        // For now, same as drive
        HandleDrive(args);
    }

    private void HandleMute(string[] args)
    {
        // mute:<bool> or mute (query)
        if (args.Length == 0)
        {
            Send(TciProtocol.Command("mute", false)); // no master mute yet
        }
        // Ignore set — not implemented
    }

    private void HandleRxMute(string[] args)
    {
        // rx_mute:<rx>,<bool> or rx_mute:<rx> (query)
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            Send(TciProtocol.Command("rx_mute", rx, false));
        }
        // Ignore set — not implemented
    }

    private void HandleVolume(string[] args)
    {
        // volume:<db> or volume (query)
        if (args.Length == 0)
        {
            Send(TciProtocol.Command("volume", 0)); // no master volume yet
        }
        // Ignore set — not implemented
    }

    private void HandleMonEnable(string[] args)
    {
        // mon_enable:<bool> or mon_enable (query)
        if (args.Length == 0)
        {
            Send(TciProtocol.Command("mon_enable", false));
        }
        // Ignore set — sidetone not implemented
    }

    private void HandleMonVolume(string[] args)
    {
        // mon_volume:<db> or mon_volume (query)
        if (args.Length == 0)
        {
            Send(TciProtocol.Command("mon_volume", -20));
        }
        // Ignore set — sidetone not implemented
    }

    private void HandleSplitEnable(string[] args)
    {
        // split_enable:<rx>,<bool> or split_enable:<rx> (query)
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            // Query: return current split state
            var state = _radio.Snapshot();
            Send(TciProtocol.Command("split_enable", rx, state.SplitEnabled));
        }
        else if (args.Length >= 2 && TciProtocol.TryParseBool(args[1], out bool enabled))
        {
            // Set split mode
            _radio.SetSplit(enabled);
        }
    }

    private void HandleRitEnable(string[] args)
    {
        // rit_enable:<rx>,<bool> or rit_enable:<rx> (query)
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            Send(TciProtocol.Command("rit_enable", rx, false));
        }
        // Ignore set — RIT not implemented
    }

    private void HandleRitOffset(string[] args)
    {
        // rit_offset:<rx>,<hz> or rit_offset:<rx> (query)
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            Send(TciProtocol.Command("rit_offset", rx, 0));
        }
        // Ignore set — RIT not implemented
    }

    private void HandleXitEnable(string[] args)
    {
        // xit_enable:<rx>,<bool> or xit_enable:<rx> (query)
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            Send(TciProtocol.Command("xit_enable", rx, false));
        }
        // Ignore set — XIT not implemented
    }

    private void HandleXitOffset(string[] args)
    {
        // xit_offset:<rx>,<hz> or xit_offset:<rx> (query)
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            Send(TciProtocol.Command("xit_offset", rx, 0));
        }
        // Ignore set — XIT not implemented
    }

    private void HandleLock(string[] args)
    {
        // lock:<rx>,<bool> or lock:<rx> (query)
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            Send(TciProtocol.Command("lock", rx, false));
        }
        // Ignore set — lock not implemented
    }

    private void HandleStart(string[] args)
    {
        // start — power on (no-op if already connected)
        // We can't auto-connect without knowing the endpoint; log and ignore
        _log.LogDebug("tci 'start' command received (no-op — connect via REST API)");
    }

    private void HandleStop(string[] args)
    {
        // stop — power off
        _ = _radio.DisconnectAsync();
    }

    private void HandleSpot(string[] args)
    {
        // spot:<callsign>,<mode>,<freq_hz>,<argb>[,<comment>]
        if (args.Length < 4) return;
        string callsign = args[0];
        string mode = args[1];
        if (!TciProtocol.TryParseLong(args[2], out long freqHz)) return;
        if (!TciProtocol.TryParseInt(args[3], out int argbSigned)) return;
        uint argb = unchecked((uint)argbSigned);
        string? comment = args.Length > 4 ? args[4] : null;

        _spots.AddSpot(callsign, mode, freqHz, argb, comment);
        _log.LogDebug("tci spot added: {Call} {Mode} {Freq} Hz", callsign, mode, freqHz);
    }

    private void HandleSpotDelete(string[] args)
    {
        // spot_delete:<callsign>
        if (args.Length < 1) return;
        _spots.RemoveSpot(args[0]);
    }

    private void HandleSpotClear(string[] args)
    {
        // spot_clear
        _spots.ClearAll();
    }

    public void Dispose()
    {
        _rateLimiter.Dispose();
    }
}
