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
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Zeus.Contracts;

namespace Zeus.Server.Tci;

/// <summary>
/// Per-client TCI session. Manages WebSocket send/receive loops, command
/// parsing, event broadcasting, and rate limiting.
///
/// Outbound architecture mirrors Thetis TCIServer: three priority queues
/// (Urgent / Binary / Control) drained by a single send loop in priority
/// order. Queues are unbounded — backpressure is provided implicitly by
/// the underlying socket send window; on a write exception, the session
/// is torn down.
/// </summary>
public sealed class TciSession : IDisposable
{
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

    private readonly ConcurrentQueue<TciOutboundFrame> _urgentQueue = new();
    private readonly ConcurrentQueue<TciOutboundFrame> _binaryQueue = new();
    private readonly ConcurrentQueue<TciOutboundFrame> _controlQueue = new();
    private readonly SemaphoreSlim _outboundSignal = new(0);

    // Track current drive level so we can echo it back on query
    private int _lastDrivePercent = 50;

    // Per-session binary stream subscriptions. Producers (TciServer publish
    // path) check WantsIqStream(rx) before building/dispatching frames.
    private readonly object _streamLock = new();
    private readonly HashSet<int> _iqStreamEnabled = new();
    private int _iqSampleRate = 48000;

    public Guid Id => _id;

    /// <summary>True if this session has subscribed to IQ for the given receiver.</summary>
    public bool WantsIqStream(int receiver)
    {
        lock (_streamLock) return _iqStreamEnabled.Contains(receiver);
    }

    /// <summary>True if this session has subscribed to IQ for any receiver.</summary>
    public bool WantsAnyIqStream()
    {
        lock (_streamLock) return _iqStreamEnabled.Count > 0;
    }

    /// <summary>Last client-requested IQ sample rate, clamped to [48000, 384000].</summary>
    public int IqSampleRate
    {
        get { lock (_streamLock) return _iqSampleRate; }
    }

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
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            // Send handshake immediately after WS upgrade
            await SendHandshakeAsync(linkedCts.Token);

            var sendTask = SendLoopAsync(linkedCts.Token);
            var recvTask = ReceiveLoopAsync(linkedCts.Token);
            await Task.WhenAny(sendTask, recvTask);
            linkedCts.Cancel();
            try { await Task.WhenAll(sendTask, recvTask); } catch { /* drained */ }
        }
        finally
        {
            _rateLimiter.Dispose();
        }
    }

    /// <summary>
    /// Enqueue a TCI text command at Control priority (commands, query echoes,
    /// state-change events). Bypasses the rate limiter.
    /// </summary>
    public void Send(string commandLine)
    {
        Enqueue(new TciOutboundFrame(commandLine), TciOutboundPriority.Control);
    }

    /// <summary>
    /// Enqueue a TCI text command at Urgent priority (ping/pong, close, errors).
    /// </summary>
    public void SendUrgent(string commandLine)
    {
        Enqueue(new TciOutboundFrame(commandLine), TciOutboundPriority.Urgent);
    }

    /// <summary>
    /// Enqueue a binary frame (IQ / RX-audio / TX-chrono stream payload) at
    /// Binary priority. Frame bytes are sent verbatim as a WebSocket binary
    /// message — the caller is responsible for the TCI 64-byte stream header.
    /// </summary>
    public void SendBinary(byte[] payload)
    {
        Enqueue(new TciOutboundFrame(payload), TciOutboundPriority.Binary);
    }

    /// <summary>
    /// Enqueue a rate-limited event (VFO/DDS changes during tuning).
    /// </summary>
    public void SendRateLimited(string key, string commandLine)
    {
        _rateLimiter.Enqueue(key, commandLine);
    }

    private void Enqueue(TciOutboundFrame frame, TciOutboundPriority priority)
    {
        switch (priority)
        {
            case TciOutboundPriority.Urgent:
                _urgentQueue.Enqueue(frame);
                break;
            case TciOutboundPriority.Binary:
                _binaryQueue.Enqueue(frame);
                break;
            default:
                _controlQueue.Enqueue(frame);
                break;
        }
        _outboundSignal.Release();
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

    /// <summary>
    /// Single send loop draining Urgent → Binary → Control queues in priority
    /// order. On any send failure the loop exits, the linked CTS cancels the
    /// receive loop, and the session tears down (matches Thetis abortSocketTransport).
    /// </summary>
    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                await _outboundSignal.WaitAsync(ct);
                if (TryDequeueNext(out var frame))
                {
                    await SendFrameAsync(frame, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            _log.LogDebug(ex, "tci send loop ended client={Id}", _id);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "tci send loop write failed client={Id}", _id);
        }
    }

    private bool TryDequeueNext(out TciOutboundFrame frame)
    {
        if (_urgentQueue.TryDequeue(out frame)) return true;
        if (_binaryQueue.TryDequeue(out frame)) return true;
        if (_controlQueue.TryDequeue(out frame)) return true;
        frame = default;
        return false;
    }

    private async Task SendFrameAsync(TciOutboundFrame frame, CancellationToken ct)
    {
        if (_ws.State != WebSocketState.Open) return;
        if (frame.IsBinary)
        {
            await _ws.SendAsync(frame.Bytes!, WebSocketMessageType.Binary, true, ct);
        }
        else
        {
            var bytes = Encoding.ASCII.GetBytes(frame.Text!);
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
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

                // --- AGC ---
                case "agc_gain":
                    HandleAgcGain(args);
                    break;

                // --- CW keyer / macros (ack-only; no CW engine yet) ---
                case "cw_macros_speed":
                case "cw_macros":
                case "cw_msg":
                case "keyer":
                    _log.LogDebug("tci cw command accepted but unimplemented (no CW engine): {Cmd}", command);
                    break;

                // --- Split / RIT / XIT / Lock (stubs) ---
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

                // --- Binary streams ---
                case "iq_start":
                    HandleIqStart(args);
                    break;
                case "iq_stop":
                    HandleIqStop(args);
                    break;
                case "iq_samplerate":
                    HandleIqSampleRate(args);
                    break;
                case "audio_start":
                case "audio_stop":
                case "audio_samplerate":
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

    private void HandleAgcGain(string[] args)
    {
        // agc_gain:<rx>,<db> or agc_gain:<rx> (query)
        // ExpertSDR3 TCI spec: AGC gain is synonymous with AGC top (max gain)
        // Range: -20 to 120 dB per Thetis convention
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            // Query: echo current AGC top
            var state = _radio.Snapshot();
            Send(TciProtocol.Command("agc_gain", rx, (int)state.AgcTopDb));
        }
        else if (args.Length >= 2 && TciProtocol.TryParseDouble(args[1], out double db))
        {
            // Set AGC top (gain)
            _radio.SetAgcTop(db);
            // StateChanged event will broadcast the update to all clients
        }
    }

    private void HandleSplitEnable(string[] args)
    {
        // split_enable:<rx>,<bool> or split_enable:<rx> (query)
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;

        if (args.Length == 1)
        {
            Send(TciProtocol.Command("split_enable", rx, false));
        }
        // Ignore set — split not implemented
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

    private void HandleIqStart(string[] args)
    {
        // iq_start:<rx>,<bool>  — start (true) or stop (false) per-receiver IQ stream
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;
        bool enable = true;
        if (args.Length >= 2 && TciProtocol.TryParseBool(args[1], out bool parsed))
            enable = parsed;
        SetIqStream(rx, enable);
    }

    private void HandleIqStop(string[] args)
    {
        // iq_stop:<rx>  — alias of iq_start:<rx>,false
        if (args.Length < 1) return;
        if (!TciProtocol.TryParseInt(args[0], out int rx)) return;
        SetIqStream(rx, false);
    }

    private void HandleIqSampleRate(string[] args)
    {
        // iq_samplerate:<rate>  or  iq_samplerate (query)
        // Range matches Thetis: [48000, 384000]. Stored on the session; the
        // actual rate of published frames is the radio's native rate, echoed
        // back to the client when streaming starts.
        if (args.Length == 0)
        {
            Send(TciProtocol.Command("iq_samplerate", IqSampleRate));
            return;
        }
        if (TciProtocol.TryParseInt(args[0], out int rate))
        {
            rate = Math.Clamp(rate, 48000, 384000);
            lock (_streamLock) _iqSampleRate = rate;
            Send(TciProtocol.Command("iq_samplerate", rate));
        }
    }

    private void SetIqStream(int rx, bool enable)
    {
        lock (_streamLock)
        {
            if (enable) _iqStreamEnabled.Add(rx);
            else _iqStreamEnabled.Remove(rx);
        }
        Send(TciProtocol.Command("iq_start", rx, enable));
    }

    public void Dispose()
    {
        _rateLimiter.Dispose();
        _outboundSignal.Dispose();
    }
}

internal enum TciOutboundPriority
{
    Urgent,
    Binary,
    Control,
}

internal readonly struct TciOutboundFrame
{
    public readonly string? Text;
    public readonly byte[]? Bytes;

    public bool IsBinary => Bytes is not null;

    public TciOutboundFrame(string text)
    {
        Text = text;
        Bytes = null;
    }

    public TciOutboundFrame(byte[] bytes)
    {
        Text = null;
        Bytes = bytes;
    }
}
