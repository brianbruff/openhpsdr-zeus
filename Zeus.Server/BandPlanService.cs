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
using Zeus.Contracts;

namespace Zeus.Server;

// Exposes band-plan lookup to any service that needs to query the current
// region (e.g. filter overlay, future TX guard). Consumed by the REST
// endpoints and subscribable via PlanChanged for reactive consumers.
public interface IBandPlanService
{
    BandRegion CurrentRegion { get; }
    IReadOnlyList<BandSegment> CurrentPlan { get; }
    BandSegment? GetSegment(long freqHz);
    bool InBand(long freqHz, RxMode mode);
    // Fired after region change or plan edit (override save/delete).
    event Action PlanChanged;
}

public sealed class BandPlanService : IBandPlanService, IDisposable
{
    private const string DefaultRegionId = "IARU_R1";

    private readonly BandPlanStore _store;
    private readonly BandPrefsStore _prefs;
    private readonly ILogger<BandPlanService> _log;

    // Cached so every GetSegment/InBand call doesn't re-resolve.
    private volatile BandRegion _currentRegion;
    private volatile IReadOnlyList<BandSegment> _currentPlan;

    public event Action? PlanChanged;

    public BandPlanService(
        BandPlanStore store,
        BandPrefsStore prefs,
        ILogger<BandPlanService> log)
    {
        _store = store;
        _prefs = prefs;
        _log = log;

        var savedId = _prefs.GetRegionId() ?? DefaultRegionId;
        var region = _store.Regions.FirstOrDefault(r => r.Id == savedId)
                     ?? _store.Regions.FirstOrDefault(r => r.Id == DefaultRegionId)
                     ?? _store.Regions.FirstOrDefault()
                     ?? new BandRegion(DefaultRegionId, "IARU Region 1", "R1", null);

        _currentRegion = region;
        _currentPlan = _store.Resolve(region.Id);

        _log.LogInformation("BandPlanService: active region={RegionId} segments={Count}",
            region.Id, _currentPlan.Count);
    }

    public BandRegion CurrentRegion => _currentRegion;
    public IReadOnlyList<BandSegment> CurrentPlan => _currentPlan;

    // Binary-search the resolved plan for the segment containing freqHz.
    // Returns null when outside any Amateur allocation.
    public BandSegment? GetSegment(long freqHz)
    {
        var plan = _currentPlan;
        int lo = 0, hi = plan.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var seg = plan[mid];
            if (freqHz < seg.LowHz)       hi = mid - 1;
            else if (freqHz > seg.HighHz)  lo = mid + 1;
            else                           return seg;
        }
        return null;
    }

    // Returns true if freqHz falls within an Amateur-allocated segment whose
    // mode restriction permits mode.
    public bool InBand(long freqHz, RxMode mode)
    {
        var seg = GetSegment(freqHz);
        if (seg is null || seg.Allocation != BandAllocation.Amateur)
            return false;
        return ModeMatches(seg.ModeRestriction, mode);
    }

    // Sets the active region and refreshes the plan cache.
    public bool TrySetRegion(string regionId)
    {
        var region = _store.Regions.FirstOrDefault(r => r.Id == regionId);
        if (region is null) return false;

        _currentRegion = region;
        _currentPlan = _store.Resolve(regionId);
        _prefs.SetRegionId(regionId);

        _log.LogInformation("BandPlanService: region changed to {RegionId}", regionId);
        PlanChanged?.Invoke();
        return true;
    }

    // Invalidates the plan cache after a PUT/DELETE override operation.
    public void NotifyPlanChanged()
    {
        _currentPlan = _store.Resolve(_currentRegion.Id);
        PlanChanged?.Invoke();
    }

    public void Dispose() { }

    private static bool ModeMatches(ModeRestriction restriction, RxMode mode) => restriction switch
    {
        ModeRestriction.Any         => true,
        ModeRestriction.CwOnly      => mode is RxMode.CWU or RxMode.CWL,
        ModeRestriction.PhoneOnly   => mode is RxMode.USB or RxMode.LSB or RxMode.AM or RxMode.SAM or RxMode.DSB or RxMode.FM,
        ModeRestriction.DigitalOnly => mode is RxMode.DIGL or RxMode.DIGU,
        _                           => false,
    };
}

// Persists the operator's selected region ID in zeus-prefs.db.
public sealed class BandPrefsStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<BandPrefsEntry> _entries;

    public BandPrefsStore(ILogger<BandPrefsStore> log)
    {
        var dbPath = GetDatabasePath();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _entries = _db.GetCollection<BandPrefsEntry>("band_plan_prefs");
        _entries.EnsureIndex(x => x.Key, unique: true);
    }

    public string? GetRegionId()
    {
        var e = _entries.FindOne(x => x.Key == "currentRegionId");
        return e?.Value;
    }

    public void SetRegionId(string regionId)
    {
        var existing = _entries.FindOne(x => x.Key == "currentRegionId");
        if (existing is null)
            _entries.Insert(new BandPrefsEntry { Key = "currentRegionId", Value = regionId });
        else
        {
            existing.Value = regionId;
            _entries.Update(existing);
        }
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

public sealed class BandPrefsEntry
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
