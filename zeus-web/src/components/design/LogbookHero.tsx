// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Licensed under the GNU General Public License v2.0-or-later. See the LICENSE
// file at the repository root for the full text.

import { useMemo, useState } from 'react';
import type { LogEntry } from '../../api/log';
import { heroStats } from './logbook-stats';
import { LogbookDxRadar } from './LogbookDxRadar';
import { ZeusGlobe } from './ZeusGlobe';

type HeroView = 'globe' | 'radar';

export function LogbookHero({
  entries,
  totalCount,
}: {
  entries: LogEntry[];
  totalCount: number;
}) {
  const [view, setView] = useState<HeroView>('globe');
  const stats = useMemo(() => heroStats(entries, totalCount), [entries, totalCount]);

  return (
    <section className="lb-hero" aria-label="Logbook world view">
      <div className="lb-hero-head">
        <div className="lb-hero-copy">
          <div className="lb-hero-title">Worked The World</div>
          <div className="lb-hero-subtitle">Live great-circle map</div>
        </div>
        <div className="lb-hero-stats" aria-label="Logbook totals">
          <div className="lb-hero-stat">
            <span className="lb-hero-stat-num">{stats.qsos.toLocaleString()}</span>
            <span className="lb-hero-stat-label">QSOs</span>
          </div>
          <div className="lb-hero-stat">
            <span className="lb-hero-stat-num">{stats.grids.toLocaleString()}</span>
            <span className="lb-hero-stat-label">Grids</span>
          </div>
          <div className="lb-hero-stat">
            <span className="lb-hero-stat-num">{stats.dxcc.toLocaleString()}</span>
            <span className="lb-hero-stat-label">DXCC</span>
          </div>
          <div className="lb-hero-stat">
            <span className="lb-hero-stat-num">{stats.bands.toLocaleString()}</span>
            <span className="lb-hero-stat-label">Bands</span>
          </div>
        </div>
        <div className="lb-hero-toggle" role="group" aria-label="Hero view">
          <button
            type="button"
            aria-pressed={view === 'globe'}
            onClick={() => setView('globe')}
          >
            Globe
          </button>
          <button
            type="button"
            aria-pressed={view === 'radar'}
            onClick={() => setView('radar')}
          >
            DX Radar
          </button>
        </div>
      </div>
      <div className="lb-hero-stage">
        {view === 'globe' ? <ZeusGlobe entries={entries} /> : <LogbookDxRadar entries={entries} />}
      </div>
    </section>
  );
}
