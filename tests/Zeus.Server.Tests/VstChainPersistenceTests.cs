// SPDX-License-Identifier: GPL-2.0-or-later
//
// VstChainPersistenceTests — LiteDB round-trip for the VstChainStore.
// No sidecar required; the tests construct VstChainEntry directly,
// save it, and assert the reload preserves shape.
//
// Auto-save behavior of VstHostHostedService is exercised in
// integration tests where the sidecar is available; here we verify
// the persistence layer's contract in isolation.

using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class VstChainPersistenceTests : IDisposable
{
    private readonly string _tmpDb;

    public VstChainPersistenceTests()
    {
        _tmpDb = Path.Combine(Path.GetTempPath(),
            $"zeus-vstchain-tests-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { if (File.Exists(_tmpDb)) File.Delete(_tmpDb); } catch { }
    }

    [Fact]
    public void Load_FreshDb_Returns8EmptySlots_DefaultsOff()
    {
        using var store = new LiteDbVstChainPersistence(
            NullLogger<LiteDbVstChainPersistence>.Instance, _tmpDb);

        var doc = store.Load();
        Assert.False(doc.MasterEnabled);
        Assert.Equal(8, doc.Slots.Count);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(i, doc.Slots[i].Index);
            Assert.Null(doc.Slots[i].PluginPath);
            Assert.False(doc.Slots[i].Bypass);
            Assert.Empty(doc.Slots[i].Parameters);
        }
        Assert.Empty(doc.CustomSearchPaths);
    }

    [Fact]
    public void Save_RoundTrips_AcrossInstances()
    {
        // Persist a chain shape, then construct a new store and verify
        // the same shape comes back. Catches LiteDB mapper drift.
        var entry = new VstChainEntry
        {
            Id = 1,
            SchemaVersion = 1,
            MasterEnabled = true,
            CustomSearchPaths = new List<string> { "/opt/vst3-extra", "/home/op/.lxvst" },
            Slots = new List<VstChainSlotEntry>
            {
                new() { Index = 0, PluginPath = "/usr/lib/vst3/Foo.vst3",
                    Bypass = false,
                    Parameters = new() { ["42"] = 0.75, ["7"] = 0.0 } },
                new() { Index = 1, PluginPath = "/usr/lib/vst3/Bar.vst3",
                    Bypass = true,
                    Parameters = new() { ["100"] = 0.5 } },
                new() { Index = 2 },
                new() { Index = 3 },
                new() { Index = 4 },
                new() { Index = 5 },
                new() { Index = 6 },
                new() { Index = 7 },
            },
        };

        using (var writer = new LiteDbVstChainPersistence(
            NullLogger<LiteDbVstChainPersistence>.Instance, _tmpDb))
        {
            writer.Save(entry);
        }

        using var reader = new LiteDbVstChainPersistence(
            NullLogger<LiteDbVstChainPersistence>.Instance, _tmpDb);
        var loaded = reader.Load();

        Assert.True(loaded.MasterEnabled);
        Assert.Equal(2, loaded.CustomSearchPaths.Count);
        Assert.Contains("/opt/vst3-extra", loaded.CustomSearchPaths);
        Assert.Contains("/home/op/.lxvst", loaded.CustomSearchPaths);

        Assert.Equal(8, loaded.Slots.Count);
        Assert.Equal("/usr/lib/vst3/Foo.vst3", loaded.Slots[0].PluginPath);
        Assert.False(loaded.Slots[0].Bypass);
        Assert.Equal(2, loaded.Slots[0].Parameters.Count);
        Assert.Equal(0.75, loaded.Slots[0].Parameters["42"], precision: 6);
        Assert.Equal(0.0, loaded.Slots[0].Parameters["7"], precision: 6);

        Assert.Equal("/usr/lib/vst3/Bar.vst3", loaded.Slots[1].PluginPath);
        Assert.True(loaded.Slots[1].Bypass);
        Assert.Equal(0.5, loaded.Slots[1].Parameters["100"], precision: 6);

        // Empty slots round-trip with null path.
        for (int i = 2; i < 8; i++)
        {
            Assert.Null(loaded.Slots[i].PluginPath);
        }
    }

    [Fact]
    public void Save_OverwritesPreviousDocument()
    {
        // Single-document collection — Save must replace, not accumulate.
        using var store = new LiteDbVstChainPersistence(
            NullLogger<LiteDbVstChainPersistence>.Instance, _tmpDb);

        var first = new VstChainEntry { MasterEnabled = false };
        for (int i = 0; i < 8; i++) first.Slots.Add(new() { Index = i });
        store.Save(first);

        var second = new VstChainEntry { MasterEnabled = true };
        for (int i = 0; i < 8; i++) second.Slots.Add(new() { Index = i });
        second.Slots[3].PluginPath = "/foo.vst3";
        store.Save(second);

        var loaded = store.Load();
        Assert.True(loaded.MasterEnabled);
        Assert.Equal("/foo.vst3", loaded.Slots[3].PluginPath);
    }

    [Fact]
    public void Load_LegacyDocWithFewerSlots_BackfillsTo8()
    {
        // If a future schema version writes a doc with N<8 slots, Load
        // backfills to 8 so the host can index Slots[0..7] safely.
        using var store = new LiteDbVstChainPersistence(
            NullLogger<LiteDbVstChainPersistence>.Instance, _tmpDb);

        var partial = new VstChainEntry
        {
            MasterEnabled = false,
            Slots = new List<VstChainSlotEntry>
            {
                new() { Index = 0, PluginPath = "/a.vst3" },
                new() { Index = 1 },
                new() { Index = 2 },
            },
        };
        store.Save(partial);

        var loaded = store.Load();
        Assert.Equal(8, loaded.Slots.Count);
        Assert.Equal("/a.vst3", loaded.Slots[0].PluginPath);
        for (int i = 3; i < 8; i++)
        {
            Assert.Equal(i, loaded.Slots[i].Index);
            Assert.Null(loaded.Slots[i].PluginPath);
        }
    }
}
