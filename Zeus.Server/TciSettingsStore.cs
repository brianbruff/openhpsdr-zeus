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

using LiteDB;

namespace Zeus.Server;

// Persists the operator's TCI enabled/port preference across server restarts.
// TCI is disabled by default — enabling it requires an explicit operator action.
// Port changes and new enables take effect on next server start (Kestrel binds at startup).
public sealed class TciSettingsStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<TciSettingsEntry> _entries;

    public TciSettingsStore(string? dbPathOverride = null)
    {
        var dbPath = dbPathOverride ?? GetDatabasePath();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _entries = _db.GetCollection<TciSettingsEntry>("tci_settings");
        _entries.EnsureIndex(x => x.ProfileId, unique: true);
    }

    public TciSettingsEntry Get(string profileId = "default")
        => _entries.FindOne(x => x.ProfileId == profileId) ?? new TciSettingsEntry();

    public void Upsert(TciSettingsEntry entry, string profileId = "default")
    {
        entry.ProfileId = profileId;
        entry.UpdatedUtc = DateTime.UtcNow;
        var existing = _entries.FindOne(x => x.ProfileId == profileId);
        if (existing is null)
            _entries.Insert(entry);
        else
        {
            entry.Id = existing.Id;
            _entries.Update(entry);
        }
    }

    // Called before DI builds so Kestrel knows whether to bind the TCI port.
    public static TciSettingsEntry ReadDirect(string dbPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            using var db = new LiteDatabase($"Filename={dbPath};Connection=shared");
            var col = db.GetCollection<TciSettingsEntry>("tci_settings");
            return col.FindOne(x => x.ProfileId == "default") ?? new TciSettingsEntry();
        }
        catch
        {
            return new TciSettingsEntry();
        }
    }

    public static string GetDatabasePath()
    {
        var appDataDir = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        return Path.Combine(appDataDir, "Zeus", "zeus-prefs.db");
    }

    public void Dispose() => _db.Dispose();
}

public sealed class TciSettingsEntry
{
    public int Id { get; set; }
    public string ProfileId { get; set; } = string.Empty;
    // Off by default — no surprise port conflicts for new users.
    public bool Enabled { get; set; } = false;
    public int Port { get; set; } = 40001;
    public string BindAddress { get; set; } = "127.0.0.1";
    public DateTime UpdatedUtc { get; set; }
}
