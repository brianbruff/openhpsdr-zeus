// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Licensed under the GNU General Public License v2.0-or-later. See the LICENSE
// file at the repository root for the full text.

import { useMemo } from 'react';
import type { LogEntry } from '../../api/log';
import {
  bandHistogram,
  modeBreakdown,
  topCountries,
  countThisMonth,
  awardStats,
  type ModeCount,
} from './logbook-stats';
import { countryToFlag } from './logbook-flag';
import { LogbookAwardDial, LogbookRingGauge, sliceColor, type InstrumentSlice } from './LogbookInstrumentSvg';

const DAY_MS = 86_400_000;
const HEATMAP_WEEKS = 26;

type ActivityCell = {
  key: string;
  count: number;
  level: number;
};

function ringSlices<T extends { count: number }>(
  rows: T[],
  keyOf: (row: T) => string,
  labelOf: (row: T) => string,
  pctOf?: (row: T) => number,
): InstrumentSlice[] {
  return rows.map((row) => ({
    key: keyOf(row),
    label: labelOf(row),
    value: row.count,
    pct: pctOf?.(row),
  }));
}

function utcDay(value: Date): number {
  return Date.UTC(value.getUTCFullYear(), value.getUTCMonth(), value.getUTCDate());
}

function activityCells(entries: LogEntry[], now: Date): ActivityCell[] {
  const days = HEATMAP_WEEKS * 7;
  const end = utcDay(now);
  const start = end - (days - 1) * DAY_MS;
  const counts = new Map<number, number>();

  for (const entry of entries) {
    const day = utcDay(new Date(entry.qsoDateTimeUtc));
    if (!Number.isFinite(day) || day < start || day > end) continue;
    counts.set(day, (counts.get(day) ?? 0) + 1);
  }

  const max = Math.max(1, ...counts.values());
  return Array.from({ length: days }, (_, index) => {
    const day = start + index * DAY_MS;
    const count = counts.get(day) ?? 0;
    return {
      key: new Date(day).toISOString().slice(0, 10),
      count,
      level: count === 0 ? 0 : Math.max(1, Math.ceil((count / max) * 4)),
    };
  });
}

function ActivityHeatmap({ entries }: { entries: LogEntry[] }) {
  const todayKey = new Date().toISOString().slice(0, 10);
  const cells = useMemo(() => activityCells(entries, new Date()), [entries, todayKey]);
  const total = cells.reduce((sum, cell) => sum + cell.count, 0);

  return (
    <div className="lb-dash-tile lb-dash-activity">
      <div className="lb-dash-title">Activity · 26 Weeks</div>
      <div className="lb-heatmap-grid" aria-label={`${total} QSOs in the last 26 weeks`}>
        {cells.map((cell) => (
          <span
            key={cell.key}
            className="lb-heat-cell"
            data-level={cell.level}
            title={`${cell.key}: ${cell.count} QSO${cell.count === 1 ? '' : 's'}`}
          />
        ))}
      </div>
    </div>
  );
}

export function LogbookDashboard({
  entries,
  totalCount,
  fullyLoaded,
}: {
  entries: LogEntry[];
  totalCount: number;
  fullyLoaded: boolean;
}) {
  const bands = useMemo(() => bandHistogram(entries), [entries]);
  const modes = useMemo<ModeCount[]>(() => modeBreakdown(entries, 3), [entries]);
  const countries = useMemo(() => topCountries(entries, 5), [entries]);
  const thisMonth = useMemo(() => countThisMonth(entries, new Date()), [entries]);
  const awards = useMemo(() => awardStats(entries), [entries]);
  const bandSlices = useMemo(
    () => ringSlices(bands, (band) => band.band, (band) => band.band),
    [bands],
  );
  const modeSlices = useMemo(
    () => ringSlices(modes, (mode) => mode.mode, (mode) => mode.mode, (mode) => mode.pct),
    [modes],
  );
  const totalRing = modeSlices.length > 0 ? modeSlices : bandSlices;
  const topMode = modes[0] ?? null;

  return (
    <div className="lb-dash">
      <div className="lb-dash-tile lb-dash-total">
        <LogbookRingGauge
          slices={totalRing}
          ariaLabel="Total QSO mix"
          center={
            <>
              <span className="lb-total-num">{totalCount.toLocaleString()}</span>
              <span className="lb-ring-caption">Total QSOs</span>
            </>
          }
        />
        <div className="lb-dash-total-labels">
          <div className="lb-stat">
            <span className="lb-stat-num">{totalCount.toLocaleString()}</span>
            <span className="lb-stat-cap">Total QSOs</span>
          </div>
          <div className="lb-stat">
            <span className="lb-stat-num">{thisMonth.toLocaleString()}</span>
            <span className="lb-stat-cap">This Month</span>
          </div>
        </div>
      </div>

      <div className="lb-dash-tile lb-dash-bands">
        <div className="lb-dash-title">Bands</div>
        <div className="lb-ring-row">
          <LogbookRingGauge
            slices={bandSlices}
            ariaLabel="QSO bands"
            size={90}
            center={
              <>
                <span className="lb-total-num">{entries.length.toLocaleString()}</span>
                <span className="lb-ring-caption">Loaded</span>
              </>
            }
          />
          <div className="lb-modes-legend">
            {bands.length === 0 && <div className="lb-dash-empty">No bands yet</div>}
            {bands.slice(0, 5).map((band, index) => (
              <div key={band.band} className="lb-legend-row">
                <span className="lb-legend-sw" style={{ background: sliceColor(index) }} />
                <span className="lb-legend-name">{band.band}</span>
                <span className="lb-legend-pct mono">{band.count}</span>
              </div>
            ))}
          </div>
        </div>
      </div>

      <div className="lb-dash-tile lb-dash-modes">
        <div className="lb-dash-title">Modes</div>
        <div className="lb-ring-row">
          <LogbookRingGauge
            slices={modeSlices}
            ariaLabel="QSO modes"
            size={90}
            center={
              <>
                <span className="lb-total-num">{topMode ? `${topMode.pct}%` : '0%'}</span>
                <span className="lb-ring-caption">{topMode?.mode ?? 'Modes'}</span>
              </>
            }
          />
          <div className="lb-modes-legend">
            {modes.length === 0 && <div className="lb-dash-empty">No modes yet</div>}
            {modes.map((m, i) => (
              <div key={m.mode} className="lb-legend-row">
                <span className="lb-legend-sw" style={{ background: sliceColor(i) }} />
                <span className="lb-legend-name">{m.mode}</span>
                <span className="lb-legend-pct mono">{m.pct}%</span>
              </div>
            ))}
          </div>
        </div>
      </div>

      <ActivityHeatmap entries={entries} />

      <div className="lb-dash-tile lb-dash-countries">
        <div className="lb-dash-title">Top Countries</div>
        <div className="lb-countries">
          {countries.length === 0 && <div className="lb-dash-empty">No countries yet</div>}
          {countries.map((c) => (
            <div key={c.country} className="lb-country-row">
              <span className="lb-country-flag" aria-hidden="true">{countryToFlag(c.country)}</span>
              <span className="lb-country-name">{c.country}</span>
              <span className="lb-country-count mono">{c.count.toLocaleString()}</span>
            </div>
          ))}
        </div>
      </div>

      <div className="lb-dash-tile lb-dash-awards">
        <div className="lb-dash-title">Awards</div>
        <div className="lb-awards-grid">
          <LogbookAwardDial
            label="DXCC"
            value={awards.dxccWorked.toLocaleString()}
            detail={`${awards.dxccConfirmed.toLocaleString()} confirmed`}
            progressPct={(awards.dxccWorked / 340) * 100}
            toneIndex={0}
          />
          <LogbookAwardDial
            label="WAS"
            value={`${awards.statesWorked}/50`}
            detail="US states"
            progressPct={(awards.statesWorked / 50) * 100}
            toneIndex={1}
          />
          <LogbookAwardDial
            label="Grids"
            value={awards.gridsWorked.toLocaleString()}
            detail="4-char squares"
            progressPct={awards.gridsWorked}
            toneIndex={3}
          />
        </div>
      </div>

      {!fullyLoaded && (
        <div className="lb-dash-loading" title="Loading the full logbook for accurate statistics">
          Loading full log…
        </div>
      )}
    </div>
  );
}
