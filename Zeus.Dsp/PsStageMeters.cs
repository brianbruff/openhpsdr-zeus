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

namespace Zeus.Dsp;

/// <summary>
/// PureSignal calcc-stage readings sampled from <c>GetPSInfo</c> and
/// <c>GetPSMaxTX</c>. Captured at the same 10 Hz cadence as the TX stage
/// meters but only emitted to the wire when PsEnabled is true.
/// </summary>
/// <param name="FeedbackLevel">info[4] — feedback envelope level, 0..256
/// raw. UI normalises to 0..1 via /256 for the bar.</param>
/// <param name="CalState">info[15] — cal-state enum: 0 RESET, 1 WAIT,
/// 2 MOXDELAY, 3 SETUP, 4 COLLECT, 5 MOXCHECK, 6 CALC, 7 DELAY, 8 STAYON,
/// 9 TURNON. Drives the cal-state badge in the UI.</param>
/// <param name="Correcting">info[14] != 0 — the iqc stage is actively
/// applying a correction curve.</param>
/// <param name="CorrectionDb">Derived metric: RMS of the calcc output curve
/// in dB. Zero when not correcting; useful as a "depth" indicator.</param>
/// <param name="MaxTxEnvelope"><c>GetPSMaxTX</c> — peak TX envelope
/// magnitude since the last reset. Used by auto-attenuate to know when to
/// step the attenuator down.</param>
public readonly record struct PsStageMeters(
    float FeedbackLevel,
    byte CalState,
    bool Correcting,
    float CorrectionDb,
    float MaxTxEnvelope)
{
    public static readonly PsStageMeters Silent = new(
        FeedbackLevel: 0f,
        CalState: 0,
        Correcting: false,
        CorrectionDb: 0f,
        MaxTxEnvelope: 0f);
}
