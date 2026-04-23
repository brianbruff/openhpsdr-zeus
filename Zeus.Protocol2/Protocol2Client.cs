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

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Zeus.Protocol2;

/// <summary>
/// Protocol 2 (OpenHPSDR "new protocol" / Thetis "ETH") streaming client.
/// Mirrors Zeus.Protocol1.Protocol1Client's lifecycle surface where it
/// overlaps; Protocol-1-only methods (HL2 dither, N2ADR filter board) are
/// absent here. Wire format verified against Thetis ChannelMaster network.c.
/// </summary>
public sealed class Protocol2Client : IDisposable, IAsyncDisposable
{
    private const int BufLen = 1444;
    private const int DiscoverySamplesPerPacket = 238;

    // On ANAN G2 MkII (Orion-II / Saturn) the first two DDC slots are wired
    // to the PureSignal / diversity feedback path. User-visible receivers
    // start at DDC2. pihpsdr's `new_protocol_receive_specific` and
    // `new_protocol_high_priority` both do `ddc = 2 + i` for these boards;
    // we follow the same convention. Radio then sends DDC2 IQ from port
    // 1035 + 2 = 1037.
    private const int G2RxDdc = 2;

    // 2^32 / 122_880_000 — converts Hz to a 32-bit phase-increment word
    // when the general packet is in "send phase word" mode (bit 3 of
    // CmdGeneral[37], which pihpsdr and Thetis both set).
    private const double HzToPhase = 34.952533333333333;

    private readonly ILogger<Protocol2Client> _log;
    private readonly Channel<IqFrame> _iqFrames = Channel.CreateUnbounded<IqFrame>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private Socket? _sock;
    private IPEndPoint? _radioEndpoint;
    private CancellationTokenSource? _rxCts;
    private Task? _rxTask;
    private Task? _keepaliveTask;
    private int _sampleRateKhz = 48;
    private uint _rxFreqHz = 14_200_000;
    private byte _numAdc = 2;
    // Mercury preamp defaults OFF — on a G2 the ADC has enough dynamic range
    // that the preamp is a crutch, not a default. Operator enables it when
    // needed via the UI. Attenuator 0 dB so the front-end isn't knocked down.
    private bool _preampOn;
    private byte _rxStepAttnDb;
    private long _totalFrames;
    private long _droppedFrames;
    private uint _lastDdc0Seq;
    private bool _haveFirstDdc0;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    // Per-stream sequence counters. The G2 firmware tracks seq per destination
    // port; sharing one counter across CmdGeneral/CmdRx/CmdTx/CmdHighPriority
    // makes the first HighPriority packet land at seq=2+, which the radio
    // treats as "stream started mid-flight" and silently drops — leaving the
    // DDC locked to whatever the previous client's last tune was.
    private uint _seqCmdGeneral;
    private uint _seqCmdRx;
    private uint _seqCmdTx;
    private uint _seqCmdHp;

    public Protocol2Client(ILogger<Protocol2Client> log)
    {
        _log = log;
    }

    public ChannelReader<IqFrame> IqFrames => _iqFrames.Reader;
    public long TotalFrames => Interlocked.Read(ref _totalFrames);
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    public Task ConnectAsync(IPEndPoint radioEndpoint, CancellationToken ct)
    {
        if (_sock is not null)
            throw new InvalidOperationException("Already connected.");

        _radioEndpoint = new IPEndPoint(radioEndpoint.Address, 1024);
        var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // Matched port convention — PC binds 1025, radio sends back with source
        // ports 1025/1026/1027/1035.. which we demux by fromaddr.
        sock.Bind(new IPEndPoint(IPAddress.Any, 1025));
        sock.ReceiveBufferSize = 1 << 20;
        _sock = sock;
        _log.LogInformation("p2.connect radio={Radio} localPort=1025", radioEndpoint.Address);
        return Task.CompletedTask;
    }

    public Task StartAsync(int sampleRateKhz, CancellationToken ct)
    {
        if (_sock is null || _radioEndpoint is null)
            throw new InvalidOperationException("Call ConnectAsync first.");
        if (_rxTask is not null)
            throw new InvalidOperationException("Already started.");

        _sampleRateKhz = sampleRateKhz;

        // Startup sequence matches Thetis SendStart() and Priapus/NextGenSDR:
        // CmdGeneral → CmdRx → CmdTx → CmdHighPriority(run=1). Skipping CmdTx
        // leaves the G2 MkII in a half-configured state where its BPF board
        // latches a random band instead of honouring CmdHighPriority filter
        // bits on subsequent tunes.
        SendCmdGeneral();
        Thread.Sleep(50);
        SendCmdRx();
        Thread.Sleep(50);
        SendCmdTx();
        Thread.Sleep(50);
        SendCmdHighPriority(run: true);

        _rxCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _rxTask = Task.Run(() => RxLoop(_rxCts.Token));
        _keepaliveTask = Task.Run(() => KeepaliveLoop(_rxCts.Token));
        _log.LogInformation("p2.start rate={Rate}kHz freq={Freq}Hz", _sampleRateKhz, _rxFreqHz);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_rxTask is null) return;

        SendCmdHighPriority(run: false);
        _rxCts?.Cancel();
        try { await _rxTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _rxTask = null;
        if (_keepaliveTask is not null)
        {
            try { await _keepaliveTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _keepaliveTask = null;
        }
        _rxCts?.Dispose();
        _rxCts = null;
        _iqFrames.Writer.TryComplete();
        _log.LogInformation("p2.stop totalFrames={Total} dropped={Drop}", _totalFrames, _droppedFrames);
    }

    public void SetVfoAHz(long hz)
    {
        _rxFreqHz = (uint)Math.Clamp(hz, 0L, uint.MaxValue);
        var running = _rxTask is not null;
        _log.LogInformation("p2.tune hz={Hz} running={Running} hpSeq={Seq}",
            _rxFreqHz, running, _seqCmdHp);
        if (running) SendCmdHighPriority(run: true);
    }

    public void SetSampleRateKhz(int rateKhz)
    {
        _sampleRateKhz = rateKhz;
        if (_rxTask is not null)
        {
            SendCmdRx();
        }
    }

    public void SetNumAdc(byte numAdc)
    {
        _numAdc = numAdc;
    }

    public void SetPreamp(bool on)
    {
        _preampOn = on;
        if (_rxTask is not null) SendCmdHighPriority(run: true);
    }

    public void SetAttenuator(int db)
    {
        _rxStepAttnDb = (byte)Math.Clamp(db, 0, 31);
        if (_rxTask is not null) SendCmdHighPriority(run: true);
    }

    private void SendCmdGeneral()
    {
        var p = new byte[60];
        WriteBeU32(p, 0, _seqCmdGeneral++);
        p[4] = 0x00;
        WriteBeU16(p, 5, 1025);
        WriteBeU16(p, 7, 1026);
        WriteBeU16(p, 9, 1027);
        WriteBeU16(p, 11, 1025);
        WriteBeU16(p, 13, 1028);
        WriteBeU16(p, 15, 1029);
        WriteBeU16(p, 17, 1035);
        WriteBeU16(p, 19, 1026);
        WriteBeU16(p, 21, 1027);
        p[23] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(24), 512);
        p[26] = 16;
        p[27] = 0;
        p[28] = 32;
        // Matches pihpsdr new_protocol_general for ORION2/SATURN hardware:
        // [37] bit 3 = phase-word mode (radio reads phase increments at the
        // DDC-frequency offsets, not raw Hz), [38] = hardware-timer enable,
        // [58] = PA enable, [59] = Alex0|Alex1 enable (0x03 is required on
        // MkII for the BPF board to honour the alex bits further down).
        p[37] = 0x08;
        p[38] = 0x01;
        p[58] = 0x01;
        p[59] = 0x03;
        _sock!.SendTo(p, new IPEndPoint(_radioEndpoint!.Address, 1024));
    }

    private void SendCmdRx()
    {
        var p = new byte[BufLen];
        WriteBeU32(p, 0, _seqCmdRx++);
        // Mirrors pihpsdr new_protocol_receive_specific for the MkII:
        //   n_adc = 2 (G2 has two physical ADCs), DDC2 enabled by bit 2 in
        //   the enable mask, and the DDC config block sits at 17 + 2*6 = 29.
        //   DDC0/1 stay disabled — those slots are reserved by the radio for
        //   the PureSignal/Diversity hardware pair.
        p[4] = 2;
        p[5] = 0;
        p[6] = 0;
        p[7] = (byte)(1 << G2RxDdc);  // = 0x04
        int off = 17 + G2RxDdc * 6;
        p[off + 0] = 0x00;            // DDC2 <- ADC0
        WriteBeU16(p, off + 1, _sampleRateKhz);
        p[off + 5] = 24;              // bit depth
        _sock!.SendTo(p, new IPEndPoint(_radioEndpoint!.Address, 1025));
    }

    private void SendCmdTx()
    {
        var p = new byte[60];
        WriteBeU32(p, 0, _seqCmdTx++);
        p[4] = 1;                 // num_dac
        WriteBeU16(p, 14, _sampleRateKhz);
        _sock!.SendTo(p, new IPEndPoint(_radioEndpoint!.Address, 1026));
    }

    private void SendCmdHighPriority(bool run)
    {
        var p = new byte[BufLen];
        WriteBeU32(p, 0, _seqCmdHp++);
        p[4] = (byte)(run ? 0x01 : 0x00);

        // Frequency field is a PHASE word (general[37] bit 3 set) — radio
        // reads a 32-bit phase increment, not Hz. pihpsdr computes this as
        //   phase = freq_hz * 2^32 / 122_880_000
        // The G2 MkII puts the user-visible RX0 at DDC slot 2, so the phase
        // goes to bytes 9 + 2*4 = 17..20. Bytes 9..16 (DDC0/1) are left as
        // zero per pihpsdr's non-PS non-diversity code path. TX DUC phase is
        // written to 329..332 as the same value for out-of-band gating.
        uint rxPhase = (uint)(_rxFreqHz * HzToPhase);
        WriteBeU32(p, 9 + G2RxDdc * 4, rxPhase);
        WriteBeU32(p, 329, rxPhase);

        // Mercury attenuator byte: bit 0 = RX0 preamp, bit 1 = RX1 preamp
        // (Thetis network.c:1037).
        p[1403] = (byte)(_preampOn ? 0x01 : 0x00);

        // ADC0 step attenuator (0-31 dB). Thetis network.c:1057.
        p[1443] = _rxStepAttnDb;

        // Alex words. Bit positions and BPF selections per pihpsdr's alex.h +
        // new_protocol.c (function new_protocol_high_priority, device cases
        // NEW_DEVICE_ORION2 / NEW_DEVICE_SATURN). Alex0 holds the RX BPF +
        // TX LPF + TX antenna; Alex1 duplicates for the RX path during TX.
        // Offsets: Alex0 at 1432..1435, Alex1 at 1428..1431.
        uint alex = ComputeAlexWord(_rxFreqHz, _rxFreqHz, txAnt: 1);
        WriteBeU32(p, 1428, alex);
        WriteBeU32(p, 1432, alex);
        _sock!.SendTo(p, new IPEndPoint(_radioEndpoint!.Address, 1027));
    }

    // ANAN-7000 / Orion-II / Saturn (G2 MkII) BPF board constants. Copied
    // verbatim from pihpsdr's alex.h — these are the RX BPF selections the
    // MkII's filter board expects. The older ALEX_*_HPF constants used for
    // ANAN-100 / classic Alex do NOT work on MkII — the filter board
    // silently selects "nothing" and all RF is cut off at the ADC.
    private const uint ALEX_ANAN7000_RX_BYPASS_BPF = 0x00001000;
    private const uint ALEX_ANAN7000_RX_160_BPF    = 0x00000040;
    private const uint ALEX_ANAN7000_RX_80_60_BPF  = 0x00000020;
    private const uint ALEX_ANAN7000_RX_40_30_BPF  = 0x00000010;
    private const uint ALEX_ANAN7000_RX_20_15_BPF  = 0x00000002;
    private const uint ALEX_ANAN7000_RX_12_10_BPF  = 0x00000004;
    private const uint ALEX_ANAN7000_RX_6_PRE_BPF  = 0x00000008;

    // TX LPF constants (used during TX; harmless during RX but worth setting
    // correctly so the BPF board stays in a sane latched state if the radio
    // momentarily T/Rs).
    private const uint ALEX_160_LPF        = 0x00800000;
    private const uint ALEX_80_LPF         = 0x00400000;
    private const uint ALEX_60_40_LPF      = 0x00200000;
    private const uint ALEX_30_20_LPF      = 0x00100000;
    private const uint ALEX_17_15_LPF      = 0x80000000;
    private const uint ALEX_12_10_LPF      = 0x40000000;
    private const uint ALEX_6_BYPASS_LPF   = 0x20000000;

    // TX antenna select.
    private const uint ALEX_TX_ANTENNA_1   = 0x01000000;
    private const uint ALEX_TX_ANTENNA_2   = 0x02000000;
    private const uint ALEX_TX_ANTENNA_3   = 0x04000000;

    internal static uint ComputeAlexWord(uint rxFreqHz, uint txFreqHz, int txAnt)
    {
        uint word = 0;
        word |= BpfBitsAnan7000(rxFreqHz);
        word |= LpfBitsAnan7000(txFreqHz);
        word |= txAnt switch
        {
            1 => ALEX_TX_ANTENNA_1,
            2 => ALEX_TX_ANTENNA_2,
            3 => ALEX_TX_ANTENNA_3,
            _ => ALEX_TX_ANTENNA_1,
        };
        return word;
    }

    // RX BPF band splits lifted from pihpsdr new_protocol.c
    // (function new_protocol_high_priority, ADC0 BPFfreq selection).
    internal static uint BpfBitsAnan7000(uint freqHz)
    {
        if (freqHz <  1_500_000u) return ALEX_ANAN7000_RX_BYPASS_BPF;
        if (freqHz <  2_100_000u) return ALEX_ANAN7000_RX_160_BPF;
        if (freqHz <  5_500_000u) return ALEX_ANAN7000_RX_80_60_BPF;
        if (freqHz < 11_000_000u) return ALEX_ANAN7000_RX_40_30_BPF;
        if (freqHz < 22_000_000u) return ALEX_ANAN7000_RX_20_15_BPF;
        if (freqHz < 35_000_000u) return ALEX_ANAN7000_RX_12_10_BPF;
        return ALEX_ANAN7000_RX_6_PRE_BPF;
    }

    // TX LPF band splits (pihpsdr calc_tx_alex in alex.c). Same band groupings
    // as the physical LPF board — not HPF-shaped.
    internal static uint LpfBitsAnan7000(uint freqHz)
    {
        if (freqHz >= 33_000_000u) return ALEX_6_BYPASS_LPF;
        if (freqHz >= 22_000_000u) return ALEX_12_10_LPF;
        if (freqHz >= 15_000_000u) return ALEX_17_15_LPF;
        if (freqHz >=  8_000_000u) return ALEX_30_20_LPF;
        if (freqHz >=  4_500_000u) return ALEX_60_40_LPF;
        if (freqHz >=  2_500_000u) return ALEX_80_LPF;
        return ALEX_160_LPF;
    }

    // Mirrors pihpsdr's new_protocol_timer_thread:
    //   HighPriority every 100 ms, RX/TX specific every 200 ms, General
    //   every 800 ms. The G2 MkII expects this cadence once the hardware
    //   watchdog is enabled in CmdGeneral[38] — without it the radio
    //   treats the stream as abandoned and freezes IQ within ~1 s.
    private async Task KeepaliveLoop(CancellationToken ct)
    {
        int cycle = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
                cycle = (cycle % 8) + 1;
                SendCmdHighPriority(run: true);
                switch (cycle)
                {
                    case 2: case 4: case 6:
                        SendCmdRx();
                        break;
                    case 1: case 3: case 5: case 7:
                        SendCmdTx();
                        break;
                    case 8:
                        SendCmdRx();
                        SendCmdGeneral();
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "p2.keepalive exited with error");
        }
    }

    private void RxLoop(CancellationToken ct)
    {
        var buf = new byte[2048];
        var sock = _sock!;
        sock.ReceiveTimeout = 500;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n;
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                try
                {
                    n = sock.ReceiveFrom(buf, ref from);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                {
                    continue;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted
                                              || ex.SocketErrorCode == SocketError.OperationAborted)
                {
                    break;
                }

                var srcPort = ((IPEndPoint)from).Port;
                if (srcPort >= 1035 && srcPort <= 1041 && n == BufLen)
                {
                    HandleDdcPacket(buf, srcPort - 1035);
                }
                // other ports (hi-pri status, mic, wideband) intentionally ignored for now
            }
        }
        finally
        {
            _iqFrames.Writer.TryComplete();
        }
    }

    private void HandleDdcPacket(byte[] buf, int ddcIndex)
    {
        var seq = BinaryPrimitives.ReadUInt32BigEndian(buf);
        if (ddcIndex == 0)
        {
            if (_haveFirstDdc0 && seq != _lastDdc0Seq + 1)
            {
                Interlocked.Increment(ref _droppedFrames);
            }
            _haveFirstDdc0 = true;
            _lastDdc0Seq = seq;
        }

        // 238 complex samples: I (int24 BE) + Q (int24 BE), starting at byte 16.
        // We own the array for the lifetime of the IqFrame the downstream
        // pump consumes; there's no back-channel to Return it to a pool, so
        // plain GC allocation is both simpler and correct.
        const int samplesPerPacket = DiscoverySamplesPerPacket;
        var samples = new double[samplesPerPacket * 2];
        const double scale = 1.0 / 8388608.0;
        for (int i = 0; i < samplesPerPacket; i++)
        {
            int off = 16 + i * 6;
            int iRaw = (buf[off] << 16) | (buf[off + 1] << 8) | buf[off + 2];
            if ((iRaw & 0x800000) != 0) iRaw |= unchecked((int)0xFF000000);
            int qRaw = (buf[off + 3] << 16) | (buf[off + 4] << 8) | buf[off + 5];
            if ((qRaw & 0x800000) != 0) qRaw |= unchecked((int)0xFF000000);
            samples[i * 2] = iRaw * scale;
            samples[i * 2 + 1] = qRaw * scale;
        }

        var frame = new IqFrame(
            InterleavedSamples: new ReadOnlyMemory<double>(samples, 0, samplesPerPacket * 2),
            SampleCount: samplesPerPacket,
            SampleRateHz: _sampleRateKhz * 1000,
            Sequence: seq,
            TimestampNs: _stopwatch.ElapsedTicks * 1_000_000_000L / Stopwatch.Frequency);

        Interlocked.Increment(ref _totalFrames);
        _iqFrames.Writer.TryWrite(frame);
    }

    public void Dispose()
    {
        try { StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
        _sock?.Dispose();
        _sock = null;
        _rxCts?.Dispose();
        _rxCts = null;
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        _sock?.Dispose();
        _sock = null;
        _rxCts?.Dispose();
        _rxCts = null;
    }

    private static void WriteBeU16(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)((value >> 8) & 0xff);
        buf[offset + 1] = (byte)(value & 0xff);
    }

    private static void WriteBeU32(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)((value >> 24) & 0xff);
        buf[offset + 1] = (byte)((value >> 16) & 0xff);
        buf[offset + 2] = (byte)((value >> 8) & 0xff);
        buf[offset + 3] = (byte)(value & 0xff);
    }
}
