// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using LiteDB;

namespace Zeus.Server;

// Persists the detached TX/RX Audio Suite Photino window geometry. These are
// native child windows opened from the desktop shell, so their size is not
// covered by the in-page AudioSuiteWindow Zustand placement state.
public sealed class AudioSuiteWindowGeometryStore : IDisposable
{
    public const int DefaultWidth = 860;
    public const int DefaultHeight = 760;
    public const int MinWidth = 600;
    public const int MinHeight = 520;

    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<AudioSuiteWindowGeometryEntry> _docs;
    private readonly ILogger<AudioSuiteWindowGeometryStore> _log;
    private readonly object _sync = new();

    public AudioSuiteWindowGeometryStore(
        ILogger<AudioSuiteWindowGeometryStore> log,
        string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _docs = _db.GetCollection<AudioSuiteWindowGeometryEntry>("audio_suite_window_geometry");
        _docs.EnsureIndex(x => x.Route, unique: true);

        _log.LogInformation("AudioSuiteWindowGeometryStore initialized at {Path}", dbPath);
    }

    public AudioSuiteWindowGeometry Get(string route)
    {
        var normalized = NormalizeRoute(route);
        lock (_sync)
        {
            var e = _docs.FindOne(x => x.Route == normalized);
            if (e is null)
                return new AudioSuiteWindowGeometry(DefaultWidth, DefaultHeight, Maximized: false);
            return new AudioSuiteWindowGeometry(
                ClampWidth(e.Width),
                ClampHeight(e.Height),
                e.Maximized);
        }
    }

    public void Save(string route, int width, int height, bool maximized)
    {
        var normalized = NormalizeRoute(route);
        var w = ClampWidth(width);
        var h = ClampHeight(height);
        lock (_sync)
        {
            var e = _docs.FindOne(x => x.Route == normalized) ?? new AudioSuiteWindowGeometryEntry
            {
                Route = normalized,
            };
            e.Width = w;
            e.Height = h;
            e.Maximized = maximized;
            e.UpdatedUtc = DateTime.UtcNow;
            if (e.Id == 0) _docs.Insert(e);
            else _docs.Update(e);
        }
    }

    private static string NormalizeRoute(string route) =>
        string.Equals(route, "rx", StringComparison.OrdinalIgnoreCase) ? "rx" : "tx";

    private static int ClampWidth(int v) => v < MinWidth ? DefaultWidth : v;
    private static int ClampHeight(int v) => v < MinHeight ? DefaultHeight : v;

    public void Dispose() => _dbLease.Dispose();
}

public readonly record struct AudioSuiteWindowGeometry(int Width, int Height, bool Maximized);

public sealed class AudioSuiteWindowGeometryEntry
{
    public int Id { get; set; }
    public string Route { get; set; } = "tx";
    public int Width { get; set; } = AudioSuiteWindowGeometryStore.DefaultWidth;
    public int Height { get; set; } = AudioSuiteWindowGeometryStore.DefaultHeight;
    public bool Maximized { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
