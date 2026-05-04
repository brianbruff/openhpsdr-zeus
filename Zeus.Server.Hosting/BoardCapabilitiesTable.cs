// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Zeus.Contracts;
using Zeus.Protocol1.Discovery;

namespace Zeus.Server;

/// <summary>
/// Dispatch from <see cref="HpsdrBoardKind"/> to the per-board static
/// <see cref="BoardCapabilities"/> fingerprint. Mirrors
/// <see cref="RadioCalibrations"/>'s seam — every board fact that the
/// frontend needs to gate UI on (RX2 attenuator mode, audio amp,
/// volts/amps telemetry, etc.) flows through this helper.
///
/// Source: Thetis <c>clsHardwareSpecific.cs:85-803</c>, cross-referenced
/// in <c>docs/references/protocol-1/thetis-board-matrix.md</c>.
///
/// The 0x0A wire collision (OrionMkII / 7000DLE / 8000DLE / G2 / G2-1K /
/// ANVELINA-PRO3 / Red Pitaya all share a single byte) is handled here by
/// picking the most common variant's facts (G2-class). Once the operator
/// override from issue #218 lands the dispatch will fan out per variant.
/// </summary>
internal static class BoardCapabilitiesTable
{
    /// <summary>
    /// Look up the static capability fingerprint for a connected board.
    /// Falls back to <see cref="BoardCapabilities.UnknownDefaults"/> for
    /// any future enum value that hasn't been wired yet.
    /// </summary>
    public static BoardCapabilities For(HpsdrBoardKind board) =>
        For(board, OrionMkIIVariant.G2);

    /// <summary>
    /// Variant-aware overload — when <paramref name="board"/> is
    /// <see cref="HpsdrBoardKind.OrionMkII"/>, the variant selects the
    /// matching capability fingerprint. The Apache OrionMkII original
    /// (<see cref="OrionMkIIVariant.OrionMkII"/>) lacks volts/amps/audio-amp
    /// telemetry per <c>clsHardwareSpecific.cs:249-262, 459-468</c>; every
    /// other 0x0A variant shares the Saturn-class Saturn fingerprint.
    /// </summary>
    public static BoardCapabilities For(HpsdrBoardKind board, OrionMkIIVariant variant) => board switch
    {
        // --- Hermes-class single-RX, Alex-class BPF, Hermes-side L/R swap ---
        // Thetis clsHardwareSpecific.cs:87-121 sets RxADC=1, MKIIBPF=0,
        // ADCSupply=33, LRAudioSwap=1 for HERMES / ANAN10 / ANAN10E /
        // ANAN100 / ANAN100B. Path Illustrator supported (line 773-780
        // excludes only the high-power MkII family).
        HpsdrBoardKind.Metis      => HermesClass,
        HpsdrBoardKind.Hermes     => HermesClass,
        HpsdrBoardKind.HermesII    => HermesClass, // ANAN-10E / 100B firmware
        // --- ANAN-100D: dual-ADC Hermes-supply ---
        // clsHardwareSpecific.cs:122-128 — first DDC family entrant.
        HpsdrBoardKind.Angelia    => Angelia,
        // --- ANAN-200D: dual-ADC, 50 mV supply ---
        // clsHardwareSpecific.cs:136-142 — first 50 mV / high-power board.
        HpsdrBoardKind.Orion      => Orion,
        // --- HermesLite2 (mi0bot) ---
        // Thetis MW0LGE leaves HL2 unconfigured; Zeus has its own HL2 path
        // (docs/lessons/wdsp-init-gotchas.md, hl2-drive-model.md). Single
        // RX, no Alex, no telemetry, no path illustrator.
        HpsdrBoardKind.HermesLite2 => HermesLite2,
        // --- 0x0A family ---
        // Operator-selected variant (issue #218) routes to the matching
        // Saturn vs Apache-OrionMkII-original fingerprint.
        HpsdrBoardKind.OrionMkII  => variant == OrionMkIIVariant.OrionMkII
            ? OrionMkIIOriginal
            : Saturn,
        // --- ANAN-G2E (HermesC10, N1GP) ---
        // clsHardwareSpecific.cs:129-135. Hybrid: single RX + 33 mV supply
        // (Hermes-class) BUT MKII BPF on + LR-swap off + telemetry +
        // audio amp (Saturn-class). One of the two odd boards.
        HpsdrBoardKind.HermesC10  => HermesC10,
        // Unknown / future enum value — safe defaults.
        _                          => BoardCapabilities.UnknownDefaults,
    };

    private static readonly BoardCapabilities HermesClass = new(
        RxAdcCount: 1,
        MkiiBpf: false,
        AdcSupplyMv: 33,
        LrAudioSwap: true,
        HasVolts: false,
        HasAmps: false,
        HasAudioAmplifier: false,
        HasSteppedAttenuationRx2: false, // single-RX: RX2 doesn't exist
        SupportsPathIllustrator: true);

    private static readonly BoardCapabilities Angelia = new(
        RxAdcCount: 2,
        MkiiBpf: false,
        AdcSupplyMv: 33,
        LrAudioSwap: false,
        HasVolts: false,
        HasAmps: false,
        HasAudioAmplifier: false,
        HasSteppedAttenuationRx2: true,
        SupportsPathIllustrator: true);

    private static readonly BoardCapabilities Orion = new(
        RxAdcCount: 2,
        MkiiBpf: false,
        AdcSupplyMv: 50,
        LrAudioSwap: false,
        HasVolts: false,
        HasAmps: false,
        HasAudioAmplifier: false,
        HasSteppedAttenuationRx2: true,
        SupportsPathIllustrator: true);

    // 0x0A family (G2 / G2-1K / 7000DLE / 8000DLE / OrionMkII / ANVELINA-PRO3 /
    // Red Pitaya). Saturn-class facts.
    private static readonly BoardCapabilities Saturn = new(
        RxAdcCount: 2,
        MkiiBpf: true,
        AdcSupplyMv: 50,
        LrAudioSwap: false,
        HasVolts: true,
        HasAmps: true,
        HasAudioAmplifier: true,
        HasSteppedAttenuationRx2: true,
        SupportsPathIllustrator: false);

    private static readonly BoardCapabilities HermesC10 = new(
        RxAdcCount: 1,
        MkiiBpf: true,
        AdcSupplyMv: 33,
        LrAudioSwap: false,
        HasVolts: true,
        HasAmps: true,
        HasAudioAmplifier: true,
        HasSteppedAttenuationRx2: false, // single-RX: RX2 doesn't exist
        SupportsPathIllustrator: false);

    // Apache OrionMkII original (Orion-MkII firmware, 100 W) — Saturn-class
    // hardware fingerprint but without on-board telemetry / audio amp per
    // clsHardwareSpecific.cs:249-262 (HasVolts/Amps lists exclude
    // ORIONMKII) and :459-468 (HasAudioAmplifier excludes it too).
    private static readonly BoardCapabilities OrionMkIIOriginal = new(
        RxAdcCount: 2,
        MkiiBpf: true,
        AdcSupplyMv: 50,
        LrAudioSwap: false,
        HasVolts: false,
        HasAmps: false,
        HasAudioAmplifier: false,
        HasSteppedAttenuationRx2: true,
        SupportsPathIllustrator: false);

    private static readonly BoardCapabilities HermesLite2 = new(
        RxAdcCount: 1,
        MkiiBpf: false,
        AdcSupplyMv: 33,
        LrAudioSwap: false,
        HasVolts: false,
        HasAmps: false,
        HasAudioAmplifier: false,
        HasSteppedAttenuationRx2: false,
        SupportsPathIllustrator: false);
}
