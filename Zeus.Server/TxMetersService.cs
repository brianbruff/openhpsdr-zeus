using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Dsp;

namespace Zeus.Server;

/// <summary>
/// Consumes raw FWD/REF ADC readings from Protocol1, smooths them with an
/// exponential low-pass, converts to watts + SWR per the HermesLite2
/// calibration, and broadcasts a <see cref="TxMetersFrame"/> over the
/// StreamingHub at 10 Hz while MOX is on / 2 Hz when idle.
///
/// PRD FR-6: If SWR > 2.5 sustained for ≥500 ms while MOX or TUN is on,
/// auto-drop MOX/TUN and emit an AlertFrame. Protects the HL2 finals if
/// the antenna goes out of match mid-transmission.
///
/// Math provenance: Thetis <c>console.cs:25008-25072</c> (watts) and
/// <c>console.cs:25972-25978</c> (SWR). Smoothing α matches
/// <c>console.cs:25011,25931</c>.
/// </summary>
public sealed class TxMetersService : BackgroundService
{
    // Thetis uses a 90/10 split on the raw ADC (console.cs:25011).
    private const double SmoothAlpha = 0.90;

    // Wire FWD ≤ 2 W as a floor for SWR; below the bridge noise dominates
    // and the ratio is meaningless (Thetis does the same in console.cs:25974).
    private const double SwrMinFwdWatts = 2.0;
    private const double SwrMax = 9.0;

    // PRD FR-6: SWR > 2.5 sustained 500 ms → trip MOX/TUN. Tighter than the
    // original 3.0 threshold; chosen to protect HL2 PA aggressively.
    private const double SwrTripThreshold = 2.5;
    private static readonly TimeSpan SwrTripDuration = TimeSpan.FromMilliseconds(500);

    // PRD FR-6: a single MOX/TUN transmission may not exceed 120 s. Catches
    // stuck spacebars, jammed buttons, or a client that forgot to unkey. The
    // constant is internal-settable so a test can shorten the window without
    // driving a 2-minute wall-clock delay.
    internal static readonly TimeSpan DefaultTxTimeout = TimeSpan.FromSeconds(120);
    internal TimeSpan TxTimeout { get; set; } = DefaultTxTimeout;

    private static readonly TimeSpan MoxTick = TimeSpan.FromMilliseconds(100); // 10 Hz
    private static readonly TimeSpan IdleTick = TimeSpan.FromMilliseconds(500); // 2 Hz

    private readonly StreamingHub _hub;
    private readonly TxService _tx;
    private readonly DspPipelineService _pipe;
    private readonly ILogger<TxMetersService> _log;
    private readonly RadioCalibration _cal = RadioCalibration.HermesLite2;

    private readonly object _sync = new();
    private double _fwdAdc;
    private double _refAdc;
    private bool _seenSample;
    // SWR trip state: timestamp when SWR first exceeded threshold, or null if
    // SWR is currently below threshold. Checked on every meter tick (100 ms).
    private DateTime? _swrAboveThresholdSince;

    public TxMetersService(StreamingHub hub, RadioService radio, TxService tx, DspPipelineService pipe, ILogger<TxMetersService> log)
    {
        _hub = hub;
        _tx = tx;
        _pipe = pipe;
        _log = log;
        // Bind to the radio's connection lifecycle so we subscribe to telemetry
        // on every fresh Protocol1Client instance. The event is one-way
        // (Protocol1 → Server) and carries only AIN readings.
        radio.Connected += OnConnected;
        radio.Disconnected += OnDisconnected;
    }

    // Holds the last subscribed client so OnDisconnected can detach the
    // TelemetryReceived handler once the Protocol1 surface lands.
    private Zeus.Protocol1.IProtocol1Client? _subscribedClient;

    // HL2 C&C-echo addresses that carry the alex FWD/REF ADCs
    // (see TelemetryReading docs).
    // addr=1 (C0=0x08): Ain1 = alex_forward_power
    // addr=2 (C0=0x10): Ain0 = alex_reverse_power
    // Match on bits 4:1 only — C0[0] is the PTT/MOX echo (so a live TX packet
    // arrives as 0x09/0x11), and C0[7] is the HL2 IOB ACK
    // marker which PacketParser already filters out via the addr==1|2|3 gate.
    private const byte C0AddrMask = 0x7E;
    private const byte C0AddrAlexFwd = 0x08;
    private const byte C0AddrAlexRef = 0x10;

    /// <summary>
    /// Entry point for telemetry consumers. FWD and REF live on different
    /// C&amp;C echo slots (0x08 and 0x10), so one <see cref="Zeus.Protocol1.TelemetryReading"/>
    /// updates at most one axis per call.
    /// </summary>
    public void OnTelemetry(Zeus.Protocol1.TelemetryReading reading)
    {
        switch (reading.C0Address & C0AddrMask)
        {
            case C0AddrAlexFwd:
                ApplySmoothed(ref _fwdAdc, reading.Ain1);
                break;
            case C0AddrAlexRef:
                ApplySmoothed(ref _refAdc, reading.Ain0);
                break;
            default:
                // Other echo slots (ADC bias, exciter/temp) aren't part of the
                // FWD/REF meter pair. Silently ignored — protection/alerts
                // are a later slice.
                break;
        }
    }

    // Overload kept for unit tests that want to drive both axes simultaneously
    // without constructing two TelemetryReading structs.
    internal void OnTelemetryRaw(ushort fwdAdc, ushort refAdc)
    {
        ApplySmoothed(ref _fwdAdc, fwdAdc);
        ApplySmoothed(ref _refAdc, refAdc);
    }

    private void ApplySmoothed(ref double state, ushort raw)
    {
        lock (_sync)
        {
            if (!_seenSample)
            {
                // First-sample fast path matches Thetis console.cs:25011 — seed
                // both axes so the UI doesn't ramp up from zero across the
                // ~2 s alpha=0.90 settling time.
                _fwdAdc = raw;
                _refAdc = raw;
                state = raw;
                _seenSample = true;
                return;
            }
            state = SmoothAlpha * state + (1.0 - SmoothAlpha) * raw;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // TX timeout guard: PRD FR-6 caps a single TX at 120 s to catch
                // stuck spacebar / jammed PTT. TxService hands off the trip via
                // the same AlertFrame path as SWR so the client only needs one
                // protection-event listener.
                if (EvaluateTimeoutTrip(DateTime.UtcNow) is { } timeoutReason)
                {
                    _tx.TryTripForAlert(AlertKind.TxTimeout, timeoutReason);
                }

                // Meter during MOX *or* TUN — both drive the PA, both need live
                // FWD/SWR readouts. Idle frame is only for fully-unkeyed RX.
                bool mox = _tx.IsMoxOn || _tx.IsTunOn;
                TxMetersFrame frame;
                double swr = 1.0;
                if (mox)
                {
                    double fwdAdc, refAdc;
                    lock (_sync) { fwdAdc = _fwdAdc; refAdc = _refAdc; }
                    var (fwdW, refW, swrVal) = ComputeMeters(fwdAdc, refAdc, _cal);
                    swr = swrVal;
                    // Stage meters are published by WdspDspEngine.ProcessTxBlock;
                    // may lag the first TX block by a few ticks at MOX-on, which
                    // reads as "Silent" (−∞ level / 0 GR) — UI treats as empty.
                    var stage = _pipe.CurrentEngine?.GetTxStageMeters() ?? TxStageMeters.Silent;
                    frame = new TxMetersFrame(
                        FwdWatts: (float)fwdW,
                        RefWatts: (float)refW,
                        Swr: (float)swr,
                        MicDbfs: stage.MicPk,
                        EqPk: stage.EqPk,
                        LvlrPk: stage.LvlrPk,
                        AlcPk: stage.AlcPk,
                        AlcGr: stage.AlcGr,
                        OutPk: stage.OutPk);

                    if (EvaluateSwrTrip(swr, DateTime.UtcNow) is { } tripReason)
                    {
                        // TryTripForAlert is idempotent — a second caller on the
                        // same tick (e.g. timeout firing concurrently) finds MOX
                        // already off and no-ops.
                        _tx.TryTripForAlert(AlertKind.SwrTrip, tripReason);
                    }
                }
                else
                {
                    // Zero the TX fields while idle so the UI doesn't latch a
                    // stale pre-unkey reading. Stage meters go to Silent (−∞)
                    // so the diagnostic strip renders empty instead of latching
                    // last-during-TX values.
                    var silent = TxStageMeters.Silent;
                    frame = new TxMetersFrame(
                        FwdWatts: 0f,
                        RefWatts: 0f,
                        Swr: 1.0f,
                        MicDbfs: silent.MicPk,
                        EqPk: silent.EqPk,
                        LvlrPk: silent.LvlrPk,
                        AlcPk: silent.AlcPk,
                        AlcGr: silent.AlcGr,
                        OutPk: silent.OutPk);
                    // Clear the trip timer when not keyed so a brief spike doesn't
                    // carry over into the next TX.
                    lock (_sync) { _swrAboveThresholdSince = null; }
                }

                _hub.Broadcast(frame);

                try { await Task.Delay(mox ? MoxTick : IdleTick, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "tx.meters broadcast loop exited with error");
        }
    }

    /// <summary>
    /// Evaluate the SWR sustain window and return the operator-facing trip
    /// message when the threshold has been exceeded for ≥500 ms, or null if
    /// not yet. Exposed as <c>internal</c> so unit tests can drive synthetic
    /// timestamps without a <see cref="DateTime"/> abstraction — the
    /// production caller passes <see cref="DateTime.UtcNow"/>. Firing resets
    /// the timer so the caller gets exactly one trip per sustained excursion.
    /// </summary>
    internal string? EvaluateSwrTrip(double swr, DateTime now)
    {
        lock (_sync)
        {
            if (swr > SwrTripThreshold)
            {
                if (_swrAboveThresholdSince is null)
                {
                    _swrAboveThresholdSince = now;
                    return null;
                }
                if (now - _swrAboveThresholdSince.Value >= SwrTripDuration)
                {
                    _swrAboveThresholdSince = null;
                    return $"SWR {swr:F1}:1 sustained >500 ms — dropped TX to protect PA";
                }
                return null;
            }
            _swrAboveThresholdSince = null;
            return null;
        }
    }

    /// <summary>
    /// PRD FR-6 TX timeout: returns a trip reason if MOX or TUN has been
    /// continuously on for ≥ <see cref="TxTimeout"/>, else null. Reads the
    /// keyed-at timestamps from <see cref="TxService"/> (which records them
    /// on state transitions) so the check is stateless in this class.
    /// </summary>
    internal string? EvaluateTimeoutTrip(DateTime now)
    {
        var moxStart = _tx.MoxStartedAt;
        if (moxStart is not null && now - moxStart.Value >= TxTimeout)
            return $"TX timeout: MOX keyed >{(int)TxTimeout.TotalSeconds} s — dropped to protect PA";
        var tunStart = _tx.TunStartedAt;
        if (tunStart is not null && now - tunStart.Value >= TxTimeout)
            return $"TX timeout: TUN keyed >{(int)TxTimeout.TotalSeconds} s — dropped to protect PA";
        return null;
    }

    /// <summary>
    /// Port of Thetis <c>console.cs:25008-25072</c> watts math plus the
    /// <c>console.cs:25972-25978</c> SWR ratio. Exposed for unit tests —
    /// pure function, no state.
    /// </summary>
    public static (double FwdWatts, double RefWatts, double Swr) ComputeMeters(
        double fwdAdc, double refAdc, RadioCalibration cal)
    {
        double fwdV = (fwdAdc - cal.AdcCalOffset) / 4095.0 * cal.RefVoltage;
        double refV = (refAdc - cal.AdcCalOffset) / 4095.0 * cal.RefVoltage;
        double fwdW = fwdV * fwdV / cal.BridgeVolt;
        double refW = refV * refV / cal.BridgeVolt;
        if (fwdW < 0 || double.IsNaN(fwdW)) fwdW = 0;
        if (refW < 0 || double.IsNaN(refW)) refW = 0;

        double swr;
        if (fwdW <= SwrMinFwdWatts)
        {
            swr = 1.0;
        }
        else
        {
            double ratio = refW / fwdW;
            if (ratio < 0) ratio = 0;
            if (ratio >= 1.0)
            {
                swr = SwrMax;
            }
            else
            {
                double rho = Math.Sqrt(ratio);
                double s = (1.0 + rho) / (1.0 - rho);
                if (double.IsNaN(s) || double.IsInfinity(s)) swr = SwrMax;
                else swr = Math.Min(s, SwrMax);
            }
        }

        return (fwdW, refW, swr);
    }

    private void OnConnected(Zeus.Protocol1.IProtocol1Client client)
    {
        _subscribedClient = client;
        client.TelemetryReceived += OnTelemetry;
    }

    private void OnDisconnected()
    {
        var client = _subscribedClient;
        _subscribedClient = null;
        if (client is not null) client.TelemetryReceived -= OnTelemetry;
        lock (_sync)
        {
            _fwdAdc = 0;
            _refAdc = 0;
            _seenSample = false;
            _swrAboveThresholdSince = null;
        }
    }
}
