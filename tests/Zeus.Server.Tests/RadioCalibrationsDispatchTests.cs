// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

using Xunit;
using Zeus.Contracts;
using Zeus.Protocol1.Discovery;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// <see cref="RadioCalibrations.For"/> must dispatch each connected board
/// kind to the right calibration record. Constants come from Thetis
/// <c>console.cs:25053-25118</c> (computeAlexFwdPower); regressing the
/// dispatch silently mis-scales the operator's TX power meter.
///
/// Issue #174 — added when P2 hi-pri telemetry was wired into the meter
/// pipeline; before that point the meter ignored everything except HL2.
/// </summary>
public class RadioCalibrationsDispatchTests
{
    [Fact]
    public void HermesLite2_GetsHl2Bridge()
    {
        var cal = RadioCalibrations.For(HpsdrBoardKind.HermesLite2);
        Assert.Same(RadioCalibration.HermesLite2, cal);
        Assert.Equal(1.5, cal.BridgeVolt);
    }

    [Fact]
    public void Hermes_Metis_Griffin_GetsHermesBridge()
    {
        Assert.Same(RadioCalibration.Hermes, RadioCalibrations.For(HpsdrBoardKind.Hermes));
        Assert.Same(RadioCalibration.Hermes, RadioCalibrations.For(HpsdrBoardKind.Metis));
        Assert.Same(RadioCalibration.Hermes, RadioCalibrations.For(HpsdrBoardKind.Griffin));
    }

    [Fact]
    public void Angelia_GetsAnan100Bridge()
    {
        var cal = RadioCalibrations.For(HpsdrBoardKind.Angelia);
        Assert.Same(RadioCalibration.Anan100, cal);
        Assert.Equal(0.095, cal.BridgeVolt);
        Assert.Equal(3.3, cal.RefVoltage);
        Assert.Equal(6, cal.AdcCalOffset);
    }

    [Fact]
    public void Orion_GetsAnan200Bridge()
    {
        var cal = RadioCalibrations.For(HpsdrBoardKind.Orion);
        Assert.Same(RadioCalibration.Anan200, cal);
        Assert.Equal(0.108, cal.BridgeVolt);
        Assert.Equal(5.0, cal.RefVoltage);
        Assert.Equal(4, cal.AdcCalOffset);
    }

    [Fact]
    public void OrionMkII_GetsG2Bridge_NotAnan8000Bridge()
    {
        // Board id 0x0A aliases ANAN-7000 / G1 / G2 / G2-1K / RedPitaya
        // (Thetis ANAN_G2 — bridge 0.12 / ref 5.0 / offset 32) and
        // ANAN-8000D (Thetis ORIONMKII — bridge 0.08 / ref 5.0 / offset 18).
        // The default dispatch picks the G2 bucket because that is what
        // KB2UKA's test rig reports. ANAN-8000D operators may see a
        // ~30 % low FWD reading — see RadioCalibration.OrionMkIIAnan8000.
        var cal = RadioCalibrations.For(HpsdrBoardKind.OrionMkII);
        Assert.Same(RadioCalibration.OrionMkII, cal);
        Assert.Equal(0.12, cal.BridgeVolt);
        Assert.Equal(5.0, cal.RefVoltage);
        Assert.Equal(32, cal.AdcCalOffset);
    }

    [Fact]
    public void Unknown_FallsBackToHl2_NotZero()
    {
        // A divide-by-zero in ComputeMeters would surface as Infinity / NaN
        // on the meter — fall back to a sane bucket instead.
        var cal = RadioCalibrations.For(HpsdrBoardKind.Unknown);
        Assert.True(cal.BridgeVolt > 0);
        Assert.True(cal.RefVoltage > 0);
    }
}
