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

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Provides the active band plan and query methods consumed by the filter-overlay
/// and future TX-guard. Wraps BandPlanStore with current-region state.
/// </summary>
public interface IBandPlanService
{
    BandRegion CurrentRegion { get; }
    IReadOnlyList<BandSegment> CurrentPlan { get; }

    /// <summary>Returns the segment containing <paramref name="freqHz"/>, or null if out of band.</summary>
    BandSegment? GetSegment(long freqHz);

    /// <summary>True if <paramref name="freqHz"/> is inside an Amateur segment whose mode restriction allows <paramref name="mode"/>.</summary>
    bool InBand(long freqHz, RxMode mode);

    event Action? PlanChanged;
}

public sealed class BandPlanService : IBandPlanService
{
    private const string DefaultRegionId = "IARU_R1";

    private readonly BandPlanStore _store;
    private readonly DspSettingsStore _settings;
    private readonly ILogger<BandPlanService> _log;
    private string _currentRegionId;

    public BandPlanService(BandPlanStore store, DspSettingsStore settings, ILogger<BandPlanService> log)
    {
        _store = store;
        _settings = settings;
        _log = log;
        _currentRegionId = _settings.GetCurrentRegionId() ?? DefaultRegionId;
        _log.LogInformation("BandPlanService active region: {RegionId}", _currentRegionId);
    }

    public event Action? PlanChanged;

    internal BandPlanStore Store => _store;

    public BandRegion CurrentRegion =>
        _store.Regions.FirstOrDefault(r =>
            string.Equals(r.Id, _currentRegionId, StringComparison.OrdinalIgnoreCase))
        ?? new BandRegion(_currentRegionId, _currentRegionId, _currentRegionId, null);

    public IReadOnlyList<BandSegment> CurrentPlan => _store.ResolvePlan(_currentRegionId);

    public void SetRegion(string regionId)
    {
        if (string.IsNullOrWhiteSpace(regionId))
            throw new ArgumentException("regionId required", nameof(regionId));

        _currentRegionId = regionId;
        _settings.SetCurrentRegionId(regionId);
        _log.LogInformation("BandPlanService region changed to {RegionId}", regionId);
        PlanChanged?.Invoke();
    }

    public BandSegment? GetSegment(long freqHz)
    {
        var plan = CurrentPlan;
        return BinarySearchSegment(plan, freqHz);
    }

    public bool InBand(long freqHz, RxMode mode)
    {
        var seg = GetSegment(freqHz);
        if (seg is null) return false;
        if (seg.Allocation != BandAllocation.Amateur) return false;
        return ModeMatchesRestriction(mode, seg.ModeRestriction);
    }

    private static BandSegment? BinarySearchSegment(IReadOnlyList<BandSegment> plan, long freqHz)
    {
        int lo = 0, hi = plan.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var seg = plan[mid];
            if (freqHz < seg.LowHz)
                hi = mid - 1;
            else if (freqHz > seg.HighHz)
                lo = mid + 1;
            else
                return seg;
        }
        return null;
    }

    private static bool ModeMatchesRestriction(RxMode mode, ModeRestriction restriction) =>
        restriction switch
        {
            ModeRestriction.Any => true,
            ModeRestriction.CwOnly => mode is RxMode.CWU or RxMode.CWL,
            ModeRestriction.PhoneOnly => mode is RxMode.USB or RxMode.LSB or RxMode.AM or RxMode.SAM or RxMode.DSB or RxMode.FM,
            ModeRestriction.DigitalOnly => mode is RxMode.DIGL or RxMode.DIGU,
            _ => false,
        };
}
