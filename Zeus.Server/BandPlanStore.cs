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

using System.Text.Json;
using System.Text.Json.Serialization;
using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

// Loads shipped JSON band plans from Zeus.Server/BandPlans/ and stores
// operator overrides in a LiteDB collection. Resolution order:
//   1. Walk ancestor chain (oldest-first) — each ancestor contributes its
//      segments (from the LiteDB override if one exists, otherwise the
//      shipped JSON).
//   2. Child segments replace any overlapping parent segment.
//   3. Return sorted by LowHz with no overlaps.
public sealed class BandPlanStore : IDisposable
{
    // Shipped JSON baseline, keyed by regionId. Never mutated after ctor.
    private readonly IReadOnlyDictionary<string, IReadOnlyList<BandSegment>> _shipped;
    private readonly IReadOnlyList<BandRegion> _regions;

    private readonly LiteDatabase _db;
    private readonly ILiteCollection<BandPlanOverrideEntry> _overrides;
    private readonly ILogger<BandPlanStore> _log;

    public BandPlanStore(ILogger<BandPlanStore> log)
    {
        _log = log;

        var dbPath = GetDatabasePath();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _overrides = _db.GetCollection<BandPlanOverrideEntry>("band_plan_overrides");
        _overrides.EnsureIndex(x => x.RegionId, unique: true);

        (_regions, _shipped) = LoadShippedJson();
        _log.LogInformation("BandPlanStore: loaded {RegionCount} regions, {SegmentCount} total shipped segments",
            _regions.Count,
            _shipped.Values.Sum(v => v.Count));
    }

    public IReadOnlyList<BandRegion> Regions => _regions;

    // Resolved (parent-merged, overrides applied) plan for the given region.
    public IReadOnlyList<BandSegment> Resolve(string regionId)
    {
        var region = _regions.FirstOrDefault(r => r.Id == regionId);
        if (region is null) return Array.Empty<BandSegment>();

        // Ancestry chain: oldest ancestor first, then the target region.
        var chain = BuildChain(regionId);

        var acc = new List<BandSegment>();
        foreach (var rId in chain)
        {
            var segments = GetEffectiveSegments(rId);
            foreach (var seg in segments)
                MergeSegment(acc, seg with { RegionId = rId });
        }

        acc.Sort((a, b) => a.LowHz.CompareTo(b.LowHz));
        return acc;
    }

    // Raw segments for one region level (not resolved through parents) —
    // used by the editor's Source column and PUT/DELETE endpoints.
    public IReadOnlyList<BandSegment> GetRawSegments(string regionId)
        => GetEffectiveSegments(regionId);

    public bool RegionExists(string regionId)
        => _regions.Any(r => r.Id == regionId);

    // Returns the override record if one exists, null otherwise.
    public BandPlanOverrideEntry? GetOverride(string regionId)
        => _overrides.FindOne(x => x.RegionId == regionId);

    // Saves or replaces the override for a region. Validates first.
    public (bool ok, string? error) SaveOverride(string regionId, IReadOnlyList<BandSegment> segments)
    {
        if (!RegionExists(regionId))
            return (false, $"unknown region '{regionId}'");

        var err = ValidateSegments(segments);
        if (err is not null)
            return (false, err);

        var sorted = segments.OrderBy(s => s.LowHz).ToList();
        var existing = _overrides.FindOne(x => x.RegionId == regionId);
        if (existing is null)
        {
            _overrides.Insert(new BandPlanOverrideEntry
            {
                RegionId = regionId,
                SegmentsJson = JsonSerializer.Serialize(sorted, _jsonOptions),
                UpdatedUtc = DateTime.UtcNow,
            });
        }
        else
        {
            existing.SegmentsJson = JsonSerializer.Serialize(sorted, _jsonOptions);
            existing.UpdatedUtc = DateTime.UtcNow;
            _overrides.Update(existing);
        }

        return (true, null);
    }

    // Deletes the operator override for a region; reverts to shipped JSON.
    public void DeleteOverride(string regionId)
    {
        _overrides.DeleteMany(x => x.RegionId == regionId);
    }

    public void Dispose() => _db.Dispose();

    // ── private helpers ──────────────────────────────────────────────────────

    // Returns the region-level segments from LiteDB override if present,
    // otherwise the shipped JSON.
    private IReadOnlyList<BandSegment> GetEffectiveSegments(string regionId)
    {
        var entry = _overrides.FindOne(x => x.RegionId == regionId);
        if (entry is not null)
        {
            try
            {
                var segs = JsonSerializer.Deserialize<List<BandSegmentJson>>(
                    entry.SegmentsJson, _jsonOptions);
                if (segs is not null)
                    return segs.Select(s => MapSegment(s, regionId)).ToArray();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "BandPlanStore: failed to deserialize override for {RegionId}; falling back to shipped", regionId);
            }
        }

        return _shipped.TryGetValue(regionId, out var v) ? v : Array.Empty<BandSegment>();
    }

    // Ancestry chain for a region: oldest ancestor → ... → regionId.
    private List<string> BuildChain(string regionId)
    {
        var chain = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = regionId;

        while (current is not null)
        {
            if (!visited.Add(current)) break; // cycle guard
            chain.Add(current);
            current = _regions.FirstOrDefault(r => r.Id == current)?.ParentId!;
        }

        chain.Reverse();
        return chain;
    }

    // Merge one new segment into the accumulator, removing any overlaps first.
    private static void MergeSegment(List<BandSegment> acc, BandSegment seg)
    {
        acc.RemoveAll(existing =>
            existing.LowHz <= seg.HighHz && existing.HighHz >= seg.LowHz);
        acc.Add(seg);
    }

    // Validates a submitted segment list: non-empty labels, Hz bounds, no
    // overlaps within the set.
    private static string? ValidateSegments(IReadOnlyList<BandSegment> segments)
    {
        foreach (var s in segments)
        {
            if (string.IsNullOrWhiteSpace(s.Label))
                return "all segments must have a non-empty label";
            if (s.LowHz < 0 || s.HighHz < 0)
                return "Hz values must be non-negative";
            if (s.LowHz > s.HighHz)
                return $"segment '{s.Label}': LowHz ({s.LowHz}) must be ≤ HighHz ({s.HighHz})";
        }

        var sorted = segments.OrderBy(s => s.LowHz).ToList();
        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i].LowHz <= sorted[i - 1].HighHz)
                return $"overlapping segments: '{sorted[i - 1].Label}' and '{sorted[i].Label}'";
        }

        return null;
    }

    // ── JSON loading ─────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private (IReadOnlyList<BandRegion> regions, IReadOnlyDictionary<string, IReadOnlyList<BandSegment>> shipped)
        LoadShippedJson()
    {
        var dir = GetBandPlansDirectory();
        _log.LogInformation("BandPlanStore: loading from {Dir}", dir);

        // Load region catalog
        var regionFile = Path.Combine(dir, "regions.json");
        List<BandRegion> regions;
        if (File.Exists(regionFile))
        {
            var raw = File.ReadAllText(regionFile);
            var list = JsonSerializer.Deserialize<List<BandRegionJson>>(raw, _jsonOptions) ?? new();
            regions = list.Select(r => new BandRegion(r.Id!, r.DisplayName!, r.ShortCode!, r.ParentId)).ToList();
        }
        else
        {
            _log.LogWarning("BandPlanStore: regions.json not found at {Path}", regionFile);
            regions = new List<BandRegion>();
        }

        // Auto-discover *.segments.json
        var shipped = new Dictionary<string, IReadOnlyList<BandSegment>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(dir, "*.segments.json"))
        {
            try
            {
                var raw = File.ReadAllText(file);
                var doc = JsonSerializer.Deserialize<BandPlanFileJson>(raw, _jsonOptions);
                if (doc?.RegionId is null || doc.Segments is null) continue;
                var segs = doc.Segments
                    .Select(s => MapSegment(s, doc.RegionId))
                    .OrderBy(s => s.LowHz)
                    .ToArray();
                shipped[doc.RegionId] = segs;
                _log.LogDebug("BandPlanStore: loaded {Count} segments for {RegionId}", segs.Length, doc.RegionId);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "BandPlanStore: failed to load {File}", file);
            }
        }

        return (regions, shipped);
    }

    private static BandSegment MapSegment(BandSegmentJson s, string regionId) => new(
        RegionId: regionId,
        LowHz: s.LowHz,
        HighHz: s.HighHz,
        Label: s.Label ?? string.Empty,
        Allocation: s.Allocation,
        ModeRestriction: s.ModeRestriction,
        MaxPowerW: s.MaxPowerW,
        Notes: s.Notes);

    private static string GetBandPlansDirectory()
    {
        // Check next to the executable first (published layout).
        var execDir = Path.GetDirectoryName(
            System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
            ?? typeof(BandPlanStore).Assembly.Location) ?? ".";
        var candidateExec = Path.Combine(execDir, "BandPlans");
        if (Directory.Exists(candidateExec))
            return candidateExec;

        // Fallback: relative to the content root (dotnet run from project dir).
        return Path.Combine(AppContext.BaseDirectory, "BandPlans");
    }

    private static string GetDatabasePath()
    {
        var appDataDir = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        return Path.Combine(appDataDir, "Zeus", "zeus-prefs.db");
    }

    // ── JSON shim types ───────────────────────────────────────────────────────

    private sealed class BandRegionJson
    {
        public string? Id { get; set; }
        public string? DisplayName { get; set; }
        public string? ShortCode { get; set; }
        public string? ParentId { get; set; }
    }

    private sealed class BandSegmentJson
    {
        public long LowHz { get; set; }
        public long HighHz { get; set; }
        public string? Label { get; set; }
        public BandAllocation Allocation { get; set; }
        public ModeRestriction ModeRestriction { get; set; }
        public int? MaxPowerW { get; set; }
        public string? Notes { get; set; }
    }

    private sealed class BandPlanFileJson
    {
        public string? RegionId { get; set; }
        public List<BandSegmentJson>? Segments { get; set; }
    }
}

// LiteDB document for operator overrides (one per region).
public sealed class BandPlanOverrideEntry
{
    public int Id { get; set; }
    public string RegionId { get; set; } = string.Empty;
    public string SegmentsJson { get; set; } = "[]";
    public DateTime UpdatedUtc { get; set; }
}
