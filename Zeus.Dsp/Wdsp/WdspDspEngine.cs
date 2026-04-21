using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;

namespace Zeus.Dsp.Wdsp;

public sealed class WdspDspEngine : IDspEngine
{
    // RXA: keep the 1024-sample window the panadapter / audio pipeline have
    // always used. Changing it broke RX audio entirely (regression observed
    // 2026-04-18). RXA OpenChannel uses RxaInSize / RxaDspSize.
    private const int RxaInSize = 1024;
    private const int RxaDspSize = 1024;

    // TXA: match RXA's 1024@48k window. The Thetis 64@96k profile was tried
    // on 2026-04-18 and produced ~0.6 W on TUN at 100% drive (target ~6 W),
    // so this branch (feature/tx_pwr2) restores the dcd3766 settings that
    // the operator confirmed produced rated power.
    private const int TxaInSize = 1024;
    private const int TxaDspSize = 1024;

    // Legacy aliases — RXA-side code still references these. Kept = RxaInSize
    // / RxaDspSize so existing callsites (audio outSamples math, channel
    // structs, etc.) don't have to change.
    private const int InSize = RxaInSize;
    private const int DspSize = RxaDspSize;
    private const int DspRate = 48_000;
    private const int OutputRate = 48_000;
    private const int MaxFftSize = 262_144;
    private const int AnalyzerFftSize = 16_384;
    private const int AnalyzerFps = 30;
    private const int AnalyzerWindow = 2;
    private const double AnalyzerKaiserPi = 14.0;
    private const double AnalyzerKeepTime = 0.1;

    private enum RxaMode
    {
        LSB = 0, USB = 1, DSB = 2, CWL = 3, CWU = 4,
        FM = 5, AM = 6, DIGU = 7, SPEC = 8, DIGL = 9,
        SAM = 10, DRM = 11,
    }

    // Audio ring holds ~1 s of mono float32 @ 48 kHz (producer: worker thread after fexchange0,
    // consumer: ReadAudio caller on pipeline thread). Drops oldest when over capacity.
    private const int AudioRingCapacity = OutputRate;

    private sealed class ChannelState
    {
        public required int Id;
        public required int SampleRateHz;
        public required int PixelWidth;
        public required int OutDoubles;
        public required Thread Worker;
        public required BlockingCollection<double[]> InQueue;
        public readonly ConcurrentQueue<double[]> FreeFrames = new();
        public double[] PartialFrame = new double[2 * InSize];
        public int PartialFill;
        public readonly object FillGate = new();
        public volatile bool Stopped;
        public CancellationTokenSource Cts = new();
        public int SpectrumRun = 1;
        public readonly float[] AudioRing = new float[AudioRingCapacity];
        public int AudioHead;
        public int AudioCount;
        public readonly object AudioGate = new();
        // Bandpass tracked as unsigned magnitudes so SetMode can re-sign per mode
        // (WDSP wants negative f_low/f_high for LSB-family, positive for USB-family).
        public int FilterLowAbsHz = 150;
        public int FilterHighAbsHz = 2850;
        public RxaMode CurrentMode = RxaMode.USB;
        // Thetis "AGC Top" max-gain setting in dB. 80 matches the Thetis
        // AGC_MEDIUM default; the /api/agcGain endpoint can override at runtime.
        public double AgcTopDb = 80.0;
        // Read by RunWorker to gate xanbEXT/xnobEXT; writes only from SetNoiseReduction.
        // Single-writer on the pipeline thread + word-sized read on the worker = safe
        // without a lock (worst case: one extra frame at the old setting on toggle).
        public volatile NbMode CurrentNbMode = NbMode.Off;
        // Zoom level (1/2/4/8). Changing it re-calls SetAnalyzer with shifted
        // fscLin/fscHin; the worker's Spectrum0 and the pixel drain's GetPixels
        // take this lock so they never interleave with an in-flight reconfig.
        public int ZoomLevel = 1;
        public readonly object AnalyzerLock = new();
    }

    private readonly ConcurrentDictionary<int, ChannelState> _channels = new();
    private readonly ILogger _log;
    private int _disposed;

    // TXA lifecycle is disjoint from RXA's (no analyzer, no audio ring, no NB)
    // so we don't register it in _channels. _txaLock serializes OpenTxChannel
    // vs SetMox vs teardown — all three are rare, so a plain lock is fine.
    private readonly object _txaLock = new();
    // Counter throttles fexchange2-error logging so a persistent wire-protocol
    // mismatch doesn't flood the log. First 8 errors are visible then suppressed.
    private int _txFexchangeErrLogged;
    private int? _txaChannelId;
    // Tracked so SetTxMode can re-sign bandpass bounds (LSB family wants negative,
    // USB family positive) the same way RXA does through ApplyBandpassForMode.
    private RxaMode _txCurrentMode = RxaMode.USB;
    // Latest per-stage TX peak meters, published atomically at the end of each
    // ProcessTxBlock. The reader (TxMetersService, 10 Hz during MOX) sees a
    // consistent snapshot without blocking the DSP thread. null until first TX
    // block runs or after TXA closes; GetTxStageMeters() returns
    // TxStageMeters.Silent in that case.
    private TxStageMeters? _latestTxStageMeters;
    private readonly object _txMeterPublishLock = new();

    public WdspDspEngine(ILogger<WdspDspEngine>? logger = null)
    {
        _log = logger ?? NullLogger<WdspDspEngine>.Instance;
        WdspNativeLoader.EnsureResolverRegistered();
        // WDSPwisdom is run by WdspWisdomInitializer at app startup before any
        // connect is allowed, so we trust it has completed by the time the
        // first OpenChannel lands here. Tests that construct the engine in
        // isolation can call WdspWisdomInitializer.EnsureInitializedAsync()
        // themselves, or accept slow first-open planning.
    }

    public int OpenChannel(int sampleRateHz, int pixelWidth)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (pixelWidth <= 0) throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        if (sampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));

        int id = 0;
        while (_channels.ContainsKey(id)) id++;

        int outSamples = (int)((long)InSize * OutputRate / sampleRateHz);
        int outDoubles = Math.Max(2, outSamples * 2);

        // Thetis pattern: open channel quiescent (state=0), apply all config,
        // then explicitly transition to state=1 with SetChannelState at the end.
        // Mirrors cmaster.c:80 (// initial state = 0) and rxa.cs:63
        // (WDSP.SetChannelState(chid + 0, 1, 0); // main rcvr ON). A fresh
        // channel opened at state=1 does set the exchange bit correctly
        // in-vitro, but runtime observation shows it can land clear — SAv/ADC
        // pin at -400 — suggesting the open→configure window allows the flag
        // to be stomped. Opening at 0 and flipping on last guarantees exchange
        // is set after all setters have run.
        NativeMethods.OpenChannel(
            channel: id,
            in_size: InSize,
            dsp_size: DspSize,
            input_samplerate: sampleRateHz,
            dsp_rate: DspRate,
            output_samplerate: OutputRate,
            type: 0,
            state: 0,
            tdelayup: 0.010,
            tslewup: 0.025,
            tdelaydown: 0.0,
            tslewdown: 0.010,
            bfo: 1);

        NativeMethods.SetRXABandpassWindow(id, 1);
        NativeMethods.SetRXABandpassRun(id, 1);
        NativeMethods.SetRXAAMDSBMode(id, 0);
        NativeMethods.SetRXAPanelRun(id, 1);
        // select=3 → route both I and Q into the panel. Without this WDSP
        // demodulates a single real-valued channel and can't separate sidebands
        // (LSB/USB become audibly identical mush).
        NativeMethods.SetRXAPanelSelect(id, 3);
        NativeMethods.SetRXAPanelBinaural(id, 0);
        NativeMethods.SetRXAPanelGain1(id, 1.0);
        NativeMethods.SetRXAMode(id, (int)RxaMode.USB);
        NativeMethods.SetRXABandpassFreqs(id, 150.0, 2850.0);
        NativeMethods.RXANBPSetFreqs(id, 150.0, 2850.0);
        NativeMethods.SetRXASNBAOutputBandwidth(id, 150.0, 2850.0);

        ApplyAgcDefaults(id);

        // Pre-RXA blankers: create run=0 so the setters / xanbEXT slots are
        // allocated before any SetNoiseReduction call touches them (EXT
        // setters deref panb[id]/pnob[id]). Create-time knob values are
        // passed through here too so the struct is self-consistent on return,
        // but the authoritative knob state comes from ApplyNbDefaults right
        // after — same approach a future advanced-NB panel will take.
        NativeMethods.CreateAnbEXT(
            id: id, run: 0, buffsize: InSize, samplerate: sampleRateHz,
            tau: NrDefaults.NbTau, hangtime: NrDefaults.NbHangtime,
            advtime: NrDefaults.NbAdvtime, backtau: NrDefaults.NbBacktau,
            threshold: NrDefaults.NbDefaultThresholdScaled);
        NativeMethods.CreateNobEXT(
            id: id, run: 0, mode: 0, buffsize: InSize, samplerate: sampleRateHz,
            slewtime: NrDefaults.NbTau, hangtime: NrDefaults.NbHangtime,
            advtime: NrDefaults.NbAdvtime, backtau: NrDefaults.NbBacktau,
            threshold: NrDefaults.NbDefaultThresholdScaled);
        ApplyNbDefaults(id);

        NativeMethods.XCreateAnalyzer(id, out int rc, MaxFftSize, 1, 1, null);
        if (rc != 0) throw new InvalidOperationException($"XCreateAnalyzer failed rc={rc}");

        ConfigureAnalyzer(id, sampleRateHz, pixelWidth, zoomLevel: 1);
        ConfigureDisplayAveraging(id);

        var state = new ChannelState
        {
            Id = id,
            SampleRateHz = sampleRateHz,
            PixelWidth = pixelWidth,
            OutDoubles = outDoubles,
            InQueue = new BlockingCollection<double[]>(boundedCapacity: 32),
            Worker = null!,
        };

        var worker = new Thread(() => RunWorker(state))
        {
            IsBackground = true,
            Name = $"WdspDsp-{id}",
            Priority = ThreadPriority.AboveNormal,
        };
        state.Worker = worker;

        _channels[id] = state;
        worker.Start();

        // Thetis rxa.cs:63 — "main rcvr ON". The OpenChannel call above used
        // state=0 so the slew.upflag / ch_upslew / exchange-bit initialisation
        // block in channel.c:94-99 did NOT run. SetChannelState(id, 1, 0) is
        // the canonical transition: it sets slew.upflag, ch_upslew, clears
        // exec_bypass, and sets exchange (channel.c:278-283). After this
        // returns, fexchange0's `if (_InterlockedAnd (&ch[channel].exchange, 1))`
        // guard (iobuffs.c:484) will be satisfied and xrxa → xmeter will run.
        NativeMethods.SetChannelState(id, 1, 0);

        return id;
    }

    public void CloseChannel(int channelId)
    {
        if (!_channels.TryRemove(channelId, out var state)) return;
        StopChannel(state);
    }

    public void FeedIq(int channelId, ReadOnlySpan<double> interleavedIqSamples)
    {
        if (!_channels.TryGetValue(channelId, out var state)) return;
        if (state.Stopped) return;

        int offset = 0;
        while (offset < interleavedIqSamples.Length)
        {
            lock (state.FillGate)
            {
                int need = state.PartialFrame.Length - state.PartialFill;
                int take = Math.Min(need, interleavedIqSamples.Length - offset);
                interleavedIqSamples.Slice(offset, take).CopyTo(state.PartialFrame.AsSpan(state.PartialFill));
                state.PartialFill += take;
                offset += take;

                if (state.PartialFill == state.PartialFrame.Length)
                {
                    double[] frame = state.PartialFrame;
                    if (!state.FreeFrames.TryDequeue(out var next))
                        next = new double[2 * InSize];
                    state.PartialFrame = next;
                    state.PartialFill = 0;
                    if (!state.InQueue.IsAddingCompleted)
                    {
                        try { state.InQueue.Add(frame); }
                        catch (InvalidOperationException) { state.FreeFrames.Enqueue(frame); }
                    }
                    else
                    {
                        state.FreeFrames.Enqueue(frame);
                    }
                }
            }
        }
    }

    public void SetMode(int channelId, RxMode mode)
    {
        if (!_channels.TryGetValue(channelId, out var state)) return;
        var mapped = MapMode(mode);
        NativeMethods.SetRXAMode(channelId, (int)mapped);
        state.CurrentMode = mapped;
        _log.LogInformation("wdsp.setMode channel={Id} mode={Mode}", channelId, mapped);
        ApplyBandpassForMode(state);
        // Drop up to ~1 s of already-demodulated audio queued with the old mode so
        // the user hears the new sideband immediately after clicking instead of
        // finishing the tail of the wrong one. AudioHead stays put; the read
        // position is derived from Head - Count, so zeroing Count is enough.
        lock (state.AudioGate) { state.AudioCount = 0; }
    }

    public void SetFilter(int channelId, int lowHz, int highHz)
    {
        if (!_channels.TryGetValue(channelId, out var state)) return;
        // Normalize to positive magnitudes; mode dictates the sign via ApplyBandpassForMode.
        int lo = Math.Abs(lowHz);
        int hi = Math.Abs(highHz);
        if (hi < lo) (lo, hi) = (hi, lo);
        state.FilterLowAbsHz = lo;
        state.FilterHighAbsHz = hi;
        ApplyBandpassForMode(state);
    }

    public void SetVfoHz(int channelId, long vfoHz)
    {
        // VFO lives in VfoService above Protocol1Client (doc 07 §1.5) — WDSP has no
        // tuner; frequency translation happens at the protocol seam.
    }

    public void SetAgcTop(int channelId, double topDb)
    {
        if (!_channels.TryGetValue(channelId, out var state)) return;
        state.AgcTopDb = topDb;
        NativeMethods.SetRXAAGCTop(channelId, topDb);
        _log.LogInformation("wdsp.setAgcTop channel={Id} topDb={TopDb:F1}", channelId, topDb);
    }

    public void SetZoom(int channelId, int level)
    {
        SyntheticDspEngine.ValidateZoomLevel(level);
        if (!_channels.TryGetValue(channelId, out var state)) return;
        // Analyzer reconfig can race with Spectrum0 (worker) and GetPixels
        // (pipeline tick); the lock is the simpler option of the two team-lead
        // flagged. Briefly holds both producer and consumer while WDSP rebuilds
        // its bin mapping. Clients may still see one transient frame on the
        // wire — the averaging recovers in ~tau (≈100 ms) after the switch.
        lock (state.AnalyzerLock)
        {
            if (state.ZoomLevel == level) return;
            state.ZoomLevel = level;
            ConfigureAnalyzer(channelId, state.SampleRateHz, state.PixelWidth, level);
        }
        _log.LogInformation("wdsp.setZoom channel={Id} level={Level}", channelId, level);
    }

    public void SetNoiseReduction(int channelId, NrConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (!_channels.TryGetValue(channelId, out var state)) return;

        // Mutually-exclusive NR button. When switching to a mode, re-apply its
        // Thetis defaults before toggling Run=1 — matches Thetis setup.cs order
        // (configure, then enable) and keeps "toggle off then back on" at parity
        // even if a future caller changes the knobs between toggles.
        switch (cfg.NrMode)
        {
            case NrMode.Anr:
                NativeMethods.SetRXAEMNRRun(channelId, 0);
                NativeMethods.SetRXAANRVals(channelId, NrDefaults.AnrTaps, NrDefaults.AnrDelay, NrDefaults.AnrGain, NrDefaults.AnrLeakage);
                NativeMethods.SetRXAANRPosition(channelId, NrDefaults.Position);
                NativeMethods.SetRXAANRRun(channelId, 1);
                break;
            case NrMode.Emnr:
                NativeMethods.SetRXAANRRun(channelId, 0);
                NativeMethods.SetRXAEMNRgainMethod(channelId, NrDefaults.EmnrGainMethod);
                NativeMethods.SetRXAEMNRnpeMethod(channelId, NrDefaults.EmnrNpeMethod);
                NativeMethods.SetRXAEMNRaeRun(channelId, NrDefaults.EmnrAeRun);
                NativeMethods.SetRXAEMNRPosition(channelId, NrDefaults.Position);
                // post2 comfort-noise injection. emnr.c:981–1023 generates a
                // smoothed noise floor that masks residual EMNR warble — the
                // psychoacoustic mechanism behind Thetis's noticeably smoother
                // NR2 hiss. Configure params before flipping post2Run, then
                // Run=1 below activates EMNR with post2 already armed.
                NativeMethods.SetRXAEMNRpost2Factor(channelId, NrDefaults.EmnrPost2Factor);
                NativeMethods.SetRXAEMNRpost2Nlevel(channelId, NrDefaults.EmnrPost2Nlevel);
                NativeMethods.SetRXAEMNRpost2Rate(channelId, NrDefaults.EmnrPost2Rate);
                NativeMethods.SetRXAEMNRpost2Taper(channelId, NrDefaults.EmnrPost2Taper);
                NativeMethods.SetRXAEMNRpost2Run(channelId, NrDefaults.EmnrPost2Run);
                NativeMethods.SetRXAEMNRRun(channelId, 1);
                break;
            default:
                NativeMethods.SetRXAANRRun(channelId, 0);
                NativeMethods.SetRXAEMNRpost2Run(channelId, 0);
                NativeMethods.SetRXAEMNRRun(channelId, 0);
                break;
        }

        if (cfg.AnfEnabled)
        {
            NativeMethods.SetRXAANFVals(channelId, NrDefaults.AnfTaps, NrDefaults.AnfDelay, NrDefaults.AnfGain, NrDefaults.AnfLeakage);
            NativeMethods.SetRXAANFPosition(channelId, NrDefaults.Position);
            NativeMethods.SetRXAANFRun(channelId, 1);
        }
        else
        {
            NativeMethods.SetRXAANFRun(channelId, 0);
        }

        NativeMethods.SetRXASNBARun(channelId, cfg.SnbEnabled ? 1 : 0);
        NativeMethods.RXANBPSetNotchesRun(channelId, cfg.NbpNotchesEnabled ? 1 : 0);

        // Mutually-exclusive pre-RXA blanker. Update threshold on whichever
        // path we're about to run (or both paths when switching off → on → the
        // dormant side keeps a stale value, harmless while its Run=0). UI slider
        // is 0..100; Thetis multiplies by 0.165 before passing to WDSP.
        double scaledThreshold = cfg.NbThreshold * NrDefaults.NbThresholdScale;
        switch (cfg.NbMode)
        {
            case NbMode.Nb1:
                NativeMethods.SetEXTNOBRun(channelId, 0);
                NativeMethods.SetEXTANBThreshold(channelId, scaledThreshold);
                NativeMethods.SetEXTANBRun(channelId, 1);
                break;
            case NbMode.Nb2:
                NativeMethods.SetEXTANBRun(channelId, 0);
                NativeMethods.SetEXTNOBThreshold(channelId, scaledThreshold);
                NativeMethods.SetEXTNOBRun(channelId, 1);
                break;
            default:
                NativeMethods.SetEXTANBRun(channelId, 0);
                NativeMethods.SetEXTNOBRun(channelId, 0);
                break;
        }

        // RunWorker gate. Toggled after the Run flags above so the worker
        // doesn't call xanbEXT/xnobEXT between "dispatch starts running NB1"
        // and "we remember we're NB1 mode" — same reason SetNoiseReduction
        // runs Run=0 on the other side before Run=1 on this side.
        state.CurrentNbMode = cfg.NbMode;

        _log.LogInformation(
            "wdsp.setNoiseReduction channel={Id} nr={Nr} anf={Anf} snb={Snb} notches={Notches} nb={Nb} thr={Thr:F2}",
            channelId, cfg.NrMode, cfg.AnfEnabled, cfg.SnbEnabled, cfg.NbpNotchesEnabled,
            cfg.NbMode, scaledThreshold);
    }

    // Post-RXA NR defaults — sourced from Thetis setup.designer.cs + radio.cs.
    // UI-space scaling (gain × 1e-6, leakage × 1e-3) is already resolved: these
    // are the post-scale values WDSP actually receives. See docs/prd/10-noise-reduction.md.
    private static class NrDefaults
    {
        public const int AnrTaps = 64;
        public const int AnrDelay = 16;
        public const double AnrGain = 1e-4;
        public const double AnrLeakage = 0.1;
        public const int AnfTaps = 64;
        public const int AnfDelay = 16;
        public const double AnfGain = 1e-4;
        public const double AnfLeakage = 0.1;
        public const int EmnrGainMethod = 2;
        public const int EmnrNpeMethod = 0;
        public const int EmnrAeRun = 1;
        public const int Position = 1;

        // post2 defaults sourced from Thetis radio.cs slider initialisation
        // and emnr.c:1026–1056 parameter ranges. UI sliders (factor/nlevel/
        // taper) are 0..100 in Thetis and divided by 100 before SetRXA*; the
        // 15-on-the-slider Thetis defaults arrive at WDSP as 0.15 / 12. See
        // docs/prd/11-nr2-gap-plan.md §2a.
        public const int EmnrPost2Run = 1;
        public const double EmnrPost2Factor = 0.15;
        public const double EmnrPost2Nlevel = 0.15;
        public const double EmnrPost2Rate = 5.0;
        public const int EmnrPost2Taper = 12;

        // NB1/NB2 runtime-steady-state params — what Thetis actually runs with
        // once radio.cs's NB property setters have fired (tau=advtime=hangtime
        // = 5e-5, threshold = 0.165 × UI=20 = 3.3). backtau has no property
        // setter in Thetis, so it keeps cmaster.c's create-time value of 0.05.
        // Applied through Set* setters post-create (see ApplyNbDefaults) so
        // a future advanced-NB panel can reuse the same code path.
        public const double NbTau = 5e-5;
        public const double NbHangtime = 5e-5;
        public const double NbAdvtime = 5e-5;
        public const double NbBacktau = 0.05;
        public const double NbThresholdScale = 0.165;
        public const double NbDefaultThresholdScaled = 3.3;
    }

    public int ReadAudio(int channelId, Span<float> output)
    {
        if (!_channels.TryGetValue(channelId, out var state))
        {
            output.Clear();
            return 0;
        }

        lock (state.AudioGate)
        {
            int n = Math.Min(output.Length, state.AudioCount);
            if (n == 0) return 0;

            int tail = (state.AudioHead - state.AudioCount + AudioRingCapacity) % AudioRingCapacity;
            int firstChunk = Math.Min(n, AudioRingCapacity - tail);
            state.AudioRing.AsSpan(tail, firstChunk).CopyTo(output);
            int remainder = n - firstChunk;
            if (remainder > 0)
                state.AudioRing.AsSpan(0, remainder).CopyTo(output.Slice(firstChunk));

            state.AudioCount -= n;
            return n;
        }
    }

    private static void PushAudio(ChannelState state, ReadOnlySpan<double> interleavedStereo, int monoSampleCount)
    {
        lock (state.AudioGate)
        {
            for (int i = 0; i < monoSampleCount; i++)
            {
                // interleavedStereo is [L0, R0, L1, R1, ...]; take the left channel as mono.
                state.AudioRing[state.AudioHead] = (float)interleavedStereo[i * 2];
                state.AudioHead = (state.AudioHead + 1) % AudioRingCapacity;
                if (state.AudioCount < AudioRingCapacity)
                    state.AudioCount++;
                // Otherwise the oldest sample has been overwritten — head advance already did it.
            }
        }
    }

    // Thetis rxaMeterType.RXA_S_AV = 1 (console/dsp.cs:876-884) — average
    // signal strength in dBm, smoothed by WDSP's internal meter tau. Returns
    // a large negative (~−200) before any frame has been exchanged.
    private const int RxaMeterSAv = 1;

    // HL2 S-meter calibration offset in dB. Thetis
    // clsHardwareSpecific.cs:428 — RXMeterCalbrationOffsetDefaults default
    // branch returns 0.98f for non-ANAN models (HL2 falls here). Added to
    // GetRXAMeter output before exposing as dBm.
    private const double Hl2MeterCalOffsetDb = 0.98;

    private DateTime _lastRxMeterLogUtc;
    public double GetRxaSignalDbm(int channelId)
    {
        if (!_channels.ContainsKey(channelId)) return -200.0;
        double sAv = NativeMethods.GetRXAMeter(channelId, RxaMeterSAv);
        // Debug aid: if S_AV reads the "meter-didn't-run" sentinel (-400),
        // fall through to ADC_AV (index 3) which runs earlier in xrxa and
        // tells us whether the pipeline is exchanging at all. Pass the raw
        // value through on the sentinel path so the caller's `<= -399.0`
        // check still fires instead of being shifted by the cal.
        if (sAv <= -399.0)
        {
            double adcAv = NativeMethods.GetRXAMeter(channelId, 3);
            _log.LogInformation("wdsp.getRxaMeter sAv={SAv:F1} adcAv={AdcAv:F1} (sentinel)", sAv, adcAv);
            return sAv;
        }
        // Diagnostic 2026-04-18: log the live sAv at 1 Hz so we can see RX
        // signal level over time and pinpoint when it dies (e.g. after
        // MOX-on/off transition). Extended to read all four wcp-AGC indices —
        // smeter sits BEFORE the AGC stage in xrxa (RXA.c:645 vs 662), so if
        // sAv is -400 but agcAv/agcGain are real, the chain is alive through
        // AGC and the dead zone is between adcmeter and smeter (xbpsnbain or
        // xnbp). Conversely, if all are -400, xrxa itself is not running.
        var now = DateTime.UtcNow;
        if (now - _lastRxMeterLogUtc >= TimeSpan.FromSeconds(1))
        {
            _lastRxMeterLogUtc = now;
            // Indices per WDSP RXA.h:47-57 enum rxaMeterType.
            double adcAv = NativeMethods.GetRXAMeter(channelId, 3);   // RXA_ADC_AV
            double agcGain = NativeMethods.GetRXAMeter(channelId, 4); // RXA_AGC_GAIN
            double agcAv = NativeMethods.GetRXAMeter(channelId, 6);   // RXA_AGC_AV
            _log.LogInformation(
                "wdsp.rx.meter sAv={SAv:F1} adcAv={AdcAv:F1} agcGain={AgcGain:F1} agcAv={AgcAv:F1}",
                sAv, adcAv, agcGain, agcAv);
        }
        return sAv + Hl2MeterCalOffsetDb;
    }

    public bool TryGetDisplayPixels(int channelId, DisplayPixout which, Span<float> dbOut)
    {
        if (!_channels.TryGetValue(channelId, out var state)) return false;
        if (dbOut.Length != state.PixelWidth)
            throw new ArgumentException($"expected span of {state.PixelWidth}", nameof(dbOut));

        lock (state.AnalyzerLock)
        {
            NativeMethods.GetPixels(channelId, (int)which, ref MemoryMarshal.GetReference(dbOut), out int flag);
            return flag == 1;
        }
    }

    public int OpenTxChannel()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        lock (_txaLock)
        {
            if (_txaChannelId is int existing) return existing;

            // TXA id must not collide with any RXA id — pick the first free slot
            // past the current RXA allocation. WDSP doesn't care about id
            // ordering, it just uses the int as an index into its channel table.
            int id = 0;
            while (_channels.ContainsKey(id) || id == _txaChannelId) id++;

            // TXA matches the RXA OpenChannel shape — tdelay defaults identical
            // — differing only in `type: 1` (TX) and `state: 0` (stays quiescent
            // until SetMox(true)). Sample rates all 48 kHz: P1 EP2 uplink is
            // 48 kHz, TXA runs at 48 kHz internally, output returns to 48 kHz
            // for the EP2 packer. This is the dcd3766 configuration that the
            // operator confirmed produced rated TX power.
            NativeMethods.OpenChannel(
                channel: id,
                in_size: TxaInSize,
                dsp_size: TxaDspSize,
                input_samplerate: 48_000,
                dsp_rate: 48_000,
                output_samplerate: 48_000,
                type: 1,
                state: 0,
                tdelayup: 0.010,
                tslewup: 0.025,
                tdelaydown: 0.0,
                tslewdown: 0.010,
                bfo: 1);

            // SSB USB default + 150-2850 passband: wider than the classic SSB
            // 300-2700 to keep low-frequency voice energy through the chain
            // (task C.0 spec). Phase-C mic ingest drives fexchange2 once
            // SetMox(true) flips the TXA state to 1; until then the TXA sits
            // at state=0 and consumes nothing.
            NativeMethods.SetTXAMode(id, (int)RxaMode.USB);
            _txCurrentMode = RxaMode.USB;
            NativeMethods.SetTXABandpassFreqs(id, 150.0, 2850.0);
            NativeMethods.SetTXABandpassWindow(id, 1);
            // Intentionally NOT calling SetTXABandpassRun(id, 1): despite the
            // name it sets bp1.run (the compressor-only aux bandpass), not bp0,
            // and bp1 ships with stale LSB-direction coefs that reject the USB
            // mic on first MOX — that's the "TX 0 W until mode toggle" symptom
            // the operator saw return after this branch first restored the call.
            // bp0 is always on from create_bandpass; nothing to enable here.
            NativeMethods.SetTXAPanelRun(id, 1);
            NativeMethods.SetTXAPanelGain1(id, 1.0);

            // Explicit clean-slate TX chain state. WDSP initializes these
            // "off" at channel-create, but asserting them makes the baseline
            // deterministic and independent of the library build. Leveler
            // and Compressor are Thetis-factory-ON per its database profile,
            // but we keep them OFF here until they're wired to operator UI
            // and tuned — enabling them with library-default parameters can
            // mask or create new distortion on its own. ALC stays on (see
            // SetTXAALCSt below; never 0). AMSQ is the mic noise gate and
            // shouldn't shape SSB audio.
            NativeMethods.SetTXALevelerSt(id, 0);
            NativeMethods.SetTXACompressorRun(id, 0);
            NativeMethods.SetTXACFCOMPRun(id, 0);
            NativeMethods.SetTXAPHROTRun(id, 0);
            NativeMethods.SetTXAosctrlRun(id, 0);
            NativeMethods.SetTXAEQRun(id, 0);
            NativeMethods.SetTXAAMSQRun(id, 0);
            NativeMethods.SetTXAALCSt(id, 1);

            _txaChannelId = id;
            _log.LogInformation(
                "wdsp.openTxChannel id={Id} chain=[alc=1 lvlr=0 cpdr=0 cfc=0 phrot=0 osctrl=0 eq=0 amsq=0] bp=150..2850 panelGain=1.0",
                id);
            return id;
        }
    }

    public void SetMox(bool moxOn)
    {
        if (_disposed != 0) return;

        int txaId;
        int rxaId;
        lock (_txaLock)
        {
            if (_txaChannelId is not int txa) return;
            txaId = txa;

            // v0.1 always has exactly one RXA open; take the first key. If
            // there's no RXA (shouldn't happen in practice) the SetMox call is
            // meaningless — bail without touching TXA so we don't desync state.
            int? rxa = null;
            foreach (var key in _channels.Keys) { rxa = key; break; }
            if (rxa is not int r) return;
            rxaId = r;
        }

        // Thetis console.cs:31375/31387/31409 orders the transitions so the
        // outgoing side is damped (dmp=1) before the incoming side comes up
        // clean (dmp=0) — avoids a pop from the demuted side catching an
        // in-flight buffer.
        int rxaPrior, txaPrior;
        if (moxOn)
        {
            rxaPrior = NativeMethods.SetChannelState(rxaId, 0, 1);
            txaPrior = NativeMethods.SetChannelState(txaId, 1, 0);
            // No priming: Thetis (console.cs:31375) does not prime — bfo=1
            // semantics already make the first fexchange wait for real output.
        }
        else
        {
            txaPrior = NativeMethods.SetChannelState(txaId, 0, 1);
            rxaPrior = NativeMethods.SetChannelState(rxaId, 1, 0);
            // Unkeying: clear the stage-meter snapshot so UI doesn't latch the
            // last-during-TX reading while idle. The next MOX-on will publish
            // fresh data on its first ProcessTxBlock.
            lock (_txMeterPublishLock) { _latestTxStageMeters = null; }
        }
        // Diagnostic 2026-04-18: capture the prior-state return of every
        // SetChannelState call so we can detect cases where the requested
        // transition was a no-op (prior == new) — that's the failure mode that
        // looks like "RX audio doesn't come back after MOX-off".
        _log.LogInformation(
            "wdsp.setMox on={Mox} rxa={Rxa} (prior {RxaPrior}) txa={Txa} (prior {TxaPrior})",
            moxOn, rxaId, rxaPrior, txaId, txaPrior);
    }

    public TxStageMeters GetTxStageMeters()
    {
        lock (_txMeterPublishLock)
        {
            return _latestTxStageMeters ?? TxStageMeters.Silent;
        }
    }

    public int TxBlockSamples => TxaInSize;

    public void SetTxPanelGain(double linearGain)
    {
        if (_disposed != 0) return;
        lock (_txaLock)
        {
            if (_txaChannelId is not int txa) return;
            NativeMethods.SetTXAPanelGain1(txa, linearGain);
        }
        _log.LogInformation("wdsp.setTxPanelGain linear={Gain:F3}", linearGain);
    }

    public void SetTxTune(bool on)
    {
        if (_disposed != 0) return;
        lock (_txaLock)
        {
            if (_txaChannelId is not int txa) return;
            // Thetis console.cs:31806-31829 (chkTUN_CheckedChanged, non-pulse
            // branch): mode=0 single tone at ±cw_pitch offset, mag=MAX_TONE_MAG.
            // wdsp/gen.c:221-241: mode 0 = tone, mode 1 = two-tone (summed).
            // Two-tone produces a difference-frequency beat envelope, which
            // shows up on the forward-power meter as jitter — that's why the
            // old mode=1 reading "jumped like Parkinson's". The tone is offset
            // by cw_pitch so it lands in the TXA sideband passband set by
            // ApplyTxBandpassForMode (a 0 Hz tone sits on the suppressed-carrier
            // null for SSB). Sign mirrors Thetis's sideband rule.
            if (on)
            {
                const double cwPitch = 600.0;
                const double toneMag = 0.99999;
                double freq = _txCurrentMode switch
                {
                    RxaMode.LSB or RxaMode.CWL or RxaMode.DIGL => -cwPitch,
                    _ => +cwPitch,
                };
                NativeMethods.SetTXAPostGenMode(txa, 0);
                NativeMethods.SetTXAPostGenToneFreq(txa, freq);
                NativeMethods.SetTXAPostGenToneMag(txa, toneMag);
                NativeMethods.SetTXAPostGenRun(txa, 1);
                _log.LogInformation("wdsp.setTxTune on=true mode={Mode} freq={Freq:F0} mag={Mag:F5}", _txCurrentMode, freq, toneMag);
            }
            else
            {
                NativeMethods.SetTXAPostGenRun(txa, 0);
                _log.LogInformation("wdsp.setTxTune on=false");
            }
        }
    }

    public void SetTxMode(RxMode mode)
    {
        if (_disposed != 0) return;
        var mapped = MapMode(mode);
        lock (_txaLock)
        {
            if (_txaChannelId is not int txa) return;
            NativeMethods.SetTXAMode(txa, (int)mapped);
            _txCurrentMode = mapped;
            ApplyTxBandpassForMode(txa, mapped);
        }
        _log.LogInformation("wdsp.setTxMode mode={Mode}", mapped);
    }

    private DateTime _lastTxMeterLogUtc;

    public int ProcessTxBlock(ReadOnlySpan<float> micMono, Span<float> iqInterleaved)
    {
        if (_disposed != 0) return 0;
        if (micMono.Length != TxaInSize)
            throw new ArgumentException($"expected mic span of {TxaInSize}", nameof(micMono));
        if (iqInterleaved.Length != 2 * TxaInSize)
            throw new ArgumentException($"expected iq span of {2 * TxaInSize}", nameof(iqInterleaved));

        int txa;
        lock (_txaLock)
        {
            if (_txaChannelId is not int id) return 0;
            txa = id;
        }

        // fexchange2 wants mutable refs to the first float of each buffer. Copy
        // the mic ReadOnlySpan into a scratch array so we can pass a ref, and
        // stack-allocate Qin as silence and the output I/Q buffers. 1024 floats
        // × 4 buffers = 16 KiB, well inside the default stack budget.
        Span<float> iin = stackalloc float[TxaInSize];
        micMono.CopyTo(iin);
        Span<float> qin = stackalloc float[TxaInSize];
        qin.Clear();
        Span<float> iout = stackalloc float[TxaInSize];
        Span<float> qout = stackalloc float[TxaInSize];

        NativeMethods.fexchange2(txa, ref iin[0], ref qin[0], ref iout[0], ref qout[0], out int err);
        if (err != 0 && ++_txFexchangeErrLogged <= 8)
        {
            _log.LogWarning("wdsp.fexchange2 tx err={Err} (suppressed after 8 occurrences)", err);
        }

        for (int i = 0; i < TxaInSize; i++)
        {
            iqInterleaved[2 * i] = iout[i];
            iqInterleaved[2 * i + 1] = qout[i];
        }

        // Per-stage TXA peak meters. Peak (not average) is what surfaces
        // clipping-induced crackle — averages smooth a transient peak over
        // the ~100 ms meter window and hide the very thing we're chasing.
        // Indices per native/wdsp/TXA.h:49-66 txaMeterType.
        //   MIC_PK=0   EQ_PK=2   LVLR_PK=4  LVLR_GAIN=6
        //   CFC_PK=7   CFC_GAIN=9 COMP_PK=10 ALC_PK=12  ALC_GAIN=14  OUT_PK=15
        double micPk = NativeMethods.GetTXAMeter(txa, 0);
        double eqPk = NativeMethods.GetTXAMeter(txa, 2);
        double lvlrPk = NativeMethods.GetTXAMeter(txa, 4);
        double alcPk = NativeMethods.GetTXAMeter(txa, 12);
        double alcGain = NativeMethods.GetTXAMeter(txa, 14);
        double outPk = NativeMethods.GetTXAMeter(txa, 15);

        // Publish the snapshot before returning so pollers don't see a
        // partially-written set. Lock is uncontended in steady state —
        // ProcessTxBlock runs from the TX ingest thread and GetTxStageMeters
        // only from TxMetersService (10 Hz).
        var snap = new TxStageMeters(
            EqPk: (float)eqPk,
            LvlrPk: (float)lvlrPk,
            AlcPk: (float)alcPk,
            AlcGr: (float)alcGain,
            OutPk: (float)outPk);
        lock (_txMeterPublishLock) { _latestTxStageMeters = snap; }

        var now = DateTime.UtcNow;
        if (now - _lastTxMeterLogUtc >= TimeSpan.FromSeconds(1))
        {
            _lastTxMeterLogUtc = now;
            double micBlockPeak = 0, ioutPeak = 0;
            for (int i = 0; i < TxaInSize; i++)
            {
                double m = Math.Abs(iin[i]); if (m > micBlockPeak) micBlockPeak = m;
                double oi = Math.Abs(iout[i]); double oq = Math.Abs(qout[i]);
                double ma = Math.Max(oi, oq); if (ma > ioutPeak) ioutPeak = ma;
            }
            _log.LogInformation(
                "wdsp.tx.stage micBlockPeak={MP:F3} iqBlockPeak={IP:F4} | mic={Mic:F1} eq={Eq:F1} lvlr={Lvlr:F1} alc={Alc:F1} alcGr={AlcG:F1} out={Out:F1}",
                micBlockPeak, ioutPeak, micPk, eqPk, lvlrPk, alcPk, alcGain, outPk);
        }
        return TxaInSize;
    }

    // Mirror of ApplyBandpassForMode for the TX side. TXA has one bandpass
    // (SetTXABandpassFreqs), not the three-stage RXA chain, so this is short.
    private void ApplyTxBandpassForMode(int txa, RxaMode mode)
    {
        const double lo = 150.0;
        const double hi = 2850.0;
        double low, high;
        switch (mode)
        {
            case RxaMode.LSB:
            case RxaMode.CWL:
            case RxaMode.DIGL:
                low = -hi; high = -lo; break;
            case RxaMode.USB:
            case RxaMode.CWU:
            case RxaMode.DIGU:
                low = lo; high = hi; break;
            default:
                low = -hi; high = hi; break;
        }
        NativeMethods.SetTXABandpassFreqs(txa, low, high);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var key in _channels.Keys.ToArray())
        {
            if (_channels.TryRemove(key, out var state))
                StopChannel(state);
        }
        lock (_txaLock)
        {
            if (_txaChannelId is int txa)
            {
                NativeMethods.CloseChannel(txa);
                _txaChannelId = null;
            }
        }
    }

    // Thetis-style log-recursive EMA on both pan and wf outputs. `tauSec` is
    // the visual smoothing time constant; with PipelineFps ticks/s the per-tick
    // retention is `exp(-1 / (fps * tau))`. Default 100 ms reads as "smooth
    // but still alive" — heavy enough to kill the per-frame jumpiness the
    // user called out, light enough that signals still pop.
    private const int PipelineFps = 30;
    private const double DefaultAvgTauSec = 0.100;
    private const int LogRecursiveMode = 3;

    private static void ConfigureDisplayAveraging(int disp)
    {
        double backmult = Math.Exp(-1.0 / (PipelineFps * DefaultAvgTauSec));
        for (int pixout = 0; pixout < 2; pixout++)
        {
            NativeMethods.SetDisplayAverageMode(disp, pixout, LogRecursiveMode);
            NativeMethods.SetDisplayAvBackmult(disp, pixout, backmult);
            NativeMethods.SetDisplayNumAverage(disp, pixout, 2);
        }
    }

    private static void ConfigureAnalyzer(int disp, int sampleRateHz, int pixelWidth, int zoomLevel)
    {
        int overlap = (int)Math.Max(0, Math.Ceiling(AnalyzerFftSize - (double)sampleRateHz / AnalyzerFps));
        int maxW = AnalyzerFftSize + (int)Math.Min(
            AnalyzerKeepTime * sampleRateHz,
            AnalyzerKeepTime * AnalyzerFftSize * AnalyzerFps);
        int flp = 0;

        // fscLin/fscHin are integer bin counts to clip from the LOW and HIGH
        // ends of the full-span FFT output (analyzer.c:1253-1254, PanDisplay.cs
        // :4720-4726 in Thetis). For a centred zoom by factor L, keep
        // fft_size/L bins in the middle and clip (fft_size - fft_size/L)/2
        // from each side. At L=1 both clips are 0 (full span).
        double fscLin = 0.0, fscHin = 0.0;
        if (zoomLevel > 1)
        {
            int clippedPerSide = AnalyzerFftSize * (zoomLevel - 1) / (2 * zoomLevel);
            fscLin = clippedPerSide;
            fscHin = clippedPerSide;
        }

        NativeMethods.SetAnalyzer(
            disp: disp,
            n_pixout: 2,
            n_fft: 1,
            typ: 1,
            flp: ref flp,
            sz: AnalyzerFftSize,
            bf_sz: InSize,
            win_type: AnalyzerWindow,
            pi_alpha: AnalyzerKaiserPi,
            ovrlp: overlap,
            clp: 0,
            fscLin: fscLin,
            fscHin: fscHin,
            n_pix: pixelWidth,
            n_stch: 1,
            calset: 0,
            fmin: 0.0,
            fmax: 0.0,
            max_w: maxW);
    }

    private void StopChannel(ChannelState state)
    {
        state.Stopped = true;
        state.InQueue.CompleteAdding();
        state.Cts.Cancel();
        if (!state.Worker.Join(TimeSpan.FromSeconds(2)))
        {
            // Worker did not exit in time; fall through to teardown anyway.
        }
        state.InQueue.Dispose();
        state.Cts.Dispose();
        NativeMethods.DestroyAnalyzer(state.Id);
        // Tear down EXT blankers before CloseChannel — they reference our id
        // slot in panb[]/pnob[] and outlive CloseChannel unless destroyed here.
        NativeMethods.DestroyAnbEXT(state.Id);
        NativeMethods.DestroyNobEXT(state.Id);
        NativeMethods.CloseChannel(state.Id);
    }

    private static void RunWorker(ChannelState state)
    {
        double[] audio = new double[state.OutDoubles];
        double[] spectrumIq = new double[2 * InSize];
        int monoSamples = state.OutDoubles / 2;
        try
        {
            foreach (var frame in state.InQueue.GetConsumingEnumerable(state.Cts.Token))
            {
                // Pre-RXA blanker. In-place is safe: xanb/xnob read a->in[i]
                // before writing a->out[i] within each iteration, so same-buffer
                // aliasing doesn't clobber unread samples. Skipped entirely when
                // both NBs are off so there's no WDSP call overhead in the common
                // path. Non-enabled side stays at Run=0, so even if the mode
                // changes mid-frame its xanb/xnob is a no-op pass-through.
                switch (state.CurrentNbMode)
                {
                    case NbMode.Nb1:
                        NativeMethods.XanbEXT(state.Id, ref frame[0], ref frame[0]);
                        break;
                    case NbMode.Nb2:
                        NativeMethods.XnobEXT(state.Id, ref frame[0], ref frame[0]);
                        break;
                }

                NativeMethods.fexchange0(
                    state.Id,
                    ref frame[0],
                    ref audio[0],
                    out _);
                // Empirical fix for HL2 panadapter sideband mirror: conjugate the
                // IQ stream fed to the analyzer (I unchanged, Q negated). Audio
                // path keeps the original IQ so demod stays correct. Without this
                // the displayed spectrum appears flipped about the carrier (USB
                // energy shows left of carrier, LSB shows right) despite audio
                // and the synthetic-IQ orientation test both being correct.
                for (int i = 0; i < frame.Length; i += 2)
                {
                    spectrumIq[i] = frame[i];
                    spectrumIq[i + 1] = -frame[i + 1];
                }
                // Analyzer input side: paired with GetPixels under the same
                // lock, so SetZoom can rebuild bin mapping without a half-
                // written state being observed.
                lock (state.AnalyzerLock)
                {
                    NativeMethods.Spectrum0(state.SpectrumRun, state.Id, 0, 0, ref spectrumIq[0]);
                }
                PushAudio(state, audio, monoSamples);
                state.FreeFrames.Enqueue(frame);
            }
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
    }

    // Mirror Thetis radio.cs's NB property-setter behaviour: after create_*EXT
    // seeds the struct, the setters immediately overwrite the knob state and
    // run initBlanker / init_nob once. Keeping this as a discrete config block
    // means an advanced-NB panel can reuse the same setter path with
    // user-supplied values rather than introducing a second code path.
    private static void ApplyNbDefaults(int id)
    {
        NativeMethods.SetEXTANBTau(id, NrDefaults.NbTau);
        NativeMethods.SetEXTANBHangtime(id, NrDefaults.NbHangtime);
        NativeMethods.SetEXTANBAdvtime(id, NrDefaults.NbAdvtime);
        NativeMethods.SetEXTANBBacktau(id, NrDefaults.NbBacktau);
        NativeMethods.SetEXTANBThreshold(id, NrDefaults.NbDefaultThresholdScaled);

        NativeMethods.SetEXTNOBMode(id, 0);
        NativeMethods.SetEXTNOBTau(id, NrDefaults.NbTau);
        NativeMethods.SetEXTNOBHangtime(id, NrDefaults.NbHangtime);
        NativeMethods.SetEXTNOBAdvtime(id, NrDefaults.NbAdvtime);
        NativeMethods.SetEXTNOBBacktau(id, NrDefaults.NbBacktau);
        NativeMethods.SetEXTNOBThreshold(id, NrDefaults.NbDefaultThresholdScaled);
    }

    // Applies Thetis AGC_MEDIUM defaults — the mode all HL2 users start on.
    // Without this, WDSP's AGC is off and the audio path has effectively
    // unity gain on signals with peak ~2e-5, which is inaudible.
    private static void ApplyAgcDefaults(int id)
    {
        NativeMethods.SetRXAAGCMode(id, 3);              // MED
        NativeMethods.SetRXAAGCSlope(id, 35);
        NativeMethods.SetRXAAGCTop(id, 80.0);            // max gain, dB
        NativeMethods.SetRXAAGCAttack(id, 2);
        NativeMethods.SetRXAAGCHang(id, 0);
        NativeMethods.SetRXAAGCDecay(id, 250);
        NativeMethods.SetRXAAGCHangThreshold(id, 100);
    }

    // WDSP bandpass takes signed frequencies: LSB-family modes live in negative
    // baseband (low=-high, high=-low), USB-family in positive. CW follows the
    // USB/LSB convention per its suffix. Other modes keep unsigned bounds since
    // their passbands span zero.
    private static void ApplyBandpassForMode(ChannelState state)
    {
        int lo = state.FilterLowAbsHz;
        int hi = state.FilterHighAbsHz;
        double low, high;
        switch (state.CurrentMode)
        {
            case RxaMode.LSB:
            case RxaMode.CWL:
            case RxaMode.DIGL:
                low = -hi; high = -lo; break;
            case RxaMode.USB:
            case RxaMode.CWU:
            case RxaMode.DIGU:
                low = lo; high = hi; break;
            default:
                // AM/SAM/DSB/FM/DRM/SPEC: symmetric around 0.
                low = -hi; high = hi; break;
        }
        // Thetis rxa.cs:110-124: every filter change updates all three stages.
        // SetRXABandpassFreqs alone only affects bp1, which is bypassed for SSB.
        // nbp0 (RXANBPSetFreqs) is what actually carries the SSB passband.
        NativeMethods.SetRXABandpassFreqs(state.Id, low, high);
        NativeMethods.RXANBPSetFreqs(state.Id, low, high);
        NativeMethods.SetRXASNBAOutputBandwidth(state.Id, low, high);
    }

    private static RxaMode MapMode(RxMode mode) => mode switch
    {
        RxMode.LSB => RxaMode.LSB,
        RxMode.USB => RxaMode.USB,
        RxMode.CWL => RxaMode.CWL,
        RxMode.CWU => RxaMode.CWU,
        RxMode.AM => RxaMode.AM,
        RxMode.FM => RxaMode.FM,
        RxMode.SAM => RxaMode.SAM,
        RxMode.DSB => RxaMode.DSB,
        RxMode.DIGL => RxaMode.DIGL,
        RxMode.DIGU => RxaMode.DIGU,
        _ => RxaMode.USB,
    };
}
