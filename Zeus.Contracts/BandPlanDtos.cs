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

namespace Zeus.Contracts;

/// <summary>
/// Identifies a regional amateur-radio band plan. Country regions may declare
/// a parent (e.g. EI.ParentId = "IARU_R1"), and the effective plan is parent
/// segments overridden by country segments where they overlap.
/// </summary>
public sealed record BandRegion(
    string Id,
    string DisplayName,
    string ShortCode,
    string? ParentId);

/// <summary>
/// One contiguous frequency segment in a band plan.
/// Frequencies are inclusive and in Hz for wire consistency with the rest of Zeus.
/// </summary>
public sealed record BandSegment(
    string RegionId,
    long LowHz,
    long HighHz,
    string Label,
    BandAllocation Allocation,
    ModeRestriction ModeRestriction,
    int? MaxPowerW,
    string? Notes);

public enum BandAllocation : byte
{
    Amateur,
    SWL,
    Broadcast,
    Reserved,
    Unknown,
}

/// <summary>
/// Coarse mode restriction for a band segment.
/// CustomMask (CW+Digital, no phone) deferred to a future PRD.
/// </summary>
public enum ModeRestriction : byte
{
    Any,
    CwOnly,
    PhoneOnly,
    DigitalOnly,
}

public sealed record BandPlanCurrentSetRequest(string RegionId);
