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
using Zeus.Server;
using Xunit;

namespace Zeus.Server.Tests;

/// <summary>
/// Per-protocol / per-board PureSignal HW-peak resolution. Sourced from
/// Thetis clsHardwareSpecific.cs:295-318 + pihpsdr transmitter.c:1166-1179.
/// These values land via SetPSHWPeak to the WDSP calcc stage; getting them
/// wrong by even a few percent makes the correction curve fight the radio.
/// </summary>
public class PsHwPeakResolutionTests
{
    [Fact]
    public void Protocol1_Hermes_Defaults_To_0_4072()
    {
        Assert.Equal(0.4072, RadioService.ResolvePsHwPeak(false, HpsdrBoardKind.Hermes));
        Assert.Equal(0.4072, RadioService.ResolvePsHwPeak(false, HpsdrBoardKind.Angelia));
        Assert.Equal(0.4072, RadioService.ResolvePsHwPeak(false, HpsdrBoardKind.Orion));
    }

    [Fact]
    public void Protocol2_OrionMkII_Defaults_To_0_6121()
    {
        Assert.Equal(0.6121, RadioService.ResolvePsHwPeak(true, HpsdrBoardKind.OrionMkII));
    }

    [Fact]
    public void Protocol2_Default_Is_0_2899()
    {
        Assert.Equal(0.2899, RadioService.ResolvePsHwPeak(true, HpsdrBoardKind.Hermes));
        Assert.Equal(0.2899, RadioService.ResolvePsHwPeak(true, HpsdrBoardKind.Angelia));
        Assert.Equal(0.2899, RadioService.ResolvePsHwPeak(true, HpsdrBoardKind.Unknown));
    }

    [Theory]
    [InlineData(HpsdrBoardKind.HermesLite2)]
    [InlineData(HpsdrBoardKind.Brick2Sdr)]
    public void Hl2Class_Both_Protocols_Use_0_233(HpsdrBoardKind board)
    {
        // AD9866-based boards (HL2 and Brick2SDR HL2 clone). Same value
        // either protocol — the hardware peak is determined by the ADC mod,
        // not the protocol.
        Assert.Equal(0.233, RadioService.ResolvePsHwPeak(false, board));
        Assert.Equal(0.233, RadioService.ResolvePsHwPeak(true, board));
    }
}
