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

using System.Text.Json;
using System.Text.Json.Serialization;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Loads static band-plan JSON from Zeus.Server/BandPlans/ and resolves
/// per-region plans by merging parent segments with country-level overrides.
/// </summary>
public sealed class BandPlanStore
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IReadOnlyList<BandRegion> _regions;
    private readonly IReadOnlyDictionary<string, List<SegmentEntry>> _rawSegments;
    private readonly Dictionary<string, IReadOnlyList<BandSegment>> _resolvedCache = new();
    private readonly ILogger<BandPlanStore> _log;

    public BandPlanStore(ILogger<BandPlanStore> log)
    {
        _log = log;
        var dir = Path.Combine(AppContext.BaseDirectory, "BandPlans");
        _regions = LoadRegions(dir);
        _rawSegments = LoadSegmentFiles(dir);
        _log.LogInformation("BandPlanStore loaded {RegionCount} regions, {FileCount} segment files from {Dir}",
            _regions.Count, _rawSegments.Count, dir);
    }

    public IReadOnlyList<BandRegion> Regions => _regions;

    public IReadOnlyList<BandSegment> ResolvePlan(string regionId)
    {
        lock (_resolvedCache)
        {
            if (_resolvedCache.TryGetValue(regionId, out var cached))
                return cached;
        }

        var resolved = BuildResolvedPlan(regionId);

        lock (_resolvedCache)
        {
            _resolvedCache[regionId] = resolved;
        }
        return resolved;
    }

    private IReadOnlyList<BandSegment> BuildResolvedPlan(string regionId)
    {
        var chain = BuildRegionChain(regionId);
        var acc = new List<(long Low, long High, BandSegment Seg)>();

        foreach (var rId in chain)
        {
            if (!_rawSegments.TryGetValue(rId, out var entries))
                continue;

            foreach (var e in entries)
            {
                // Remove any accumulated segments that overlap with this new entry.
                acc.RemoveAll(a => a.High >= e.LowHz && a.Low <= e.HighHz);
                acc.Add((e.LowHz, e.HighHz, new BandSegment(
                    RegionId: rId,
                    LowHz: e.LowHz,
                    HighHz: e.HighHz,
                    Label: e.Label,
                    Allocation: e.Allocation,
                    ModeRestriction: e.ModeRestriction,
                    MaxPowerW: e.MaxPowerW,
                    Notes: e.Notes)));
            }
        }

        acc.Sort((a, b) => a.Low.CompareTo(b.Low));
        return acc.Select(a => a.Seg).ToList().AsReadOnly();
    }

    private List<string> BuildRegionChain(string regionId)
    {
        var chain = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? current = regionId;

        // Walk up to the root collecting region IDs
        while (current is not null && visited.Add(current))
        {
            chain.Add(current);
            var region = _regions.FirstOrDefault(r =>
                string.Equals(r.Id, current, StringComparison.OrdinalIgnoreCase));
            current = region?.ParentId;
        }

        // Reverse so that parent segments come first (oldest ancestor first)
        chain.Reverse();
        return chain;
    }

    private IReadOnlyList<BandRegion> LoadRegions(string dir)
    {
        var path = Path.Combine(dir, "regions.json");
        if (!File.Exists(path))
        {
            _log.LogWarning("BandPlanStore: regions.json not found at {Path}", path);
            return [];
        }

        try
        {
            var json = File.ReadAllText(path);
            var items = JsonSerializer.Deserialize<List<RegionEntry>>(json, _jsonOpts) ?? [];
            return items
                .Select(r => new BandRegion(r.Id, r.DisplayName, r.ShortCode, r.ParentId))
                .ToList()
                .AsReadOnly();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "BandPlanStore: failed to load regions.json");
            return [];
        }
    }

    private IReadOnlyDictionary<string, List<SegmentEntry>> LoadSegmentFiles(string dir)
    {
        var result = new Dictionary<string, List<SegmentEntry>>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(dir))
        {
            _log.LogWarning("BandPlanStore: directory not found: {Dir}", dir);
            return result;
        }

        foreach (var file in Directory.GetFiles(dir, "*.segments.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var doc = JsonSerializer.Deserialize<SegmentFile>(json, _jsonOpts);
                if (doc?.Segments is null || string.IsNullOrEmpty(doc.RegionId))
                {
                    _log.LogWarning("BandPlanStore: skipping malformed file {File}", file);
                    continue;
                }
                result[doc.RegionId] = doc.Segments;
                _log.LogDebug("BandPlanStore: loaded {Count} segments for {RegionId}",
                    doc.Segments.Count, doc.RegionId);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "BandPlanStore: failed to load {File}", file);
            }
        }

        return result;
    }

    // JSON DTOs (only used for deserialization — not exposed on the API).
    private sealed record RegionEntry(
        string Id,
        string DisplayName,
        string ShortCode,
        string? ParentId);

    private sealed record SegmentFile(
        string RegionId,
        string? Source,
        string? LastVerified,
        List<SegmentEntry> Segments);

    private sealed record SegmentEntry(
        long LowHz,
        long HighHz,
        string Label,
        BandAllocation Allocation,
        ModeRestriction ModeRestriction,
        int? MaxPowerW,
        string? Notes);
}
