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

namespace Zeus.Contracts;

public enum BandAllocation : byte
{
    Amateur,
    SWL,
    Broadcast,
    Reserved,
    Unknown,
}

// Coarse mode restriction. 95 % of sub-bands are CW-only, phone-only, or
// unrestricted. CustomMask (a flags variant for e.g. "digital + CW, no phone")
// is reserved for a future PRD — not in v1.
public enum ModeRestriction : byte
{
    Any,
    CwOnly,
    PhoneOnly,
    DigitalOnly,
}

// A regional band plan catalog entry. ParentId forms a layered override chain:
// EI → IARU_R1, US_FCC_GENERAL → IARU_R2, etc. Null for base IARU regions.
public sealed record BandRegion(
    string Id,
    string DisplayName,
    string ShortCode,
    string? ParentId);

// One contiguous frequency allocation in a region's band plan.
// Frequencies are in Hz (wire-consistent with VFO, filter, and meter fields).
// MaxPowerW is a nullable reserved slot so the shape doesn't change when a
// TX-power PRD lands; it is ignored in all v1 UI.
public sealed record BandSegment(
    string RegionId,
    long LowHz,
    long HighHz,
    string Label,
    BandAllocation Allocation,
    ModeRestriction ModeRestriction,
    int? MaxPowerW,
    string? Notes);

// Response wrapper for GET /api/bands/plan — the resolved (parent-merged)
// effective plan for a region.
public sealed record BandPlanDto(
    string RegionId,
    IReadOnlyList<BandSegment> Segments);

// POST /api/bands/current body.
public sealed record BandPlanCurrentSetRequest(string RegionId);

// GET /api/bands/current response.
public sealed record BandPlanCurrentDto(
    string RegionId,
    string DisplayName,
    IReadOnlyList<BandSegment> Segments);

// PUT /api/bands/plan body — replaces the override record for a region.
public sealed record BandPlanPutRequest(
    string RegionId,
    IReadOnlyList<BandSegment> Segments);
