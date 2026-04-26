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
// Wüllen (DL1YCF), with contributions from (DH1KLM); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

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

    [Fact]
    public void HermesLite2_Both_Protocols_Use_0_233()
    {
        // MI0BOT special-case (HL2 only). Same value either protocol —
        // the HL2 hardware peak is determined by the mod, not the protocol.
        Assert.Equal(0.233, RadioService.ResolvePsHwPeak(false, HpsdrBoardKind.HermesLite2));
        Assert.Equal(0.233, RadioService.ResolvePsHwPeak(true, HpsdrBoardKind.HermesLite2));
    }
}
