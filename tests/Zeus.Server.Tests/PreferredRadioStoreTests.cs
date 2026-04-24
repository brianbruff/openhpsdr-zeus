// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Protocol1.Discovery;
using Zeus.Server;

namespace Zeus.Server.Tests;

// Round-trip the preferred-radio preference. Tests use a per-test tmp DB
// path so the production zeus-prefs.db isn't mutated.
public class PreferredRadioStoreTests : IDisposable
{
    private readonly string _dbPath;

    public PreferredRadioStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-radio-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* test-only cleanup */ }
    }

    [Fact]
    public void Empty_Store_Returns_Null_Meaning_Auto()
    {
        using var store = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        Assert.Null(store.Get());
    }

    [Fact]
    public void Set_And_Get_Round_Trip_HermesLite2()
    {
        using var store = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        store.Set(HpsdrBoardKind.HermesLite2);
        Assert.Equal(HpsdrBoardKind.HermesLite2, store.Get());
    }

    [Fact]
    public void Set_Auto_Clears_Override()
    {
        using var store = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        store.Set(HpsdrBoardKind.OrionMkII);
        Assert.Equal(HpsdrBoardKind.OrionMkII, store.Get());
        store.Set(null);
        Assert.Null(store.Get());
    }

    [Fact]
    public void Setting_Unknown_Is_Treated_As_Auto()
    {
        // Unknown in the enum is a sentinel for "no radio" — persisting it
        // would just seed junk defaults, so the store collapses it to Auto.
        using var store = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        store.Set(HpsdrBoardKind.Hermes);
        store.Set(HpsdrBoardKind.Unknown);
        Assert.Null(store.Get());
    }

    [Fact]
    public void Persists_Across_Instances()
    {
        using (var s1 = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath))
        {
            s1.Set(HpsdrBoardKind.OrionMkII);
        }
        using var s2 = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        Assert.Equal(HpsdrBoardKind.OrionMkII, s2.Get());
    }

    [Fact]
    public void Changed_Fires_On_Set()
    {
        using var store = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        int fired = 0;
        store.Changed += () => fired++;
        store.Set(HpsdrBoardKind.Hermes);
        store.Set(null);
        Assert.Equal(2, fired);
    }
}
