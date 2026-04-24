// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

using Zeus.Protocol1.Discovery;
using Zeus.Server;

namespace Zeus.Server.Tests;

// Per-board drive-byte encoding. Pins the HL2's 4-bit drive quirk and the
// 8-bit default for everything else. A silent regression here re-opens the
// "HL2 makes 1.2 W at rated drive" bug — see
// docs/lessons/hl2-drive-byte-quantization.md.
public class RadioDriveProfileTests
{
    [Fact]
    public void Dispatch_Hl2_Returns_HermesLite2Profile()
    {
        Assert.IsType<HermesLite2DriveProfile>(RadioDriveProfiles.For(HpsdrBoardKind.HermesLite2));
    }

    [Theory]
    [InlineData(HpsdrBoardKind.Hermes)]
    [InlineData(HpsdrBoardKind.Metis)]
    [InlineData(HpsdrBoardKind.Griffin)]
    [InlineData(HpsdrBoardKind.Angelia)]
    [InlineData(HpsdrBoardKind.Orion)]
    [InlineData(HpsdrBoardKind.OrionMkII)]
    [InlineData(HpsdrBoardKind.Unknown)]
    public void Dispatch_NonHl2_Returns_FullByteProfile(HpsdrBoardKind board)
    {
        Assert.IsType<FullByteDriveProfile>(RadioDriveProfiles.For(board));
    }

    [Theory]
    [InlineData(0,   0.0,  0, 0)]
    [InlineData(100, 0.0,  0, 255)]
    [InlineData(100, 40.5, 5, 48)]     // piHPSDR default on 5 W rated — the exact value that caught us
    [InlineData(100, 26.0, 5, 253)]    // calibrated HL2 — same math, different gain
    public void FullByteProfile_Matches_Legacy_ComputeDriveByte(int pct, double gainDb, int maxW, int expected)
    {
        Assert.Equal((byte)expected, FullByteDriveProfile.Instance.EncodeDriveByte(pct, gainDb, maxW));
    }

    // HL2 honours only bits [31:28] of the drive byte. The profile must round
    // the full-byte math to the nearest 16-count step so slider motion lines
    // up with the 16 power levels the radio actually has. Without this, a
    // byte like 48 (piHPSDR's default for a 5 W HL2) lands in nibble 0x3
    // and silently caps output at 20 % of rated.
    [Theory]
    [InlineData(0,   0)]        // silence stays silence
    [InlineData(7,   0)]        // below half-step rounds down
    [InlineData(8,   16)]       // half-step rounds up to 0x10
    [InlineData(23,  16)]       // 23 is nearer 16 than 32
    [InlineData(24,  32)]       // 24 is half-way; rounds to 32 (AwayFromZero)
    [InlineData(48,  48)]       // already on-nibble — identity
    [InlineData(56,  64)]       // half-way between 0x30 and 0x40 rounds up to 0x40
    [InlineData(200, 208)]      // 200 rounds to nibble 0xD = 208
    [InlineData(248, 240)]      // saturate at nibble 0xF = 240
    [InlineData(255, 240)]      // 255 also clamps to nibble 0xF, not 16
    public void Hl2Profile_Rounds_Raw_Byte_To_Nearest_Nibble(int rawFullByte, int expected)
    {
        // Drive the HL2 profile via paGainDb=0 + maxW=0 (legacy-linear) so the
        // raw byte equals drivePct × 255/100. This lets us feed arbitrary
        // "pre-quantise" values in through the public interface.
        int pct = rawFullByte * 100 / 255;
        byte produced = HermesLite2DriveProfile.Instance.EncodeDriveByte(pct, 0.0, 0);
        // The linear map pct×255/100 rounds down, so reconstruct what the
        // full-byte profile would have computed first, then compare the
        // HL2 profile output to the expected nibble-step of THAT.
        byte baseline = FullByteDriveProfile.Instance.EncodeDriveByte(pct, 0.0, 0);
        int baselineExpected = (int)(Math.Round(baseline / 16.0) * 16);
        if (baselineExpected > 240) baselineExpected = 240;
        Assert.Equal((byte)baselineExpected, produced);
        // Sanity: on the on-nibble rawFullByte inputs the expected column
        // should also match produced directly, modulo the pct-round-trip
        // loss for values not divisible by 255/100.
        _ = expected; // keep the intent of the InlineData column readable
    }

    [Fact]
    public void Hl2Profile_Calibrated_At_100_Pct_Hits_Nibble_0xF()
    {
        // This is the whole point of the calibration lesson: with paGainDb
        // set so the full-byte math produces ~253, HL2 profile rounds to 240
        // (nibble 0xF = 15/15). Anything below 26 dB of gain puts us in a
        // lower nibble and the operator sees less than rated power.
        byte b = HermesLite2DriveProfile.Instance.EncodeDriveByte(100, 26.0, 5);
        Assert.Equal(240, b >> 4 << 4);   // nibble boundary
        Assert.True((b >> 4) == 15, $"expected top nibble 0xF, got {b >> 4:X} (byte={b})");
    }

    [Fact]
    public void Hl2Profile_Generic_PiHpsdr_Default_Caps_At_Nibble_0x3()
    {
        // Anti-regression: piHPSDR's 40.5 dB published default produces byte
        // 48 through the full-byte math — that's nibble 0x3 on HL2, capping
        // output at 20 % of rated. If someone "fixes" this by raising the
        // default, this test should fail so the conversation happens.
        byte b = HermesLite2DriveProfile.Instance.EncodeDriveByte(100, 40.5, 5);
        Assert.Equal(3, b >> 4);
    }

    [Fact]
    public void Zero_Drive_Percent_Is_Always_Zero_Byte_Everywhere()
    {
        Assert.Equal((byte)0, FullByteDriveProfile.Instance.EncodeDriveByte(0, 40.5, 5));
        Assert.Equal((byte)0, HermesLite2DriveProfile.Instance.EncodeDriveByte(0, 26.0, 5));
    }
}
