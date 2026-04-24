// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Per-radio quirks in how the drive-level byte actually makes it from
// Zeus to RF. Every HPSDR radio ostensibly speaks the same protocol 1 /
// protocol 2 wire format, but what each one DOES with the byte we put
// in C0=0x12, C1=drive_level varies:
//
//   - Hermes / ANAN-10 / ANAN-100 / Orion / G2  — use all 8 bits.
//     Fine-grained drive control; the classic Thetis/piHPSDR math
//     (target watts → dBm − PA gain → volts @ 50 Ω → byte/255 × 0.8 V)
//     maps cleanly to output power.
//
//   - Hermes-Lite 2 — reads ONLY bits [31:28] of the drive register.
//     The bottom nibble is silently discarded by the HL2 gateware.
//     That means the 8-bit byte the math produces is quantised to
//     one of 16 power steps. A "correct-looking" byte like 48
//     (from piHPSDR's generic pa_calibration=40.5 with 5 W rated PA)
//     lands in nibble 0x3 → 3/15 = 20 % of max drive, capping
//     output at 1–2 W no matter how precise the IQ or how correct
//     the packet rate. See docs/references/protocol-1/
//     hermes-lite2-protocol.md:51 and docs/lessons/
//     hl2-drive-byte-quantization.md.
//
// IMPORTANT for anyone touching TX / PA / drive-byte code:
//
//   Go through this abstraction. Do not hard-code an 8-bit linear
//   voltage model and expect it to "just work" on every board — it
//   won't on HL2, and the silence on the bench can eat a day before
//   you realise the bytes you're computing aren't the bytes the
//   radio is honouring. Add new board quirks by implementing
//   IRadioDriveProfile and extending RadioDriveProfiles.For.
//
// Reference implementations:
//   - piHPSDR radio.c:2809-2828 (8-bit, no HL2 quantisation — HL2
//     users happen to land drive high enough that nibble 0xF is
//     reached, which is why their "it works" setups work).
//   - Thetis console.cs:46801-46841 (8-bit, no HL2-specific branch).
//   - mi0bot/openhpsdr-thetis — HL2-specific fork; look there for
//     further HL2 quirks Zeus may need to mirror.

using Zeus.Protocol1.Discovery;

namespace Zeus.Server;

/// <summary>
/// Encapsulates per-board drive-byte encoding. Implementations convert a
/// calibrated drive % / PA-gain / max-watts triple into the final byte that
/// will be written to C0=0x12, C1 on the wire.
/// </summary>
public interface IRadioDriveProfile
{
    /// <summary>
    /// Board this profile targets. Diagnostic only.
    /// </summary>
    string BoardLabel { get; }

    /// <summary>
    /// Produce the byte to send in the DriveFilter C1 slot.
    /// </summary>
    /// <param name="drivePct">Operator slider position, 0..100.</param>
    /// <param name="paGainDb">Per-band PA forward gain from PaSettingsStore.</param>
    /// <param name="maxWatts">Rated PA output watts. 0 triggers the legacy
    /// straight-percent-to-byte mapping that pre-dates the PA math.</param>
    byte EncodeDriveByte(int drivePct, double paGainDb, int maxWatts);
}

/// <summary>
/// Shared watts → drive-byte math used by every profile as the baseline.
/// Pure function, deterministic, unit-tested. Operator-facing calibration
/// lives in PaSettingsStore; this does not touch storage.
///
/// Reference: Thetis <c>console.cs:46801-46841</c>, piHPSDR
/// <c>radio.c:2809-2828</c>.
/// </summary>
internal static class DriveByteMath
{
    public static byte ComputeFullByte(int drivePct, double paGainDb, int maxWatts)
    {
        drivePct = Math.Clamp(drivePct, 0, 100);
        if (maxWatts <= 0)
        {
            return (byte)(drivePct * 255 / 100);
        }

        double targetWatts = maxWatts * drivePct / 100.0;
        if (targetWatts <= 0) return 0;

        double sourceWatts = targetWatts / Math.Pow(10.0, paGainDb / 10.0);
        double sourceVolts = Math.Sqrt(sourceWatts * 50.0);
        double norm = Math.Clamp(sourceVolts / 0.8, 0.0, 1.0);
        return (byte)Math.Round(norm * 255.0);
    }
}

/// <summary>
/// Default 8-bit profile for Hermes, ANAN-10/100/100D/200D/8000D, Orion,
/// Orion MkII (G1/G2/G2-1K) and anything else that honours the full drive
/// byte. No quantisation — the computed byte goes straight to the wire.
/// </summary>
public sealed class FullByteDriveProfile : IRadioDriveProfile
{
    public static readonly FullByteDriveProfile Instance = new();
    private FullByteDriveProfile() { }

    public string BoardLabel => "FullByte (8-bit)";

    public byte EncodeDriveByte(int drivePct, double paGainDb, int maxWatts)
        => DriveByteMath.ComputeFullByte(drivePct, paGainDb, maxWatts);
}

/// <summary>
/// Hermes-Lite 2 profile. Quantises the full-byte math down to the 16-step
/// scale the HL2 gateware actually honours.
///
/// HL2 uses only bits [31:28] of the Hermes TX drive-level register — the
/// bottom 4 bits are silently discarded. Without this rounding, an
/// operator who calibrates to hit the nibble boundary "works"; one who
/// lands between boundaries sees no power change until their gain slider
/// crosses the next 16-count step. Rounding to the nearest nibble-step
/// here makes the slider honest.
///
/// Reference: docs/references/protocol-1/hermes-lite2-protocol.md:51
/// </summary>
public sealed class HermesLite2DriveProfile : IRadioDriveProfile
{
    public static readonly HermesLite2DriveProfile Instance = new();
    private HermesLite2DriveProfile() { }

    public string BoardLabel => "HermesLite2 (4-bit)";

    public byte EncodeDriveByte(int drivePct, double paGainDb, int maxWatts)
    {
        byte raw = DriveByteMath.ComputeFullByte(drivePct, paGainDb, maxWatts);
        // Round to the nearest nibble-step. 0→0x00, 8→0x10, 24→0x20, …, 248→0xF0.
        // Saturate at 15 so we never overflow the register.
        int nibble = (int)Math.Round(raw / 16.0);
        if (nibble > 15) nibble = 15;
        return (byte)(nibble * 16);
    }
}

/// <summary>
/// Per-board dispatch. Extend this switch whenever a new board needs a
/// non-default drive encoding. Anything not explicitly mapped falls through
/// to <see cref="FullByteDriveProfile"/>, which is the correct choice for
/// every full-Hermes-class radio.
/// </summary>
public static class RadioDriveProfiles
{
    public static IRadioDriveProfile For(HpsdrBoardKind board) => board switch
    {
        HpsdrBoardKind.HermesLite2 => HermesLite2DriveProfile.Instance,
        _                          => FullByteDriveProfile.Instance,
    };
}
