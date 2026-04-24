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
using Zeus.Protocol1.Discovery;

namespace Zeus.Server;

// Persists the operator's chosen "this is my radio" preference. Fed into
// RadioService.EffectiveBoardKind so PA defaults, per-band gain tables and
// the PA Settings preview work before the radio is physically connected —
// otherwise the UI has to wait until after a successful connect to seed
// anything useful. Null here = "Auto" (let discovery pick the board).
//
// Lives in zeus-prefs.db alongside the other non-sensitive preferences.
//
// Drive-byte encoding deliberately stays on ConnectedBoardKind (what's on
// the wire), not effective. If an operator selects HL2 here while plugged
// into a G2, we still want the G2's 8-bit drive math on the wire — the
// preference is for *configuration seeds*, not for physics.
public sealed class PreferredRadioStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<PreferredRadioEntry> _entries;
    private readonly ILogger<PreferredRadioStore> _log;
    private readonly object _sync = new();

    public event Action? Changed;

    public PreferredRadioStore(ILogger<PreferredRadioStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? GetDatabasePath();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _entries = _db.GetCollection<PreferredRadioEntry>("preferred_radio");

        _log.LogInformation("PreferredRadioStore initialized at {Path}", dbPath);
    }

    // null = "Auto" — no operator override, effective board tracks Connected.
    public HpsdrBoardKind? Get()
    {
        lock (_sync)
        {
            var e = _entries.FindAll().FirstOrDefault();
            if (e is null) return null;
            // A stored "Unknown" is indistinguishable from "Auto" and would
            // just seed junk PA defaults; treat it as Auto.
            return e.Board == HpsdrBoardKind.Unknown ? null : e.Board;
        }
    }

    public void Set(HpsdrBoardKind? board)
    {
        lock (_sync)
        {
            // "Auto" = delete the row, so a future Get() returns null cleanly
            // without us reserving a sentinel value in the enum.
            if (board is null || board == HpsdrBoardKind.Unknown)
            {
                _entries.DeleteAll();
            }
            else
            {
                var existing = _entries.FindAll().FirstOrDefault();
                if (existing is null)
                {
                    _entries.Insert(new PreferredRadioEntry
                    {
                        Board = board.Value,
                        UpdatedUtc = DateTime.UtcNow,
                    });
                }
                else
                {
                    existing.Board = board.Value;
                    existing.UpdatedUtc = DateTime.UtcNow;
                    _entries.Update(existing);
                }
            }
        }
        Changed?.Invoke();
    }

    public void Dispose() => _db.Dispose();

    private static string GetDatabasePath()
    {
        var appDataDir = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        return Path.Combine(appDataDir, "Zeus", "zeus-prefs.db");
    }
}

public sealed class PreferredRadioEntry
{
    public int Id { get; set; }
    public HpsdrBoardKind Board { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
