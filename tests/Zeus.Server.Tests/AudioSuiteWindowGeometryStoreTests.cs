// SPDX-License-Identifier: GPL-2.0-or-later
//
// AudioSuiteWindowGeometryStore - persists detached TX/RX Audio Suite native
// child-window sizes independently of the main desktop frame geometry.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class AudioSuiteWindowGeometryStoreTests : IDisposable
{
    private readonly string _dbPath;

    public AudioSuiteWindowGeometryStoreTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"zeus-prefs-audio-suite-window-geometry-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private AudioSuiteWindowGeometryStore NewStore() =>
        new(NullLogger<AudioSuiteWindowGeometryStore>.Instance, _dbPath);

    [Fact]
    public void FirstRun_ReturnsDefaultSize()
    {
        using var store = NewStore();

        var tx = store.Get("tx");
        var rx = store.Get("rx");

        Assert.Equal(AudioSuiteWindowGeometryStore.DefaultWidth, tx.Width);
        Assert.Equal(AudioSuiteWindowGeometryStore.DefaultHeight, tx.Height);
        Assert.False(tx.Maximized);
        Assert.Equal(AudioSuiteWindowGeometryStore.DefaultWidth, rx.Width);
        Assert.Equal(AudioSuiteWindowGeometryStore.DefaultHeight, rx.Height);
        Assert.False(rx.Maximized);
    }

    [Fact]
    public void SaveThenGet_RoundTripsRouteGeometry()
    {
        using var store = NewStore();

        store.Save("tx", 1040, 720, maximized: false);
        store.Save("rx", 980, 640, maximized: true);

        Assert.Equal(new AudioSuiteWindowGeometry(1040, 720, false), store.Get("tx"));
        Assert.Equal(new AudioSuiteWindowGeometry(980, 640, true), store.Get("rx"));
    }

    [Fact]
    public void GeometryPersistsAcrossStoreInstances()
    {
        using (var first = NewStore())
        {
            first.Save("tx", 1120, 780, maximized: true);
        }

        using var second = NewStore();

        Assert.Equal(new AudioSuiteWindowGeometry(1120, 780, true), second.Get("tx"));
    }

    [Fact]
    public void InvalidSmallGeometryFallsBackToDefault()
    {
        using var store = NewStore();

        store.Save("tx", 200, 120, maximized: false);
        var got = store.Get("tx");

        Assert.Equal(AudioSuiteWindowGeometryStore.DefaultWidth, got.Width);
        Assert.Equal(AudioSuiteWindowGeometryStore.DefaultHeight, got.Height);
        Assert.False(got.Maximized);
    }
}
