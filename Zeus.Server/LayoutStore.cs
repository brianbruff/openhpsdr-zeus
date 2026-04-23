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
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

// UI layout persistence — stores the opaque flexlayout-react JSON blob so the
// operator's panel arrangement survives page reloads and reinstalls.
// Shares zeus-prefs.db with BandMemoryStore (layout isn't sensitive).
public sealed class LayoutStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<LayoutEntry> _entries;
    private readonly ILogger<LayoutStore> _log;

    public LayoutStore(ILogger<LayoutStore> log)
    {
        _log = log;
        var dbPath = GetDatabasePath();

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _entries = _db.GetCollection<LayoutEntry>("ui_layout");
        _entries.EnsureIndex(x => x.ProfileId, unique: true);

        _log.LogInformation("LayoutStore initialized at {Path}", dbPath);
    }

    public UiLayoutDto? Get(string profileId = "default")
    {
        var e = _entries.FindOne(x => x.ProfileId == profileId);
        return e is null
            ? null
            : new UiLayoutDto(e.LayoutJson, new DateTimeOffset(e.UpdatedUtc).ToUnixTimeMilliseconds());
    }

    public void Upsert(string layoutJson, string profileId = "default")
    {
        var existing = _entries.FindOne(x => x.ProfileId == profileId);
        if (existing is null)
        {
            _entries.Insert(new LayoutEntry
            {
                ProfileId = profileId,
                LayoutJson = layoutJson,
                UpdatedUtc = DateTime.UtcNow,
            });
        }
        else
        {
            existing.LayoutJson = layoutJson;
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

public sealed class LayoutEntry
{
    public int Id { get; set; }
    public string ProfileId { get; set; } = string.Empty;
    public string LayoutJson { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}
