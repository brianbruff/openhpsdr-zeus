// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Protocol1.Discovery;
using Zeus.Server;

namespace Zeus.Server.Tests;

// Defaults have to be correct on first connect — operator sees them before
// any calibration. Pin the per-board seeds so a "cosmetic" refactor in
// PaDefaults doesn't silently flip HL2 back to the old piHPSDR 40.5 dB
// mis-interpretation (see docs/lessons/hl2-drive-model.md).
//
// These tests use a per-class temp DB to stay hermetic — pre-isolation they
// shared zeus-prefs.db with production and any operator APPLY would break
// GetAll-based defaults tests by returning stored values ahead of
// PaDefaults.
public class PaSettingsStoreDefaultsTests : IDisposable
{
    private readonly string _dbPath;

    public PaSettingsStoreDefaultsTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-pasettings-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private PaSettingsStore NewStore() =>
        new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath);

    // HL2 uses a PERCENTAGE model (mi0bot openhpsdr-thetis), not the dB
    // model every other HPSDR radio uses. PaGainDb on HL2 is output % 0..100
    // — HF bands default to 100 (no attenuation), 6 m drops to 38.8
    // (stock-PA-gain-limited; matches clsHardwareSpecific.cs:767-795 in the
    // mi0bot fork).
    [Fact]
    public void Hl2_Default_Is_100_Percent_On_HF_And_38_8_On_6m()
    {
        using var store = NewStore();
        var s = store.GetAll(HpsdrBoardKind.HermesLite2);

        Assert.Equal(BandUtils.HfBands.Count, s.Bands.Count);
        foreach (var b in s.Bands.Where(x => x.Band != "6m"))
        {
            Assert.Equal(100.0, b.PaGainDb);
        }
        Assert.Equal(38.8, FindGain(s, "6m"));
    }

    [Fact]
    public void Hermes_Defaults_Match_Thetis_Table()
    {
        using var store = NewStore();
        var s = store.GetAll(HpsdrBoardKind.Hermes);
        // Spot-check against Thetis clsHardwareSpecific.cs:482-513.
        Assert.Equal(41.0, FindGain(s, "160m"));
        Assert.Equal(40.5, FindGain(s, "20m"));
        Assert.Equal(38.8, FindGain(s, "10m"));
    }

    [Fact]
    public void OrionMkII_Uses_G2_Class_Defaults()
    {
        using var store = NewStore();
        var s = store.GetAll(HpsdrBoardKind.OrionMkII);
        // ANAN7000/G1/G2/ANVELINAPRO3 bracket — Thetis clsHardwareSpecific.cs:696-728.
        Assert.Equal(47.9, FindGain(s, "160m"));
        Assert.Equal(50.9, FindGain(s, "20m"));
        Assert.Equal(44.6, FindGain(s, "6m"));
    }

    [Fact]
    public void Unknown_Board_Returns_Zero_Gain_For_Legacy_Path()
    {
        using var store = NewStore();
        var s = store.GetAll(HpsdrBoardKind.Unknown);
        // 0 dB combined with maxW=0 in ComputeDriveByte short-circuits to the
        // pct×255/100 legacy mapping — first boot behaves as before PA Settings.
        foreach (var b in s.Bands)
        {
            Assert.Equal(0.0, b.PaGainDb);
        }
    }

    [Fact]
    public void GetAll_Returns_All_11_Hf_Bands_In_Canonical_Order()
    {
        using var store = NewStore();
        var s = store.GetAll(HpsdrBoardKind.HermesLite2);
        Assert.Equal(BandUtils.HfBands.ToArray(), s.Bands.Select(b => b.Band).ToArray());
    }

    [Fact]
    public void GetDefaults_Ignores_Stored_Calibration()
    {
        // Reset-to-defaults must stomp any saved per-band tweak. Asking for
        // pure HL2 defaults returns 100 % on HF / 38.8 % on 6 m regardless
        // of whatever is stored — percentage model, not dB (see
        // HermesLite2DriveProfile and docs/lessons/hl2-drive-model.md).
        using var store = NewStore();
        var d = store.GetDefaults(HpsdrBoardKind.HermesLite2);
        Assert.Equal(5, d.Global.PaMaxPowerWatts);
        Assert.True(d.Global.PaEnabled);
        foreach (var b in d.Bands.Where(x => x.Band != "6m"))
        {
            Assert.Equal(100.0, b.PaGainDb);
        }
        Assert.Equal(38.8, FindGain(d, "6m"));
    }

    [Fact]
    public void GetDefaults_OrionMkII_Uses_G2_Table()
    {
        using var store = NewStore();
        var d = store.GetDefaults(HpsdrBoardKind.OrionMkII);
        Assert.Equal(100, d.Global.PaMaxPowerWatts);
        Assert.Equal(47.9, FindGain(d, "160m"));
        Assert.Equal(50.9, FindGain(d, "20m"));
    }

    private static double FindGain(PaSettingsDto s, string band) =>
        s.Bands.First(b => b.Band == band).PaGainDb;
}
