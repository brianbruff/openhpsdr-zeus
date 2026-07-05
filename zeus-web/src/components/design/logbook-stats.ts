// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Licensed under the GNU General Public License v2.0-or-later. See the LICENSE
// file at the repository root for the full text.

import type { LogEntry } from '../../api/log';
import { gridToLatLon } from './geo';

// Pure aggregation helpers behind the Logbook dashboard. Every figure is derived
// from the in-memory entry set the workspace loads (the full log, not the
// 100-row quick page), so the numbers are honest only when the caller has
// hydrated the complete logbook first — see LogbookWorkspace's loadAllEntries.

// HF/6m band ordering (low → high) so the band histogram reads like a rig's band
// stack. Bands outside this list (unlikely) sort after, alphabetically.
const BAND_ORDER = ['160M', '80M', '60M', '40M', '30M', '20M', '17M', '15M', '12M', '10M', '6M', '2M'];
const US_STATES = new Set([
  'AL', 'AK', 'AZ', 'AR', 'CA', 'CO', 'CT', 'DE', 'FL', 'GA',
  'HI', 'ID', 'IL', 'IN', 'IA', 'KS', 'KY', 'LA', 'ME', 'MD',
  'MA', 'MI', 'MN', 'MS', 'MO', 'MT', 'NE', 'NV', 'NH', 'NJ',
  'NM', 'NY', 'NC', 'ND', 'OH', 'OK', 'OR', 'PA', 'RI', 'SC',
  'SD', 'TN', 'TX', 'UT', 'VT', 'VA', 'WA', 'WV', 'WI', 'WY',
]);

export type BandCount = { band: string; count: number };
export type ModeCount = { mode: string; count: number; pct: number };
export type CountryCount = { country: string; count: number };
export type GeoPoint = { lat: number; lon: number; count: number };
export type AwardStats = {
  dxccWorked: number;
  dxccConfirmed: number;
  statesWorked: number;
  gridsWorked: number;
};
export type HeroStats = {
  qsos: number;
  grids: number;
  dxcc: number;
  bands: number;
};

function normBand(band: string | null | undefined): string {
  return (band ?? '').trim().toUpperCase();
}

/** QSO counts per band, ordered low→high frequency; empty bands are omitted. */
export function bandHistogram(entries: LogEntry[]): BandCount[] {
  const counts = new Map<string, number>();
  for (const e of entries) {
    const band = normBand(e.band);
    if (!band) continue;
    counts.set(band, (counts.get(band) ?? 0) + 1);
  }
  return [...counts.entries()]
    .map(([band, count]) => ({ band, count }))
    .sort((a, b) => {
      const ia = BAND_ORDER.indexOf(a.band);
      const ib = BAND_ORDER.indexOf(b.band);
      if (ia !== -1 && ib !== -1) return ia - ib;
      if (ia !== -1) return -1;
      if (ib !== -1) return 1;
      return a.band.localeCompare(b.band);
    });
}

/**
 * QSO share per mode, most-worked first, each carrying an integer percentage of
 * the total. Percentages are rounded independently and may not sum to exactly
 * 100 — acceptable for a legend read. Modes below `topN` roll into "Other".
 */
export function modeBreakdown(entries: LogEntry[], topN = 3): ModeCount[] {
  const counts = new Map<string, number>();
  let total = 0;
  for (const e of entries) {
    const mode = (e.mode ?? '').trim().toUpperCase();
    if (!mode) continue;
    counts.set(mode, (counts.get(mode) ?? 0) + 1);
    total += 1;
  }
  if (total === 0) return [];
  const sorted = [...counts.entries()].sort((a, b) => b[1] - a[1]);
  const top = sorted.slice(0, topN);
  const restCount = sorted.slice(topN).reduce((sum, [, n]) => sum + n, 0);
  const out: ModeCount[] = top.map(([mode, count]) => ({
    mode,
    count,
    pct: Math.round((count / total) * 100),
  }));
  if (restCount > 0) {
    out.push({ mode: 'Other', count: restCount, pct: Math.round((restCount / total) * 100) });
  }
  return out;
}

/** Most-worked DXCC entities, most first, capped at `limit`. */
export function topCountries(entries: LogEntry[], limit = 5): CountryCount[] {
  const counts = new Map<string, number>();
  for (const e of entries) {
    const country = (e.country ?? '').trim();
    if (!country) continue;
    counts.set(country, (counts.get(country) ?? 0) + 1);
  }
  return [...counts.entries()]
    .map(([country, count]) => ({ country, count }))
    .sort((a, b) => b.count - a.count || a.country.localeCompare(b.country))
    .slice(0, limit);
}

/** QSOs whose UTC timestamp falls in the same calendar month as `now` (UTC). */
export function countThisMonth(entries: LogEntry[], now: Date): number {
  const y = now.getUTCFullYear();
  const m = now.getUTCMonth();
  let n = 0;
  for (const e of entries) {
    const d = new Date(e.qsoDateTimeUtc);
    if (Number.isNaN(d.getTime())) continue;
    if (d.getUTCFullYear() === y && d.getUTCMonth() === m) n += 1;
  }
  return n;
}

/**
 * De-duplicated map points for the dashboard mini-map: every QSO with a valid
 * Maidenhead grid is projected to lat/lon and bucketed to ~1° so co-located
 * contacts stack into one brighter dot instead of thousands of overlapping
 * pixels. Returns nothing for grid-less logs (map renders empty gracefully).
 */
export function geoPoints(entries: LogEntry[]): GeoPoint[] {
  const buckets = new Map<string, GeoPoint>();
  for (const e of entries) {
    if (!e.grid) continue;
    const ll = gridToLatLon(e.grid);
    if (!ll) continue;
    const key = `${Math.round(ll.lat)},${Math.round(ll.lon)}`;
    const existing = buckets.get(key);
    if (existing) {
      existing.count += 1;
    } else {
      buckets.set(key, { lat: ll.lat, lon: ll.lon, count: 1 });
    }
  }
  return [...buckets.values()];
}

function isConfirmed(entry: LogEntry): boolean {
  return Boolean(
    entry.lotwQslRcvdUtc
    || entry.qrzQslRcvdUtc
    || (entry.qslRcvd ?? '').toUpperCase() === 'Y',
  );
}

export function awardStats(entries: LogEntry[]): AwardStats {
  const dxccWorked = new Set<number>();
  const dxccConfirmed = new Set<number>();
  const states = new Set<string>();
  const grids = new Set<string>();

  for (const entry of entries) {
    if (typeof entry.dxcc === 'number' && Number.isFinite(entry.dxcc)) {
      dxccWorked.add(entry.dxcc);
      if (isConfirmed(entry)) dxccConfirmed.add(entry.dxcc);
    }

    const state = (entry.state ?? '').trim().toUpperCase();
    if (US_STATES.has(state)) states.add(state);

    const grid = (entry.grid ?? '').trim().toUpperCase();
    if (/^[A-R]{2}[0-9]{2}/.test(grid)) grids.add(grid.slice(0, 4));
  }

  return {
    dxccWorked: dxccWorked.size,
    dxccConfirmed: dxccConfirmed.size,
    statesWorked: states.size,
    gridsWorked: grids.size,
  };
}

export function heroStats(entries: LogEntry[], totalCount: number): HeroStats {
  const awards = awardStats(entries);
  return {
    qsos: totalCount,
    grids: awards.gridsWorked,
    dxcc: awards.dxccWorked,
    bands: bandHistogram(entries).length,
  };
}
