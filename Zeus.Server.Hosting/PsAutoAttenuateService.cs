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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

namespace Zeus.Server;

/// <summary>
/// PureSignal AutoAttenuate loop. Polls the calcc feedback level (info[4]) at
/// 10 Hz while PS is armed and the operator has AutoAttenuate on; if the level
/// lands outside the [128, 181] ideal window calcc rejects every fit
/// (binfo[6] != 0 → scOK=0 → bs_count==2 → LRESET → loop), so PS never
/// converges. The loop adjusts the radio's TX step attenuator to bring
/// feedback into the window.
///
/// Mirrors Thetis <c>PSForm.cs:728-784</c> timer2code and the
/// <c>PSForm.cs:1109-1112</c> NeedToRecalibrate threshold:
///   • feedback &gt; 181  → too hot → attenuate more (delta &gt; 0).
///   • feedback ≤ 128 AND current att &gt; 0 → too quiet → attenuate less.
/// Step size is <c>20 * log10(feedback / 152.293)</c> dB clamped to ±1 per
/// tick (1 dB/100 ms — matches Thetis feel; converges within a couple of
/// seconds without overshooting). After every attenuator change we issue a
/// SetPSControl(reset=1) so calcc retries with the new feedback level.
///
/// Hard-gated on a P2 connection (TX step attenuator wire support landed in
/// <see cref="Zeus.Protocol2.Protocol2Client.SetTxAttenuationDb"/>; the P1
/// path hasn't been wired yet). Idle when PS is off, AutoAttenuate is off, or
/// the radio isn't keyed — no broadcast, no engine pokes.
/// </summary>
public sealed class PsAutoAttenuateService : BackgroundService
{
    // Thetis ideal feedback target: 152.293 (PSForm.cs:745). Window 128..181
    // matches the lblPSInfoFB green-LED thresholds (PSForm.cs:1123-1138).
    private const double IdealFeedback = 152.293;
    private const int FeedbackLowThreshold = 128;
    private const int FeedbackHighThreshold = 181;

    // 10 Hz tick. Same cadence Thetis runs timer2code at when PS is armed and
    // the form has focus (PSForm.cs:204-209, m_bQuckAttenuate=false default).
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    // Hardware bounds for the TX step attenuator (Thetis network.c:1238-1242
    // writes a single byte 0..31 dB per ADC tap).
    private const int TxAttnMinDb = 0;
    private const int TxAttnMaxDb = 31;

    // Settle time after a step change: give the radio one wire-cycle to pick
    // up the new attenuator, then issue the reset so calcc starts fresh.
    private static readonly TimeSpan PostStepSettle = TimeSpan.FromMilliseconds(100);

    private readonly RadioService _radio;
    private readonly TxService _tx;
    private readonly DspPipelineService _pipe;
    private readonly ILogger<PsAutoAttenuateService> _log;

    // Mirrored attenuator value — server-of-truth for what we last asked the
    // radio to apply. Reset to 0 on every fresh PS arm (PsEnabled false→true)
    // so a new operator session starts from the radio's untouched baseline.
    private int _currentAttnDb;
    private bool _psWasEnabled;

    // Last-observed info[5] (calcc CalibrationAttempts counter). We only
    // step the attenuator after calcc finishes a NEW fit — matches Thetis
    // PSForm.cs:1097-1099 timer2code which gates on
    // `CalibrationAttemptsChanged`. Stepping on every 100 ms tick instead
    // makes cm jump mid-fit, scheck flags 0x40, scOK=0, bs_count==2 forces
    // LRESET, and calcc never converges. Initialize to -1 so the first
    // observed counter (0 or any value) registers as "new".
    private int _lastCalibrationAttempts = -1;

    // Rate-limit bucket for diagnostic gate-skip logging. Tick1 runs at 10 Hz;
    // without rate-limiting a stuck gate would emit 10 lines/sec. 1 s bucket
    // gives one line per gate-state per second — enough to localise the
    // failing gate during a 5 s rack key without flooding the log.
    private long _lastGateLogTickMs;
    private const long GateLogIntervalMs = 1000;

    public PsAutoAttenuateService(
        RadioService radio,
        TxService tx,
        DspPipelineService pipe,
        ILogger<PsAutoAttenuateService> log)
    {
        _radio = radio;
        _tx = tx;
        _pipe = pipe;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("psAutoAttn.start");
        try
        {
            using var timer = new PeriodicTimer(Tick);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    Tick1();
                }
                catch (Exception ex)
                {
                    // Swallow — the loop must keep running so a transient
                    // engine race doesn't permanently disable auto-attn.
                    _log.LogWarning(ex, "psAutoAttn.tick failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    // Diagnostic — emits one line per second tagging which gate short-
    // circuited Tick1. Without this the loop is invisible when it returns
    // early (the only visible signals were `psAutoAttn.armed` and
    // `psAutoAttn.step`, neither of which fire when a gate fails). Used to
    // localise the silent-gate symptom on the G2 MkII rack test.
    private void LogGate(string outcome)
    {
        long now = Environment.TickCount64;
        if (now - _lastGateLogTickMs < GateLogIntervalMs) return;
        _lastGateLogTickMs = now;
        _log.LogInformation("psAutoAttn.gate {Outcome}", outcome);
    }

    private void Tick1()
    {
        var s = _radio.Snapshot();

        // PS-arm edge: re-baseline _currentAttnDb on every false→true so a
        // fresh arm starts at the radio's untouched 0 dB. The actual radio
        // state may differ if the operator manually changed step-att between
        // sessions; assume the radio holds 0 between arms (matches pihpsdr).
        if (s.PsEnabled && !_psWasEnabled)
        {
            _currentAttnDb = 0;
            _lastCalibrationAttempts = -1;
            _log.LogInformation("psAutoAttn.armed reset attn={Db}", _currentAttnDb);
        }
        _psWasEnabled = s.PsEnabled;

        // Idle conditions — no telemetry to act on.
        if (!s.PsEnabled) { LogGate("skip=PsEnabled-off"); return; }
        if (!s.PsAutoAttenuate) { LogGate("skip=AutoAttenuate-off"); return; }
        if (!_tx.IsMoxOn && !_tx.IsTwoToneOn) { LogGate("skip=not-keyed"); return; }

        var p2 = _pipe.CurrentP2Client;
        if (p2 is null) { LogGate("skip=p2-null"); return; }

        var engine = _pipe.CurrentEngine;
        if (engine is null) { LogGate("skip=engine-null"); return; }

        var psm = engine.GetPsStageMeters();
        int feedback = (int)Math.Round(psm.FeedbackLevel);

        // Thetis-canonical CalibrationAttemptsChanged gate (PSForm.cs:
        // 1097-1099). info[5] increments on every completed calc(). We
        // only step the attenuator after a NEW fit, otherwise the loop
        // changes the envelope mid-fit, cm coefficients jump, scheck
        // flags 0x40 (cm changed too much), scOK=0, bs_count==2 forces
        // LRESET, and the state machine spins without ever converging.
        // First-tick guard: only skip when we have an old value to
        // compare against AND the counter hasn't moved.
        if (_lastCalibrationAttempts >= 0 && psm.CalibrationAttempts == _lastCalibrationAttempts)
        {
            LogGate($"skip=no-new-calc info5={psm.CalibrationAttempts} fb={feedback}");
            return;
        }
        _lastCalibrationAttempts = psm.CalibrationAttempts;

        // info[4] = 0 means calcc hasn't completed a fit yet (state machine
        // is still pre-LCALC). No reading to act on.
        if (feedback <= 0) { LogGate($"skip=fb-zero psm.fb={psm.FeedbackLevel:F2}"); return; }
        // Already in window — nothing to do.
        if (feedback >= FeedbackLowThreshold && feedback <= FeedbackHighThreshold) { LogGate($"skip=in-window fb={feedback}"); return; }
        // Too quiet AND we're already at zero attenuation — operator must
        // raise drive (Thetis behaviour: timer2code falls through silently).
        if (feedback < FeedbackLowThreshold && _currentAttnDb <= TxAttnMinDb) { LogGate($"skip=too-quiet-at-zero fb={feedback}"); return; }

        // Compute target step. Thetis PSForm.cs:745:
        //     ddB = 20 * log10(feedback / 152.293)
        // Sign convention matches: feedback > 152 → ddB > 0 → attenuate more.
        double ddB = 20.0 * Math.Log10(feedback / IdealFeedback);
        // Clamp to ±1 dB per tick — matches Thetis timer2code feel and
        // prevents overshoot when feedback briefly spikes (e.g. SSB envelope
        // transient at a syllable).
        int step = ddB > 0 ? 1 : -1;
        int newAttn = Math.Clamp(_currentAttnDb + step, TxAttnMinDb, TxAttnMaxDb);
        if (newAttn == _currentAttnDb) return;

        _log.LogInformation(
            "psAutoAttn.step feedback={Fb} info5={Cal} ddB={DDb:F1} attn {Old}->{New} dB",
            feedback, psm.CalibrationAttempts, ddB, _currentAttnDb, newAttn);

        _currentAttnDb = newAttn;
        p2.SetTxAttenuationDb((byte)newAttn);

        // Brief settle so the radio applies the new step-att before calcc
        // rebuilds. Then reset state machine so the next pscc starts fresh
        // with the new feedback envelope (Thetis PSForm.cs:760-764 pattern:
        // SetPSControl(reset=1) then re-arm).
        try { Task.Delay(PostStepSettle).Wait(); } catch { /* ignore */ }
        engine.ResetPs();
    }
}
