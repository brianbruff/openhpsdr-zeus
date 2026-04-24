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

import type { RxMode } from './client';

export type BandAllocation = 'Amateur' | 'SWL' | 'Broadcast' | 'Reserved' | 'Unknown';
export type ModeRestriction = 'Any' | 'CwOnly' | 'PhoneOnly' | 'DigitalOnly';

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
  modeRestriction: ModeRestriction;
  maxPowerW: number | null;
  notes: string | null;
};

export type BandPlanDto = {
  regionId: string;
  segments: BandSegment[];
};

export type BandPlanCurrentDto = {
  regionId: string;
  displayName: string;
  segments: BandSegment[];
};

// ── normalizers ──────────────────────────────────────────────────────────────

function toBandAllocation(v: unknown): BandAllocation {
  const allowed: BandAllocation[] = ['Amateur', 'SWL', 'Broadcast', 'Reserved', 'Unknown'];
  return (allowed as string[]).includes(v as string) ? (v as BandAllocation) : 'Unknown';
}

function toModeRestriction(v: unknown): ModeRestriction {
  const allowed: ModeRestriction[] = ['Any', 'CwOnly', 'PhoneOnly', 'DigitalOnly'];
  return (allowed as string[]).includes(v as string) ? (v as ModeRestriction) : 'Any';
}

function normalizeSegment(raw: unknown): BandSegment | null {
  if (!raw || typeof raw !== 'object') return null;
  const r = raw as Record<string, unknown>;
  return {
    regionId: typeof r.regionId === 'string' ? r.regionId : '',
    lowHz: typeof r.lowHz === 'number' ? r.lowHz : 0,
    highHz: typeof r.highHz === 'number' ? r.highHz : 0,
    label: typeof r.label === 'string' ? r.label : '',
    allocation: toBandAllocation(r.allocation),
    modeRestriction: toModeRestriction(r.modeRestriction),
    maxPowerW: typeof r.maxPowerW === 'number' ? r.maxPowerW : null,
    notes: typeof r.notes === 'string' ? r.notes : null,
  };
}

function normalizeSegments(raw: unknown): BandSegment[] {
  if (!Array.isArray(raw)) return [];
  return raw.flatMap((item) => {
    const s = normalizeSegment(item);
    return s ? [s] : [];
  });
}

function normalizeRegion(raw: unknown): BandRegion | null {
  if (!raw || typeof raw !== 'object') return null;
  const r = raw as Record<string, unknown>;
  if (typeof r.id !== 'string') return null;
  return {
    id: r.id,
    displayName: typeof r.displayName === 'string' ? r.displayName : r.id,
    shortCode: typeof r.shortCode === 'string' ? r.shortCode : r.id,
    parentId: typeof r.parentId === 'string' ? r.parentId : null,
  };
}

// ── API fetch helpers ─────────────────────────────────────────────────────────

export async function fetchRegions(signal?: AbortSignal): Promise<BandRegion[]> {
  const res = await fetch('/api/bands/regions', { signal });
  if (!res.ok) throw new Error(`GET /api/bands/regions → ${res.status}`);
  const raw = (await res.json()) as unknown[];
  return raw.flatMap((item) => {
    const r = normalizeRegion(item);
    return r ? [r] : [];
  });
}

export async function fetchPlan(regionId: string, signal?: AbortSignal): Promise<BandPlanDto> {
  const res = await fetch(`/api/bands/plan?region=${encodeURIComponent(regionId)}`, { signal });
  if (!res.ok) throw new Error(`GET /api/bands/plan → ${res.status}`);
  const raw = (await res.json()) as Record<string, unknown>;
  return {
    regionId: typeof raw.regionId === 'string' ? raw.regionId : regionId,
    segments: normalizeSegments(raw.segments),
  };
}

export async function fetchCurrent(signal?: AbortSignal): Promise<BandPlanCurrentDto> {
  const res = await fetch('/api/bands/current', { signal });
  if (!res.ok) throw new Error(`GET /api/bands/current → ${res.status}`);
  const raw = (await res.json()) as Record<string, unknown>;
  return {
    regionId: typeof raw.regionId === 'string' ? raw.regionId : '',
    displayName: typeof raw.displayName === 'string' ? raw.displayName : '',
    segments: normalizeSegments(raw.segments),
  };
}

export async function setCurrentRegion(
  regionId: string,
  signal?: AbortSignal,
): Promise<BandPlanCurrentDto> {
  const res = await fetch('/api/bands/current', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ regionId }),
    signal,
  });
  if (!res.ok) {
    let msg = `POST /api/bands/current → ${res.status}`;
    try {
      const b = (await res.json()) as { error?: string };
      if (b.error) msg = b.error;
    } catch { /* ignore */ }
    throw new Error(msg);
  }
  const raw = (await res.json()) as Record<string, unknown>;
  return {
    regionId: typeof raw.regionId === 'string' ? raw.regionId : regionId,
    displayName: typeof raw.displayName === 'string' ? raw.displayName : '',
    segments: normalizeSegments(raw.segments),
  };
}

export async function putPlan(
  regionId: string,
  segments: BandSegment[],
  signal?: AbortSignal,
): Promise<BandPlanDto> {
  const res = await fetch('/api/bands/plan', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ regionId, segments }),
    signal,
  });
  if (!res.ok) {
    let msg = `PUT /api/bands/plan → ${res.status}`;
    try {
      const b = (await res.json()) as { error?: string };
      if (b.error) msg = b.error;
    } catch { /* ignore */ }
    throw new Error(msg);
  }
  const raw = (await res.json()) as Record<string, unknown>;
  return {
    regionId: typeof raw.regionId === 'string' ? raw.regionId : regionId,
    segments: normalizeSegments(raw.segments),
  };
}

export async function deletePlan(regionId: string, signal?: AbortSignal): Promise<void> {
  const res = await fetch(`/api/bands/plan/${encodeURIComponent(regionId)}`, {
    method: 'DELETE',
    signal,
  });
  if (!res.ok && res.status !== 204)
    throw new Error(`DELETE /api/bands/plan → ${res.status}`);
}

// ── utility functions used by BandPlanContext ─────────────────────────────────

// Binary-search the sorted segments array for the segment containing freqHz.
export function binarySearchSegment(
  segments: BandSegment[],
  freqHz: number,
): BandSegment | null {
  let lo = 0;
  let hi = segments.length - 1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    const seg = segments[mid]!;
    if (freqHz < seg.lowHz)       hi = mid - 1;
    else if (freqHz > seg.highHz)  lo = mid + 1;
    else                           return seg;
  }
  return null;
}

// Returns true if the mode falls within the segment's restriction.
export function modeMatchesRestriction(
  restriction: ModeRestriction,
  mode: RxMode,
): boolean {
  switch (restriction) {
    case 'Any':
      return true;
    case 'CwOnly':
      return mode === 'CWU' || mode === 'CWL';
    case 'PhoneOnly':
      return mode === 'USB' || mode === 'LSB' || mode === 'AM' || mode === 'SAM' || mode === 'DSB' || mode === 'FM';
    case 'DigitalOnly':
      return mode === 'DIGL' || mode === 'DIGU';
  }
}
