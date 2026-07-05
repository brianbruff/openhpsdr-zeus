// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Licensed under the GNU General Public License v2.0-or-later. See the LICENSE
// file at the repository root for the full text.

import { useId, useMemo } from 'react';
import type { LogEntry } from '../../api/log';
import { useOperatorStore } from '../../state/operator-store';
import { useQrzStore } from '../../state/qrz-store';
import { prefixDefs } from '../meters/render/svgChrome';
import { gridToLatLon } from './geo';
import { geoPoints } from './logbook-stats';
import { RADAR_MAX_MILES, RADAR_RINGS_MILES, workedRadarPoints } from './logbook-radar';

const SIGNAL_AMBER = '#FFA028';
const RADAR_CENTER = 180;
const RADAR_RADIUS = 144;

type HomePoint = { lat: number; lon: number };

function useHomePoint(): HomePoint | null {
  const qrzHomeLat = useQrzStore((s) => s.home?.lat ?? null);
  const qrzHomeLon = useQrzStore((s) => s.home?.lon ?? null);
  const resolvedGrid = useOperatorStore((s) => s.resolvedGrid);

  return useMemo(() => {
    if (qrzHomeLat != null && qrzHomeLon != null) {
      return { lat: qrzHomeLat, lon: qrzHomeLon };
    }
    const ll = resolvedGrid ? gridToLatLon(resolvedGrid) : null;
    return ll ? { lat: ll.lat, lon: ll.lon } : null;
  }, [qrzHomeLat, qrzHomeLon, resolvedGrid]);
}

export function LogbookDxRadar({ entries }: { entries: LogEntry[] }) {
  const titleId = useId();
  const floorId = prefixDefs(titleId, 'floor');
  const dotGlowId = prefixDefs(titleId, 'dot-glow');
  const home = useHomePoint();
  const points = useMemo(() => geoPoints(entries), [entries]);
  const radarPoints = useMemo(
    () => (home ? workedRadarPoints(home, points, RADAR_RADIUS) : []),
    [home, points],
  );
  const maxCount = radarPoints.reduce((max, point) => Math.max(max, point.count), 1);

  if (!home) {
    return (
      <div className="logbook-dx-radar logbook-dx-radar--empty">
        <div className="lb-dash-empty">Set a home grid to enable DX radar</div>
      </div>
    );
  }

  return (
    <div className="logbook-dx-radar">
      <svg viewBox="0 0 360 360" aria-labelledby={titleId} role="img">
        <title id={titleId}>DX radar bearing and distance plot</title>
        <defs>
          <radialGradient id={floorId} cx="50%" cy="44%" r="57%">
            <stop offset="0" stopColor="var(--meter-well-edge)" />
            <stop offset="1" stopColor="var(--meter-well-floor)" />
          </radialGradient>
          <filter id={dotGlowId} x="-80%" y="-80%" width="260%" height="260%">
            <feGaussianBlur stdDeviation="2.2" result="blur" />
            <feMerge>
              <feMergeNode in="blur" />
              <feMergeNode in="SourceGraphic" />
            </feMerge>
          </filter>
        </defs>
        <circle cx={RADAR_CENTER} cy={RADAR_CENTER} r="164" fill={`url(#${floorId})`} />
        {RADAR_RINGS_MILES.map((miles) => {
          const radius = (miles / RADAR_MAX_MILES) * RADAR_RADIUS;
          return (
            <g key={miles}>
              <circle className="dx-radar-ring" cx={RADAR_CENTER} cy={RADAR_CENTER} r={radius} fill="none" />
              <text className="dx-radar-range" x={RADAR_CENTER + 8} y={RADAR_CENTER - radius + 12}>
                {miles / 1000}k mi
              </text>
            </g>
          );
        })}
        {[0, 45, 90, 135, 180, 225, 270, 315].map((bearing) => {
          const theta = ((bearing - 90) * Math.PI) / 180;
          const x = RADAR_CENTER + Math.cos(theta) * RADAR_RADIUS;
          const y = RADAR_CENTER + Math.sin(theta) * RADAR_RADIUS;
          return (
            <line
              key={bearing}
              className="dx-radar-spoke"
              x1={RADAR_CENTER}
              y1={RADAR_CENTER}
              x2={x}
              y2={y}
            />
          );
        })}
        {[
          ['N', RADAR_CENTER, 25],
          ['E', 337, RADAR_CENTER + 4],
          ['S', RADAR_CENTER, 340],
          ['W', 21, RADAR_CENTER + 4],
        ].map(([label, x, y]) => (
          <text key={label} className="dx-radar-cardinal" x={x} y={y}>
            {label}
          </text>
        ))}
        {radarPoints.map((point) => {
          const weight = point.count / maxCount;
          return (
            <circle
              key={`${point.lat}:${point.lon}`}
              data-radar-dot
              cx={RADAR_CENTER + point.x}
              cy={RADAR_CENTER + point.y}
              r={2.8 + weight * 3.7}
              fill={SIGNAL_AMBER}
              opacity={point.clipped ? 0.62 : 0.9}
              filter={`url(#${dotGlowId})`}
            />
          );
        })}
        <circle className="dx-radar-home" cx={RADAR_CENTER} cy={RADAR_CENTER} r="7" />
      </svg>
      <div className="dx-radar-readout mono">
        {radarPoints.length === 0 ? 'No gridded QSOs yet' : `${radarPoints.length} worked locations`}
      </div>
    </div>
  );
}
