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

using LiteDB;

namespace Zeus.Server;

// Layout collection persistence — stores multiple named layouts so operators
// can switch between different panel arrangements. Shares zeus-prefs.db with
// LayoutStore (which remains for backward compatibility).
public sealed class LayoutCollectionStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<LayoutCollectionEntry> _entries;
    private readonly ILogger<LayoutCollectionStore> _log;

    public LayoutCollectionStore(ILogger<LayoutCollectionStore> log)
    {
        _log = log;
        var dbPath = GetDatabasePath();

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _entries = _db.GetCollection<LayoutCollectionEntry>("ui_layout_collection");
        _entries.EnsureIndex(x => x.ProfileId, unique: true);

        _log.LogInformation("LayoutCollectionStore initialized at {Path}", dbPath);
    }

    public LayoutCollectionDto? Get(string profileId = "default")
    {
        var e = _entries.FindOne(x => x.ProfileId == profileId);
        return e is null ? null : new LayoutCollectionDto(e.Layouts, e.ActiveLayoutId);
    }

    public void Upsert(LayoutCollectionDto dto, string profileId = "default")
    {
        var existing = _entries.FindOne(x => x.ProfileId == profileId);
        if (existing is null)
        {
            _entries.Insert(new LayoutCollectionEntry
            {
                ProfileId = profileId,
                Layouts = dto.Layouts,
                ActiveLayoutId = dto.ActiveLayoutId,
                UpdatedUtc = DateTime.UtcNow,
            });
        }
        else
        {
            existing.Layouts = dto.Layouts;
            existing.ActiveLayoutId = dto.ActiveLayoutId;
            existing.UpdatedUtc = DateTime.UtcNow;
            _entries.Update(existing);
        }
    }

    public void Delete(string profileId = "default")
        => _entries.DeleteMany(x => x.ProfileId == profileId);

    public void Dispose() => _db.Dispose();

    private static string GetDatabasePath()
    {
        var appDataDir = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        return Path.Combine(appDataDir, "Zeus", "zeus-prefs.db");
    }
}

public sealed class LayoutCollectionEntry
{
    public int Id { get; set; }
    public string ProfileId { get; set; } = string.Empty;
    public LayoutItemDto[] Layouts { get; set; } = Array.Empty<LayoutItemDto>();
    public string ActiveLayoutId { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}

public record LayoutCollectionDto(LayoutItemDto[] Layouts, string ActiveLayoutId);

public record LayoutItemDto(string Id, string Name, string LayoutJson);
