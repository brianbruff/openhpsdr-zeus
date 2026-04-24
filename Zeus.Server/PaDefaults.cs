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

using Zeus.Protocol1.Discovery;

namespace Zeus.Server;

// Per-board PA gain defaults (dB), lifted from Thetis
// `clsHardwareSpecific.cs:470-767` and piHPSDR `band.c:498-500`. These are
// seeds — the operator calibrates the real numbers via an external watt-meter
// or the future in-app wizard; these just keep the drive math sane on first
// connect so "5 W target" doesn't emit µW (too much default gain) or blow the
// PA (too little).
//
// Band names must match BandUtils.HfBands. Any band not listed falls back to
// 0.0 dB, which short-circuits PaSettingsStore to the legacy percent→byte
// path and preserves pre-calibration behavior.
internal static class PaDefaults
{
    // Thetis HERMES / HPSDR / ORIONMKII / ANAN10 / ANAN10E bracket
    // (setup.cs:482-544). 100 W class-A builds; lowest gain at 6m.
    private static readonly IReadOnlyDictionary<string, double> HermesGains = new Dictionary<string, double>
    {
        ["160m"] = 41.0, ["80m"] = 41.2, ["60m"] = 41.3, ["40m"] = 41.3,
        ["30m"] = 41.0, ["20m"] = 40.5, ["17m"] = 39.9, ["15m"] = 38.8,
        ["12m"] = 38.8, ["10m"] = 38.8, ["6m"] = 38.8,
    };

    // Thetis ANAN100 / ANAN100B / ANAN8000D bracket (setup.cs:546-694).
    // 100 W ANAN production radios.
    private static readonly IReadOnlyDictionary<string, double> Anan100Gains = new Dictionary<string, double>
    {
        ["160m"] = 50.0, ["80m"] = 50.5, ["60m"] = 50.5, ["40m"] = 50.0,
        ["30m"] = 49.5, ["20m"] = 48.5, ["17m"] = 48.0, ["15m"] = 47.5,
        ["12m"] = 46.5, ["10m"] = 42.0, ["6m"] = 43.0,
    };

    // Thetis ANAN100D / ANAN200D bracket (setup.cs:606-664). Dual-ADC
    // builds — slightly lower per-band gain than the ANAN100 bracket.
    private static readonly IReadOnlyDictionary<string, double> Anan200Gains = new Dictionary<string, double>
    {
        ["160m"] = 49.5, ["80m"] = 50.5, ["60m"] = 50.5, ["40m"] = 50.0,
        ["30m"] = 49.0, ["20m"] = 48.0, ["17m"] = 47.0, ["15m"] = 46.5,
        ["12m"] = 46.0, ["10m"] = 43.5, ["6m"] = 43.0,
    };

    // Thetis ANAN7000D / ANAN_G1 / ANAN_G2 / ANVELINAPRO3 bracket
    // (setup.cs:696-728). Saturn / G2 class; highest HF gain per band.
    private static readonly IReadOnlyDictionary<string, double> OrionG2Gains = new Dictionary<string, double>
    {
        ["160m"] = 47.9, ["80m"] = 50.5, ["60m"] = 50.8, ["40m"] = 50.8,
        ["30m"] = 50.9, ["20m"] = 50.9, ["17m"] = 50.5, ["15m"] = 47.0,
        ["12m"] = 47.9, ["10m"] = 46.5, ["6m"] = 44.6,
    };

    // HL2 is a percentage-based PA model (mi0bot openhpsdr-thetis fork,
    // clsHardwareSpecific.cs:767-795). The value stored in PaGainDb for
    // HL2 is an output-percentage (0..100), NOT dB — see the long comment
    // on HermesLite2DriveProfile. HF bands sit at 100 (no attenuation);
    // 6 m falls to 38.8 because the stock HL2 PA has materially less gain
    // at 50 MHz and mi0bot soft-caps there.
    //
    // The previous 40.5 dB value came from piHPSDR's published generic PA
    // calibration constant. That number is a dB forward-gain for 8-bit-
    // drive radios (Hermes / ANAN / Orion). It does NOT apply to HL2,
    // which has a 4-bit drive register and a different firmware-side scaling.
    // Interpreting 40.5 as dB through the full-byte math produced nibble
    // 0x3 → ~20 % of rated output — the whole "HL2 makes 1 W" complaint
    // family. See docs/lessons/hl2-drive-model.md for the full trace.
    private static readonly IReadOnlyDictionary<string, double> Hl2OutputPct = new Dictionary<string, double>
    {
        ["160m"] = 100.0, ["80m"] = 100.0, ["60m"] = 100.0, ["40m"] = 100.0,
        ["30m"] = 100.0, ["20m"] = 100.0, ["17m"] = 100.0, ["15m"] = 100.0,
        ["12m"] = 100.0, ["10m"] = 100.0, ["6m"] = 38.8,
    };

    private static IReadOnlyDictionary<string, double> TableFor(HpsdrBoardKind board) => board switch
    {
        HpsdrBoardKind.Hermes      => HermesGains,
        HpsdrBoardKind.Metis       => HermesGains,
        HpsdrBoardKind.Griffin     => HermesGains,
        HpsdrBoardKind.Angelia     => Anan100Gains,
        HpsdrBoardKind.Orion       => Anan200Gains,
        HpsdrBoardKind.OrionMkII   => OrionG2Gains,
        _                          => new Dictionary<string, double>(),
    };

    // Returns the per-band PA calibration seed. Units are *board-dependent*:
    //   • HL2: output percentage (0..100) — HF bands default 100, 6 m 38.8.
    //   • Everything else: dB forward gain (Thetis / piHPSDR convention).
    // The method name is retained across the board split because the storage
    // field on the DTO is also shared (`PaGainDb`). Semantics are resolved
    // inside the per-board IRadioDriveProfile implementation.
    public static double GetPaGainDb(HpsdrBoardKind board, string band)
    {
        if (board == HpsdrBoardKind.HermesLite2)
            return Hl2OutputPct.TryGetValue(band, out var pct) ? pct : 100.0;
        return TableFor(board).TryGetValue(band, out var v) ? v : 0.0;
    }

    // Rated PA output in watts per board class. Used as the default for
    // PaGlobalSettingsDto.PaMaxPowerWatts when no operator-entered value is
    // stored. Without this, new installs fall into the
    // `maxWatts == 0` → `byte = pct * 255 / 100` legacy path, which silently
    // ignores PaGainDb and makes the per-band settings feel broken.
    //
    // Values match the Thetis factory labels per board class. Operators can
    // override at any time via the PA Settings panel and the stored value
    // wins over this default on subsequent reads.
    public static int GetMaxPowerWatts(HpsdrBoardKind board) => board switch
    {
        HpsdrBoardKind.HermesLite2 => 5,      // HL2: class-A 5 W stock
        HpsdrBoardKind.Hermes      => 10,     // Hermes / ANAN-10 / ANAN-10E: 10 W
        HpsdrBoardKind.Metis       => 10,     // Metis boards paired with 10 W PA
        HpsdrBoardKind.Griffin     => 10,
        HpsdrBoardKind.Angelia     => 100,    // ANAN-100 / ANAN-100B / ANAN-8000D: 100 W
        HpsdrBoardKind.Orion       => 100,    // ANAN-100D / ANAN-200D: 100 W
        HpsdrBoardKind.OrionMkII   => 100,    // ANAN-7000D / G1 / G2 / G2-1K driven: 100 W
        _                          => 0,      // Unknown board — keep legacy mode, no surprises
    };
}
