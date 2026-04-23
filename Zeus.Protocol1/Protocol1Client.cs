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
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Protocol1.Discovery;

namespace Zeus.Protocol1;

public sealed class Protocol1Client : IProtocol1Client
{
    private const int DefaultFrameChannelCapacity = 64;
    private const int RxSocketTimeoutMs = 100;
    private const int ConsecutiveTimeoutsBeforeGiveUp = 10;
    private const int TxTickIntervalMs = 3;

    private readonly ILogger<Protocol1Client> _log;
    private readonly Channel<IqFrame> _channel;

    // Mutation state written from any thread, read from the TX thread.
    // 64-bit fields are written atomically on 64-bit .NET (Interlocked.Exchange used for safety).
    private long _vfoAHz = 7_100_000;
    private int _rate = (int)HpsdrSampleRate.Rate48k;
    private int _preamp;       // 0 / 1
    private int _attenDb;      // 0..31 dB (HpsdrAtten value)
    private int _antenna = (int)HpsdrAntenna.Ant1;
    private int _enableHl2Dither;
    private int _boardKind = (int)HpsdrBoardKind.HermesLite2;
    private int _hasN2adr;      // 0 / 1
    private int _mox;           // 0 / 1
    private int _drivePct;      // 0..100 UI percent; mapped to 0..255 on snapshot
    private long _droppedFrames;
    private long _totalFrames;

    private Socket? _socket;
    private IPEndPoint? _remote;
    private Thread? _rxThread;
    private Task? _txTask;
    private CancellationTokenSource? _loopCts;
    private bool _disposed;

    // TX IQ source: WDSP-TXA-driven ring in the live path (task #7/#8), or
    // the built-in test-tone when caller wants a bring-up carrier. Default is
    // the tone so legacy callers (tests, tools/zeus-dump) keep working.
    private readonly ITxIqSource _txIqSource;

    public Protocol1Client(ILogger<Protocol1Client>? logger = null, ITxIqSource? iqSource = null)
    {
        _log = logger ?? NullLogger<Protocol1Client>.Instance;
        _txIqSource = iqSource ?? new TestToneGenerator();
        _channel = Channel.CreateBounded<IqFrame>(new BoundedChannelOptions(DefaultFrameChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    public ChannelReader<IqFrame> IqFrames => _channel.Reader;
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);
    public long TotalFrames => Interlocked.Read(ref _totalFrames);

    public event Action<TelemetryReading>? TelemetryReceived;
    public event Action<AdcOverloadStatus>? AdcOverloadObserved;

    public bool EnableHl2Dither
    {
        get => Volatile.Read(ref _enableHl2Dither) != 0;
        set => Interlocked.Exchange(ref _enableHl2Dither, value ? 1 : 0);
    }

    public Task ConnectAsync(IPEndPoint radioEndpoint, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_socket is not null) throw new InvalidOperationException("Already connected.");

        var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = 256 * 1024,
            SendBufferSize = 64 * 1024,
            ReceiveTimeout = RxSocketTimeoutMs,
        };
        sock.Bind(new IPEndPoint(IPAddress.Any, 0));

        _socket = sock;
        _remote = radioEndpoint;
        _log.LogInformation("Protocol1 bound local={Local} remote={Remote}", sock.LocalEndPoint, radioEndpoint);
        return Task.CompletedTask;
    }

    public Task StartAsync(StreamConfig config, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_socket is null || _remote is null) throw new InvalidOperationException("Call ConnectAsync first.");
        if (_loopCts is not null) throw new InvalidOperationException("Already started.");

        Interlocked.Exchange(ref _rate, (int)config.Rate);
        Interlocked.Exchange(ref _preamp, config.PreampOn ? 1 : 0);
        Interlocked.Exchange(ref _attenDb, config.Atten.ClampedDb);
        Interlocked.Exchange(ref _droppedFrames, 0);
        Interlocked.Exchange(ref _totalFrames, 0);

        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Send Metis start. We send 3× on macOS to work around first-UDP-drop
        // (doc 02 §3).
        SendStartStop(start: true);

        _rxThread = new Thread(RxLoop)
        {
            IsBackground = true,
            Name = "Zeus.Protocol1.Rx",
        };
        _rxThread.Start();

        _txTask = Task.Run(() => TxLoopAsync(_loopCts.Token), _loopCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_loopCts is null) return;

        try
        {
            _loopCts.Cancel();
        }
        catch (ObjectDisposedException) { }

        SendStartStop(start: false);

        if (_txTask is not null)
        {
            try { await _txTask.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { _log.LogWarning("TX loop did not exit within 2s."); }
        }

        _rxThread?.Join(TimeSpan.FromSeconds(2));

        _loopCts.Dispose();
        _loopCts = null;
        _rxThread = null;
        _txTask = null;

        // Drain stale RX packets for ~100 ms per doc 02 §3.
        await DrainSocketAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
    }

    public Task DisconnectAsync(CancellationToken ct)
    {
        if (_socket is not null)
        {
            try { _socket.Close(); } catch { /* best-effort */ }
            _socket.Dispose();
            _socket = null;
        }
        _remote = null;
        return Task.CompletedTask;
    }

    public void SetVfoAHz(long hz) => Interlocked.Exchange(ref _vfoAHz, hz);
    public void SetSampleRate(HpsdrSampleRate rate) => Interlocked.Exchange(ref _rate, (int)rate);
    public void SetPreamp(bool on) => Interlocked.Exchange(ref _preamp, on ? 1 : 0);
    public void SetAttenuator(HpsdrAtten atten) => Interlocked.Exchange(ref _attenDb, atten.ClampedDb);
    public void SetAntennaRx(HpsdrAntenna ant) => Interlocked.Exchange(ref _antenna, (int)ant);
    public void SetBoardKind(HpsdrBoardKind board) => Interlocked.Exchange(ref _boardKind, (int)board);
    public void SetHasN2adr(bool hasN2adr) => Interlocked.Exchange(ref _hasN2adr, hasN2adr ? 1 : 0);
    public void SetMox(bool on) => Interlocked.Exchange(ref _mox, on ? 1 : 0);
    public void SetDrive(int percent) =>
        Interlocked.Exchange(ref _drivePct, Math.Clamp(percent, 0, 100));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
        try { DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
    }

    internal ControlFrame.CcState SnapshotState() => new(
        VfoAHz: Interlocked.Read(ref _vfoAHz),
        Rate: (HpsdrSampleRate)Volatile.Read(ref _rate),
        PreampOn: Volatile.Read(ref _preamp) != 0,
        Atten: new HpsdrAtten(Volatile.Read(ref _attenDb)),
        RxAntenna: (HpsdrAntenna)Volatile.Read(ref _antenna),
        Mox: Volatile.Read(ref _mox) != 0,
        EnableHl2Dither: Volatile.Read(ref _enableHl2Dither) != 0,
        Board: (HpsdrBoardKind)Volatile.Read(ref _boardKind),
        HasN2adr: Volatile.Read(ref _hasN2adr) != 0,
        // UI percent → raw 0..255 HPSDR drive byte. For MVP we use the 0..255
        // range directly (no calcLevel linearization step).
        DriveLevel: (byte)(Volatile.Read(ref _drivePct) * 255 / 100));

    private void RxLoop()
    {
        var sock = _socket!;
        var ct = _loopCts!.Token;
        var buffer = new byte[PacketParser.PacketLength];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        int consecutiveTimeouts = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n;
                try
                {
                    n = sock.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref remote);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                {
                    if (++consecutiveTimeouts >= ConsecutiveTimeoutsBeforeGiveUp)
                    {
                        _log.LogWarning("RX: {N} consecutive socket timeouts — radio gone", consecutiveTimeouts);
                        return;
                    }
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                consecutiveTimeouts = 0;

                if (n != PacketParser.PacketLength) continue;

                var rented = ArrayPool<double>.Shared.Rent(2 * PacketParser.ComplexSamplesPerPacket);
                bool ok = PacketParser.TryParsePacket(
                    buffer.AsSpan(0, n),
                    rented,
                    out uint seq,
                    out int samples,
                    out TelemetryReading telemetry0,
                    out TelemetryReading telemetry1,
                    out byte overloadBits);

                if (!ok)
                {
                    ArrayPool<double>.Shared.Return(rented);
                    continue;
                }

                ObserveSequence(seq);
                Interlocked.Increment(ref _totalFrames);

                // Fire per-frame: each USB frame's C&C is processed independently,
                // so pairs like (addr=1, addr=2) both contribute updates. The former
                // "last wins" logic masked the FWD reading whenever the HL2 paired
                // its FWD frame with a REF frame.
                // Synchronous fan-out; handlers must not block the RX thread.
                if (telemetry0.C0Address != 0)
                {
                    try { TelemetryReceived?.Invoke(telemetry0); }
                    catch (Exception ex) { _log.LogWarning(ex, "TelemetryReceived handler threw"); }
                }
                if (telemetry1.C0Address != 0)
                {
                    try { TelemetryReceived?.Invoke(telemetry1); }
                    catch (Exception ex) { _log.LogWarning(ex, "TelemetryReceived handler threw"); }
                }

                // Overload status fires every packet — the auto-ATT control loop
                // needs cleared-frame signals as well as set ones to decay the offset.
                try { AdcOverloadObserved?.Invoke(AdcOverloadStatus.FromBits(overloadBits)); }
                catch (Exception ex) { _log.LogWarning(ex, "AdcOverloadObserved handler threw"); }

                var rateHz = (HpsdrSampleRate)Volatile.Read(ref _rate) switch
                {
                    HpsdrSampleRate.Rate48k => 48_000,
                    HpsdrSampleRate.Rate96k => 96_000,
                    HpsdrSampleRate.Rate192k => 192_000,
                    HpsdrSampleRate.Rate384k => 384_000,
                    _ => 48_000,
                };

                var memory = new ReadOnlyMemory<double>(rented, 0, 2 * samples);
                var frame = new IqFrame(memory, samples, rateHz, seq, NowNs());
                // DropOldest: full-channel writes never block; oldest frame is discarded.
                // Its rented buffer is not returned to ArrayPool — we accept that the pool
                // will re-allocate rather than complicate ownership for MVP.
                _channel.Writer.TryWrite(frame);
            }
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

    private uint _lastSeenSequence;
    private bool _seenAnySequence;

    private void ObserveSequence(uint seq)
    {
        if (_seenAnySequence && seq > _lastSeenSequence)
        {
            long gap = (long)seq - (long)_lastSeenSequence - 1;
            if (gap > 0) Interlocked.Add(ref _droppedFrames, gap);
        }
        _seenAnySequence = true;
        _lastSeenSequence = seq;
    }

    // 4-phase rotation across the registers we currently own. Every phase
    // pairs the frequency register (ensuring sub-3ms QSY latency) with one
    // of Config / DriveFilter / Attenuator in turn. Attenuator needs a slot
    // or HL2 firmware never sees gain changes.
    //
    // When MOX is on we swap in a TX-flavored table: with duplex=1 always
    // (ControlFrame.cs Config C4[2]), HL2 needs TxFreq (0x02) continuously
    // or its TX mixer sits at power-on default (likely 0) and the PA sees
    // no drive. RxFreq stays in the rotation so demod during duplex TX
    // follows QSY, and TxFreq shows up in 2 of every 4 packets so a QSY
    // while keyed takes effect within a couple of ms. The RX VFO is reused for
    // TxFreq when Split/RIT are off, which matches what we do here since Zeus
    // has no separate TX VFO yet.
    internal static (ControlFrame.CcRegister first, ControlFrame.CcRegister second) PhaseRegisters(int phase, bool mox)
    {
        int p = phase & 0x3;
        if (mox)
        {
            return p switch
            {
                0 => (ControlFrame.CcRegister.TxFreq,     ControlFrame.CcRegister.RxFreq),
                1 => (ControlFrame.CcRegister.TxFreq,     ControlFrame.CcRegister.DriveFilter),
                2 => (ControlFrame.CcRegister.Attenuator, ControlFrame.CcRegister.TxFreq),
                _ => (ControlFrame.CcRegister.TxFreq,     ControlFrame.CcRegister.Config),
            };
        }
        return p switch
        {
            0 => (ControlFrame.CcRegister.Config,     ControlFrame.CcRegister.RxFreq),
            1 => (ControlFrame.CcRegister.RxFreq,     ControlFrame.CcRegister.DriveFilter),
            2 => (ControlFrame.CcRegister.Attenuator, ControlFrame.CcRegister.RxFreq),
            _ => (ControlFrame.CcRegister.RxFreq,     ControlFrame.CcRegister.Config),
        };
    }

    private async Task TxLoopAsync(CancellationToken ct)
    {
        var sock = _socket!;
        var remote = _remote!;
        var buf = new byte[ControlFrame.PacketLength];
        uint sendSeq = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TxTickIntervalMs));
        int phase = 0;

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var state = SnapshotState();
                var (first, second) = PhaseRegisters(phase, state.Mox);
                phase = (phase + 1) & 0x3;
                ControlFrame.BuildDataPacket(buf, sendSeq++, first, second, in state, _txIqSource);
                try
                {
                    await sock.SendToAsync(buf, SocketFlags.None, remote, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException ex)
                {
                    _log.LogWarning(ex, "TX SendTo failed; stopping TX loop.");
                    return;
                }
            }
        }
        catch (OperationCanceledException) { /* expected on stop */ }
    }

    private void SendStartStop(bool start)
    {
        if (_socket is null || _remote is null) return;
        Span<byte> buf = stackalloc byte[64];
        ControlFrame.BuildStartStop(buf, start);
        byte[] heap = buf.ToArray();
        // Send 3× on macOS (first-UDP-drop workaround). Harmless elsewhere.
        int sends = OperatingSystem.IsMacOS() ? 3 : 1;
        for (int i = 0; i < sends; i++)
        {
            try { _socket.SendTo(heap, _remote); }
            catch (SocketException ex) { _log.LogWarning(ex, "Start/stop send {I}/{N} failed", i + 1, sends); }
            if (sends > 1 && i < sends - 1) Thread.Sleep(30);
        }
    }

    private async Task DrainSocketAsync(TimeSpan drainFor)
    {
        if (_socket is null) return;
        var deadline = DateTime.UtcNow + drainFor;
        var scratch = new byte[PacketParser.PacketLength];
        var remote = (EndPoint)new IPEndPoint(IPAddress.Any, 0);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var result = await _socket.ReceiveFromAsync(scratch, SocketFlags.None, remote).WaitAsync(drainFor).ConfigureAwait(false);
                _ = result;
            }
            catch { break; }
        }
    }

    private static long NowNs() =>
        (long)(Stopwatch.GetTimestamp() * (1_000_000_000.0 / Stopwatch.Frequency));
}
