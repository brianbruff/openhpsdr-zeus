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
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF), with contributions from (DH1KLM); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

namespace Zeus.Contracts;

/// <summary>
/// Per-radio-model constants for TX forward / reflected power calibration and
/// the safe PA ceiling used for meter scaling. The power math is the same
/// across boards — <c>watts = volts² / bridge_volt</c> where
/// <c>volts = (adc − cal_offset) / 4095 · ref_voltage</c> — only the constants
/// differ. Thetis <c>console.cs:25008-25072</c> is the reference.
/// </summary>
public sealed record RadioCalibration(
    double BridgeVolt,
    double RefVoltage,
    int AdcCalOffset,
    double MaxWatts)
{
    /// <summary>
    /// Hermes-Lite 2 defaults. Thetis <c>console.cs:25973-25977</c> uses
    /// <c>bridge_volt = 1.5</c> for HL2 specifically — its onboard RF detector
    /// has a very different transfer function from the classic Alex bridge
    /// (which is 0.09). Using the Alex value reads ~16× too high.
    /// MaxWatts is the 5 W PA rating — meter scaling only, not protection.
    /// </summary>
    public static readonly RadioCalibration HermesLite2 = new(
        BridgeVolt: 1.5,
        RefVoltage: 3.3,
        AdcCalOffset: 6,
        MaxWatts: 5.0);
}
