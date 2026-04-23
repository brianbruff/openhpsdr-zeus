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

using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Dsp;
using Zeus.Dsp.Wdsp;
using Zeus.Protocol1;

namespace Zeus.Server;

public class DspPipelineService : BackgroundService
{
    private const int Width = 2048;
    private const int SyntheticSampleRateHz = 192_000;
    private const int AudioOutputRateHz = 48_000;
    private const int AudioDrainCapacity = 2048;
    private static readonly TimeSpan TickPeriod = TimeSpan.FromMilliseconds(1000.0 / 30.0);

    private readonly RadioService _radio;
    private readonly StreamingHub _hub;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DspPipelineService> _log;

    private readonly object _engineLock = new();
    private IDspEngine? _engine;
    private int _channelId;
    private int _sampleRateHz;

    private Task? _iqPumpTask;
    private CancellationTokenSource? _iqPumpCts;

    // Protocol 2 path (parallel to the RadioService-owned P1 path). Held
    // directly here because RadioService is Protocol1Client-shaped and
    // growing a P2 variant there would require a larger refactor; for now
    // keeping it isolated avoids touching any P1 behavior.
    private Zeus.Protocol2.Protocol2Client? _p2Client;

    private RxMode _appliedMode = RxMode.USB;
    private int _appliedLowHz;
    private int _appliedHighHz;
    private double _appliedAgcTopDb;
    private NrConfig _appliedNr = new();
    private int _appliedZoomLevel = 1;

    private uint _seq;
    private uint _audioSeq;
    // RX S-meter broadcast throttle. Pipeline ticks at 30 Hz; broadcasting
    // every 6 ticks = 5 Hz gives a smoother meter than Thetis's 4 Hz baseline
    // without spamming the WS (30 Hz dBm readouts add nothing a UI can use).
    private int _rxMeterTickMod;
    private const int RxMeterTickModulus = 6;

    public DspPipelineService(RadioService radio, StreamingHub hub, ILoggerFactory loggerFactory)
    {
        _radio = radio;
        _hub = hub;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<DspPipelineService>();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        OpenSynthetic();
        _radio.Connected += OnRadioConnected;
        _radio.Disconnected += OnRadioDisconnected;
        _radio.StateChanged += OnRadioStateChanged;

        var panBuf = new float[Width];
        var wfBuf = new float[Width];
        var audioBuf = new float[AudioDrainCapacity];
        using var timer = new PeriodicTimer(TickPeriod);

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                Tick(panBuf, wfBuf, audioBuf);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _radio.Connected -= OnRadioConnected;
            _radio.Disconnected -= OnRadioDisconnected;
            _radio.StateChanged -= OnRadioStateChanged;
            await StopIqPumpAsync().ConfigureAwait(false);
            CloseCurrentEngine();
        }
    }

    public void SetMox(bool on)
    {
        IDspEngine? engine;
        lock (_engineLock) { engine = _engine; }
        // SyntheticDspEngine.SetMox is a no-op per the interface contract;
        // we still forward so the engine type stays opaque to TxService.
        engine?.SetMox(on);
    }

    public void SetTxTune(bool on)
    {
        IDspEngine? engine;
        lock (_engineLock) { engine = _engine; }
        engine?.SetTxTune(on);
    }

    /// <summary>Current engine snapshot (may be <see cref="SyntheticDspEngine"/>
    /// while disconnected). TxAudioIngest calls ProcessTxBlock on this; the
    /// engine handles a disposed-during-call race internally by returning 0.
    /// Virtual so tests can subclass this service and substitute a stub engine
    /// without running the full Synthetic/WDSP lifecycle.</summary>
    public virtual IDspEngine? CurrentEngine
    {
        get { lock (_engineLock) return _engine; }
    }

    private void OpenSynthetic()
    {
        var engine = new SyntheticDspEngine();
        int channelId = engine.OpenChannel(SyntheticSampleRateHz, Width);
        ApplyStateToNewChannel(engine, channelId);
        lock (_engineLock)
        {
            _engine = engine;
            _channelId = channelId;
            _sampleRateHz = SyntheticSampleRateHz;
        }
        _log.LogInformation("dsp.pipeline engine=synthetic channel={Id}", channelId);
    }

    private void OnRadioConnected(IProtocol1Client client)
    {
        var state = _radio.Snapshot();
        int rate = state.SampleRate;

        var wdsp = new WdspDspEngine(_loggerFactory.CreateLogger<WdspDspEngine>());
        int channelId = wdsp.OpenChannel(rate, Width);
        wdsp.OpenTxChannel();
        ApplyStateToNewChannel(wdsp, channelId);

        IDspEngine? old;
        int oldChannel;
        lock (_engineLock)
        {
            old = _engine;
            oldChannel = _channelId;
            _engine = wdsp;
            _channelId = channelId;
            _sampleRateHz = rate;
        }

        TeardownEngine(old, oldChannel);
        _log.LogInformation("dsp.pipeline engine=wdsp channel={Id} rate={Rate}", channelId, rate);

        StartIqPump(client);
    }

    private void OnRadioDisconnected()
    {
        StopIqPumpAsync().GetAwaiter().GetResult();

        var synth = new SyntheticDspEngine();
        int channelId = synth.OpenChannel(SyntheticSampleRateHz, Width);
        ApplyStateToNewChannel(synth, channelId);

        IDspEngine? old;
        int oldChannel;
        lock (_engineLock)
        {
            old = _engine;
            oldChannel = _channelId;
            _engine = synth;
            _channelId = channelId;
            _sampleRateHz = SyntheticSampleRateHz;
        }

        TeardownEngine(old, oldChannel);
        _log.LogInformation("dsp.pipeline engine=synthetic channel={Id}", channelId);
    }

    private void OnRadioStateChanged(StateDto s)
    {
        // Forward VFO changes to the P2 client when it's active. RadioService
        // does this for P1 via ActiveClient?.SetVfoAHz() inside SetVfo, but
        // ActiveClient is null for P2 connections, so the radio never learns
        // about tune changes without this forward. Sample rate / mode follow
        // here too when P2-side support is added.
        var p2 = _p2Client;
        p2?.SetVfoAHz(s.VfoHz);

        IDspEngine? engine;
        int channel;
        lock (_engineLock) { engine = _engine; channel = _channelId; }
        if (engine is null) return;

        if (s.Mode != _appliedMode)
        {
            engine.SetMode(channel, s.Mode);
            // Keep TXA modulator mode in sync with the RX side. On Synthetic
            // and before OpenTxChannel has run this is a no-op.
            engine.SetTxMode(s.Mode);
            _appliedMode = s.Mode;
        }
        if (s.FilterLowHz != _appliedLowHz || s.FilterHighHz != _appliedHighHz)
        {
            engine.SetFilter(channel, s.FilterLowHz, s.FilterHighHz);
            _appliedLowHz = s.FilterLowHz;
            _appliedHighHz = s.FilterHighHz;
        }
        if (s.AgcTopDb != _appliedAgcTopDb)
        {
            engine.SetAgcTop(channel, s.AgcTopDb);
            _appliedAgcTopDb = s.AgcTopDb;
        }
        var nr = s.Nr ?? new NrConfig();
        if (!nr.Equals(_appliedNr))
        {
            engine.SetNoiseReduction(channel, nr);
            _appliedNr = nr;
        }
        if (s.ZoomLevel != _appliedZoomLevel)
        {
            engine.SetZoom(channel, s.ZoomLevel);
            _appliedZoomLevel = s.ZoomLevel;
        }
    }

    private void ApplyStateToNewChannel(IDspEngine engine, int channelId)
    {
        var s = _radio.Snapshot();
        var nr = s.Nr ?? new NrConfig();
        engine.SetMode(channelId, s.Mode);
        // Sync TXA modulator with RX mode at engine-open time so the first
        // key-down lands with the correct sideband (no-op on Synthetic / pre-
        // OpenTxChannel).
        engine.SetTxMode(s.Mode);
        engine.SetFilter(channelId, s.FilterLowHz, s.FilterHighHz);
        engine.SetVfoHz(channelId, s.VfoHz);
        engine.SetAgcTop(channelId, s.AgcTopDb);
        engine.SetNoiseReduction(channelId, nr);
        engine.SetZoom(channelId, s.ZoomLevel);
        _appliedMode = s.Mode;
        _appliedLowHz = s.FilterLowHz;
        _appliedHighHz = s.FilterHighHz;
        _appliedAgcTopDb = s.AgcTopDb;
        _appliedNr = nr;
        _appliedZoomLevel = s.ZoomLevel;
    }

    private void StartIqPump(IProtocol1Client client)
    {
        var cts = new CancellationTokenSource();
        _iqPumpCts = cts;
        _iqPumpTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in client.IqFrames.ReadAllAsync(cts.Token).ConfigureAwait(false))
                {
                    IDspEngine? engine;
                    int channel;
                    lock (_engineLock) { engine = _engine; channel = _channelId; }
                    engine?.FeedIq(channel, frame.InterleavedSamples.Span);
                }
            }
            catch (OperationCanceledException) { }
            catch (ChannelClosedException) { }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "dsp.pipeline iq-pump exited with error");
            }
        }, cts.Token);
    }

    private void StartIqPumpP2(Zeus.Protocol2.Protocol2Client client)
    {
        var cts = new CancellationTokenSource();
        _iqPumpCts = cts;
        _iqPumpTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in client.IqFrames.ReadAllAsync(cts.Token).ConfigureAwait(false))
                {
                    IDspEngine? engine;
                    int channel;
                    lock (_engineLock) { engine = _engine; channel = _channelId; }
                    engine?.FeedIq(channel, frame.InterleavedSamples.Span);
                }
            }
            catch (OperationCanceledException) { }
            catch (ChannelClosedException) { }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "dsp.pipeline p2 iq-pump exited with error");
            }
        }, cts.Token);
    }

    /// <summary>
    /// Connect to a Protocol 2 radio and start streaming RX IQ into the DSP
    /// engine. Parallel path to RadioService.ConnectAsync (which is Protocol 1
    /// only); both swap the engine to WDSP and start a pump. Only one client
    /// at a time.
    /// </summary>
    public async Task ConnectP2Async(IPEndPoint radioEndpoint, int sampleRateKhz, byte numAdc, CancellationToken ct)
    {
        if (_p2Client is not null)
            throw new InvalidOperationException("Already connected (P2).");
        if (_radio.ActiveClient is not null)
            throw new InvalidOperationException("Already connected (P1). Disconnect first.");

        var client = new Zeus.Protocol2.Protocol2Client(
            _loggerFactory.CreateLogger<Zeus.Protocol2.Protocol2Client>());
        client.SetNumAdc(numAdc);
        await client.ConnectAsync(radioEndpoint, ct).ConfigureAwait(false);
        await client.StartAsync(sampleRateKhz, ct).ConfigureAwait(false);

        int rateHz = sampleRateKhz * 1000;
        IDspEngine newEngine;
        int newChannelId;
        try
        {
            var wdsp = new WdspDspEngine(_loggerFactory.CreateLogger<WdspDspEngine>());
            newChannelId = wdsp.OpenChannel(rateHz, Width);
            wdsp.OpenTxChannel();
            // Best-effort apply. Some local WDSP builds are missing newer
            // entry points (e.g. SetRXAEMNRpost2Run); the channel itself is
            // open and capable of spectrum work even if a noise-reduction
            // toggle can't be set. Narrow catch so a genuinely broken engine
            // still surfaces via the outer handler.
            try { ApplyStateToNewChannel(wdsp, newChannelId); }
            catch (EntryPointNotFoundException ex)
            {
                _log.LogWarning(ex, "dsp.pipeline p2 wdsp missing entry point — partial config applied");
            }
            newEngine = wdsp;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "dsp.pipeline p2 wdsp open failed, falling back to synthetic engine");
            var synth = new SyntheticDspEngine();
            newChannelId = synth.OpenChannel(rateHz, Width);
            try { ApplyStateToNewChannel(synth, newChannelId); }
            catch (EntryPointNotFoundException) { }
            newEngine = synth;
        }

        IDspEngine? old;
        int oldChannel;
        lock (_engineLock)
        {
            old = _engine;
            oldChannel = _channelId;
            _engine = newEngine;
            _channelId = newChannelId;
            _sampleRateHz = rateHz;
        }
        TeardownEngine(old, oldChannel);
        _log.LogInformation("dsp.pipeline p2 engine={Engine} rate={Rate}", newEngine.GetType().Name, rateHz);

        _p2Client = client;
        StartIqPumpP2(client);
        _radio.MarkProtocol2Connected(radioEndpoint.ToString(), rateHz);
    }

    public async Task DisconnectP2Async(CancellationToken ct)
    {
        var client = _p2Client;
        _p2Client = null;
        if (client is null) return;

        await StopIqPumpAsync().ConfigureAwait(false);
        try { await client.StopAsync(ct).ConfigureAwait(false); } catch { }
        await client.DisposeAsync().ConfigureAwait(false);

        var synth = new SyntheticDspEngine();
        int channelId = synth.OpenChannel(SyntheticSampleRateHz, Width);
        ApplyStateToNewChannel(synth, channelId);

        IDspEngine? old;
        int oldChannel;
        lock (_engineLock)
        {
            old = _engine;
            oldChannel = _channelId;
            _engine = synth;
            _channelId = channelId;
            _sampleRateHz = SyntheticSampleRateHz;
        }
        TeardownEngine(old, oldChannel);
        _radio.MarkProtocol2Disconnected();
        _log.LogInformation("dsp.pipeline p2 disconnected, engine=synthetic");
    }

    public Zeus.Protocol2.Protocol2Client? ActiveP2Client => _p2Client;

    private async Task StopIqPumpAsync()
    {
        var cts = _iqPumpCts;
        var task = _iqPumpTask;
        _iqPumpCts = null;
        _iqPumpTask = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        if (task is not null)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { /* logged at the source */ }
        }
        cts.Dispose();
    }

    private void CloseCurrentEngine()
    {
        IDspEngine? engine;
        int channel;
        lock (_engineLock)
        {
            engine = _engine;
            channel = _channelId;
            _engine = null;
            _channelId = 0;
        }
        TeardownEngine(engine, channel);
    }

    private static void TeardownEngine(IDspEngine? engine, int channelId)
    {
        if (engine is null) return;
        try { engine.CloseChannel(channelId); } catch { /* best-effort */ }
        engine.Dispose();
    }

    private void Tick(float[] panBuf, float[] wfBuf, float[] audioBuf)
    {
        IDspEngine? engine;
        int channel;
        int sampleRate;
        lock (_engineLock)
        {
            engine = _engine;
            channel = _channelId;
            sampleRate = _sampleRateHz;
        }
        if (engine is null) return;

        var state = _radio.Snapshot();
        // Synthetic engine stays open while disconnected so SetMode/SetFilter
        // etc. have somewhere to land, but its sweep+static placeholder used
        // to render a misleading "fake spectrum" before any radio existed.
        // Gate on the engine type rather than the connection status: status
        // flips to Connected before OnRadioConnected swaps the engine, and a
        // status-only check let one or two synthetic frames leak through that
        // race window — visible as a brief flash of the fake waterfall right
        // when the user clicked Connect. The synthetic engine never produces
        // real-radio data, so suppressing it unconditionally is correct.
        if (engine is SyntheticDspEngine) return;

        engine.SetVfoHz(channel, state.VfoHz);

        bool pan = engine.TryGetDisplayPixels(channel, DisplayPixout.Panadapter, panBuf);
        bool wf = engine.TryGetDisplayPixels(channel, DisplayPixout.Waterfall, wfBuf);

        // Flip to display order (low freq left, high freq right). WDSP emits
        // pixel 0 = highest positive frequency — see doc 03 §10 and
        // doc 08 §3 "Pixel axis reversal". SyntheticDspEngine already emits
        // in WDSP order so this reversal applies to both engines. Guarded by
        // the freshness flag: TryGetDisplayPixels leaves the buffer untouched
        // when no new FFT is ready, so an unconditional reverse would alternate
        // the orientation on every stale tick and broadcast mirrored garbage
        // (still flagged invalid, but bandwidth wasted and timing-sensitive).
        if (pan) Array.Reverse(panBuf);
        if (wf) Array.Reverse(wfBuf);

        var flags = DisplayBodyFlags.None;
        if (pan) flags |= DisplayBodyFlags.PanValid;
        if (wf) flags |= DisplayBodyFlags.WfValid;

        // Zoom narrows the analyzer's display span to sampleRate/level around
        // the VFO, so hzPerPixel shrinks by the same factor. Client re-uses
        // this for axis labels and planWaterfallUpdate horizontal shift — no
        // extra contract field needed, per task #7 scope note.
        int zoomLevel = Math.Max(1, state.ZoomLevel);
        float hzPerPixel = (float)((double)sampleRate / zoomLevel / Width);
        double nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var frame = new DisplayFrame(
            Seq: ++_seq,
            TsUnixMs: nowMs,
            RxId: 0,
            BodyFlags: flags,
            Width: Width,
            CenterHz: state.VfoHz,
            HzPerPixel: hzPerPixel,
            PanDb: panBuf,
            WfDb: wfBuf);

        _hub.Broadcast(frame);

        int audioSampleCount = engine.ReadAudio(channel, audioBuf);
        if (audioSampleCount > 0)
        {
            var audioFrame = new AudioFrame(
                Seq: ++_audioSeq,
                TsUnixMs: nowMs,
                RxId: 0,
                Channels: 1,
                SampleRateHz: (uint)AudioOutputRateHz,
                SampleCount: (ushort)audioSampleCount,
                Samples: new ReadOnlyMemory<float>(audioBuf, 0, audioSampleCount));
            _hub.Broadcast(audioFrame);
        }

        if (++_rxMeterTickMod >= RxMeterTickModulus)
        {
            _rxMeterTickMod = 0;
            // Prefer WDSP's calibrated S-meter when it's ticking. In this
            // integration the meter tap reads -400 ("didn't run") — needs
            // deeper WDSP state debugging to chase down. Until then, fall
            // back to RMS of the already-flowing post-demod audio ring, which
            // gives a "proof of life" meter that moves with band activity.
            double dbm = engine.GetRxaSignalDbm(channel);
            if (!double.IsFinite(dbm) || dbm <= -399.0)
            {
                // 0 dBFS audio ~= S9+ signal; calibrate against ambient band
                // noise later. Empirical offset of -50 dBm puts typical 20m
                // band noise near S2/S3 instead of pinning at S0.
                double rms = 0.0;
                if (audioSampleCount > 0)
                {
                    for (int i = 0; i < audioSampleCount; i++)
                    {
                        double v = audioBuf[i];
                        rms += v * v;
                    }
                    rms = Math.Sqrt(rms / audioSampleCount);
                }
                double dbfs = 20.0 * Math.Log10(Math.Max(rms, 1e-10));
                dbm = dbfs - 50.0; // rough uncalibrated conversion
            }
            if (!double.IsFinite(dbm)) dbm = -160.0;
            _hub.Broadcast(new RxMeterFrame((float)dbm));
        }
    }
}
