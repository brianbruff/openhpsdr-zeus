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

import type { RxMode } from './client';

export type BandAllocation = 'Amateur' | 'SWL' | 'Broadcast' | 'Reserved' | 'Unknown';
export type BandModeRestriction = 'Any' | 'CwOnly' | 'PhoneOnly' | 'DigitalOnly';

export type BandRegion = {
  id: string;
  displayName: string;
  shortCode: string;
  parentId: string | null;
};

export type BandSegment = {
  regionId: string;
  lowHz: number;
  highHz: number;
  label: string;
  allocation: BandAllocation;
  modeRestriction: BandModeRestriction;
  maxPowerW: number | null;
  notes: string | null;
};

export type BandPlanDto = {
  regionId: string;
  segments: BandSegment[];
};

function normalizeBandRegion(raw: unknown): BandRegion | null {
  if (!raw || typeof raw !== 'object') return null;
  const r = raw as Record<string, unknown>;
  if (typeof r.id !== 'string' || typeof r.displayName !== 'string') return null;
  return {
    id: r.id,
    displayName: r.displayName,
    shortCode: typeof r.shortCode === 'string' ? r.shortCode : r.id,
    parentId: typeof r.parentId === 'string' ? r.parentId : null,
  };
}

function normalizeBandSegment(raw: unknown): BandSegment | null {
  if (!raw || typeof raw !== 'object') return null;
  const r = raw as Record<string, unknown>;
  if (
    typeof r.lowHz !== 'number' ||
    typeof r.highHz !== 'number' ||
    typeof r.label !== 'string'
  ) return null;

  const ALLOCATIONS: BandAllocation[] = ['Amateur', 'SWL', 'Broadcast', 'Reserved', 'Unknown'];
  const RESTRICTIONS: BandModeRestriction[] = ['Any', 'CwOnly', 'PhoneOnly', 'DigitalOnly'];

  const allocation = (ALLOCATIONS as readonly string[]).includes(r.allocation as string)
    ? (r.allocation as BandAllocation)
    : 'Unknown';
  const modeRestriction = (RESTRICTIONS as readonly string[]).includes(r.modeRestriction as string)
    ? (r.modeRestriction as BandModeRestriction)
    : 'Any';

  return {
    regionId: typeof r.regionId === 'string' ? r.regionId : '',
    lowHz: r.lowHz,
    highHz: r.highHz,
    label: r.label,
    allocation,
    modeRestriction,
    maxPowerW: typeof r.maxPowerW === 'number' ? r.maxPowerW : null,
    notes: typeof r.notes === 'string' ? r.notes : null,
  };
}

function normalizeBandPlan(raw: unknown): BandPlanDto {
  if (!raw || typeof raw !== 'object') return { regionId: '', segments: [] };
  const r = raw as Record<string, unknown>;
  const regionId = typeof r.regionId === 'string' ? r.regionId : '';
  const segments: BandSegment[] = [];
  if (Array.isArray(r.segments)) {
    for (const s of r.segments) {
      const seg = normalizeBandSegment(s);
      if (seg) segments.push(seg);
    }
  }
  return { regionId, segments };
}

async function jsonGet<T>(url: string, parse: (raw: unknown) => T, signal?: AbortSignal): Promise<T> {
  const res = await fetch(url, { signal });
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
  const raw = await res.json() as unknown;
  return parse(raw);
}

export function fetchBandRegions(signal?: AbortSignal): Promise<BandRegion[]> {
  return jsonGet('/api/bands/regions', (raw) => {
    if (!Array.isArray(raw)) return [];
    const out: BandRegion[] = [];
    for (const item of raw) {
      const r = normalizeBandRegion(item);
      if (r) out.push(r);
    }
    return out;
  }, signal);
}

export function fetchBandPlan(regionId: string, signal?: AbortSignal): Promise<BandPlanDto> {
  return jsonGet(
    `/api/bands/plan?region=${encodeURIComponent(regionId)}`,
    normalizeBandPlan,
    signal,
  );
}

export function fetchCurrentBandPlan(signal?: AbortSignal): Promise<BandPlanDto> {
  return jsonGet('/api/bands/current', normalizeBandPlan, signal);
}

export function setCurrentRegion(regionId: string, signal?: AbortSignal): Promise<BandPlanDto> {
  return fetch('/api/bands/current', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ regionId }),
    signal,
  }).then(async (res) => {
    if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
    const raw = await res.json() as unknown;
    return normalizeBandPlan(raw);
  });
}

const PHONE_MODES: readonly RxMode[] = ['USB', 'LSB', 'AM', 'SAM', 'DSB', 'FM'];
const CW_MODES: readonly RxMode[] = ['CWU', 'CWL'];
const DIGITAL_MODES: readonly RxMode[] = ['DIGL', 'DIGU'];

export function modeMatchesRestriction(mode: RxMode, restriction: BandModeRestriction): boolean {
  switch (restriction) {
    case 'Any': return true;
    case 'CwOnly': return (CW_MODES as readonly string[]).includes(mode);
    case 'PhoneOnly': return (PHONE_MODES as readonly string[]).includes(mode);
    case 'DigitalOnly': return (DIGITAL_MODES as readonly string[]).includes(mode);
    default: return false;
  }
}

export function binarySearchSegment(
  segments: BandSegment[],
  freqHz: number,
): BandSegment | null {
  let lo = 0;
  let hi = segments.length - 1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    const seg = segments[mid]!;
    if (freqHz < seg.lowHz) hi = mid - 1;
    else if (freqHz > seg.highHz) lo = mid + 1;
    else return seg;
  }
  return null;
}
