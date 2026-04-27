// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Persistence coverage for the issue #79 Phase 2 NR4 / NR2 popover work.
// All 12 nullable scalar fields on DspSettingsEntry must round-trip through
// LiteDB so the operator's per-popover tunings survive a backend restart.
// Lazy-default behaviour: a null in the DB stays null in NrConfig (the
// engine layer falls back to NrDefaults at apply time, so clearing a field
// later reverts to the default without a migration).

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Zeus.Contracts;

namespace Zeus.Server.Tests;

public class Nr4SettingsPersistenceTests : IDisposable
{
    // Per-fixture temp DB so xUnit class-level parallelism can't collide on
    // the shared zeus-prefs.db (Linux: ~/.local/share/Zeus/zeus-prefs.db).
    // Without this, parallel construction of LiteDB instances against the
    // same file races the BsonMapper and intermittently fails LINQ
    // expression compilation. Pattern mirrors ZoomValidationTests.
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-nr4-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private DspSettingsStore BuildStore() =>
        new(NullLogger<DspSettingsStore>.Instance, _dbPath);

    [Fact]
    public void SetNr4Config_PersistsAllFields()
    {
        var cfg = new NrConfig(
            NrMode: NrMode.Sbnr,
            Nr4ReductionAmount: 14.5,
            Nr4SmoothingFactor: 0.3,
            Nr4WhiteningFactor: 0.1,
            Nr4NoiseRescale: 1.75,
            Nr4PostFilterThreshold: -3.0,
            Nr4NoiseScalingType: 2,
            Nr4Position: 0);

        using (var store = BuildStore())
        {
            store.Upsert(cfg);
        }

        // Reopen the store to prove the fields survived the LiteDB file
        // round-trip (not just an in-memory cache).
        using (var store = BuildStore())
        {
            var back = store.Get();
            Assert.NotNull(back);
            Assert.Equal(NrMode.Sbnr, back!.NrMode);
            Assert.Equal(14.5, back.Nr4ReductionAmount);
            Assert.Equal(0.3, back.Nr4SmoothingFactor);
            Assert.Equal(0.1, back.Nr4WhiteningFactor);
            Assert.Equal(1.75, back.Nr4NoiseRescale);
            Assert.Equal(-3.0, back.Nr4PostFilterThreshold);
            Assert.Equal(2, back.Nr4NoiseScalingType);
            Assert.Equal(0, back.Nr4Position);
        }
    }

    [Fact]
    public void GetNr4Config_NullFields_ReturnNullToCallerForDefaultFallback()
    {
        // Fresh write with NR4 fields left null. The store must hand the
        // nulls back so the engine's "null = use NrDefaults" lazy-default
        // contract is honoured. Hard-coding defaults at write time would
        // break the operator's "clear field to revert" workflow.
        var cfg = new NrConfig(NrMode: NrMode.Off);

        using (var store = BuildStore()) store.Upsert(cfg);

        using var fresh = BuildStore();
        var back = fresh.Get();
        Assert.NotNull(back);
        Assert.Null(back!.Nr4ReductionAmount);
        Assert.Null(back.Nr4SmoothingFactor);
        Assert.Null(back.Nr4WhiteningFactor);
        Assert.Null(back.Nr4NoiseRescale);
        Assert.Null(back.Nr4PostFilterThreshold);
        Assert.Null(back.Nr4NoiseScalingType);
        Assert.Null(back.Nr4Position);
    }

    [Fact]
    public void SetNr2Post2Config_PersistsAllFields()
    {
        var cfg = new NrConfig(
            NrMode: NrMode.Emnr,
            EmnrPost2Run: false,
            EmnrPost2Factor: 0.22,
            EmnrPost2Nlevel: 0.18,
            EmnrPost2Rate: 4.0,
            EmnrPost2Taper: 8);

        using (var store = BuildStore()) store.Upsert(cfg);

        using var fresh = BuildStore();
        var back = fresh.Get();
        Assert.NotNull(back);
        Assert.Equal(NrMode.Emnr, back!.NrMode);
        Assert.Equal(false, back.EmnrPost2Run);
        Assert.Equal(0.22, back.EmnrPost2Factor);
        Assert.Equal(0.18, back.EmnrPost2Nlevel);
        Assert.Equal(4.0, back.EmnrPost2Rate);
        Assert.Equal(8, back.EmnrPost2Taper);
    }

    // Upsert path — overwrite an existing row's NR4 fields. Catches a class
    // of bug where the update branch forgets to assign one of the new
    // fields (so the second write silently drops it).
    [Fact]
    public void SetNr4Config_UpsertOverwritesExistingFields()
    {
        var first = new NrConfig(
            NrMode: NrMode.Sbnr,
            Nr4ReductionAmount: 5.0,
            Nr4Position: 1);
        var second = new NrConfig(
            NrMode: NrMode.Sbnr,
            Nr4ReductionAmount: 20.0,
            Nr4Position: 0);

        using var store = BuildStore();
        store.Upsert(first);
        store.Upsert(second);

        var back = store.Get();
        Assert.NotNull(back);
        Assert.Equal(20.0, back!.Nr4ReductionAmount);
        Assert.Equal(0, back.Nr4Position);
    }
}
