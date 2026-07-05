// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Licensed under the GNU General Public License v2.0-or-later. See the LICENSE
// file at the repository root for the full text.

import { useId, type CSSProperties, type ReactNode } from 'react';
import { prefixDefs } from '../meters/render/svgChrome';

export const SLICE_COLORS = [
  'var(--accent-bright)',
  'var(--power)',
  'var(--ok)',
  'var(--tx)',
  'var(--orange)',
  'var(--fg-3)',
];

export type InstrumentSlice = {
  key: string;
  label: string;
  value: number;
  pct?: number;
};

type NormalizedSlice = InstrumentSlice & {
  pct: number;
  color: string;
};

export function sliceColor(index: number): string {
  return SLICE_COLORS[Math.min(index, SLICE_COLORS.length - 1)] ?? 'var(--fg-3)';
}

export function normalizeInstrumentSlices(slices: InstrumentSlice[]): NormalizedSlice[] {
  const total = slices.reduce((sum, slice) => sum + Math.max(0, slice.value), 0);
  if (slices.length === 0 || total <= 0) return [];
  return slices.map((slice, index) => ({
    ...slice,
    pct: slice.pct ?? Math.round((Math.max(0, slice.value) / total) * 100),
    color: sliceColor(index),
  }));
}

function polar(cx: number, cy: number, radius: number, angleDeg: number) {
  const angle = ((angleDeg - 90) * Math.PI) / 180;
  return {
    x: cx + radius * Math.cos(angle),
    y: cy + radius * Math.sin(angle),
  };
}

function arcPath(cx: number, cy: number, radius: number, startDeg: number, endDeg: number): string {
  const sweep = Math.max(0.01, endDeg - startDeg);
  const end = polar(cx, cy, radius, startDeg);
  const start = polar(cx, cy, radius, endDeg);
  const largeArc = sweep > 180 ? 1 : 0;
  return `M ${start.x.toFixed(3)} ${start.y.toFixed(3)} A ${radius} ${radius} 0 ${largeArc} 0 ${end.x.toFixed(3)} ${end.y.toFixed(3)}`;
}

function InstrumentDefs({ defsId }: { defsId: string }) {
  return (
    <defs>
      <radialGradient id={prefixDefs(defsId, 'bezel')} cx="32%" cy="24%" r="74%">
        <stop offset="0" stopColor="var(--meter-bezel-hi)" />
        <stop offset="0.42" stopColor="var(--meter-bezel-mid)" />
        <stop offset="1" stopColor="var(--meter-bezel-lo)" />
      </radialGradient>
      <radialGradient id={prefixDefs(defsId, 'well')} cx="36%" cy="24%" r="78%">
        <stop offset="0" stopColor="var(--meter-well-edge)" />
        <stop offset="0.62" stopColor="var(--meter-well-floor)" />
        <stop offset="1" stopColor="var(--meter-well-floor)" />
      </radialGradient>
      <radialGradient id={prefixDefs(defsId, 'glass')} cx="32%" cy="18%" r="72%">
        <stop offset="0" stopColor="var(--meter-glass-dome)" />
        <stop offset="0.48" stopColor="var(--meter-glass-top)" />
        <stop offset="1" stopColor="var(--meter-glass-bot)" />
      </radialGradient>
      <filter id={prefixDefs(defsId, 'glow')} x="-40%" y="-40%" width="180%" height="180%">
        <feGaussianBlur stdDeviation="1.35" result="blur" />
        <feMerge>
          <feMergeNode in="blur" />
          <feMergeNode in="SourceGraphic" />
        </feMerge>
      </filter>
    </defs>
  );
}

function SliceGradient({
  defsId,
  index,
  color,
}: {
  defsId: string;
  index: number;
  color: string;
}) {
  return (
    <linearGradient id={prefixDefs(defsId, `slice-${index}`)} x1="18%" y1="0%" x2="82%" y2="100%">
      <stop offset="0" stopColor="var(--meter-bezel-hi-sm)" />
      <stop offset="0.18" stopColor={color} />
      <stop offset="0.72" stopColor={color} />
      <stop offset="1" stopColor="var(--meter-well-floor)" />
    </linearGradient>
  );
}

export function LogbookRingGauge({
  slices,
  center,
  ariaLabel,
  size = 104,
  className,
}: {
  slices: InstrumentSlice[];
  center: ReactNode;
  ariaLabel: string;
  size?: number;
  className?: string;
}) {
  const id = useId();
  const defsId = prefixDefs(`lb-ring-${id}`, 'defs');
  const normalized = normalizeInstrumentSlices(slices);
  const style = { '--lb-ring-size': `${size}px` } as CSSProperties;
  const radius = 40;
  let cursor = -90;

  return (
    <div className={`lb-ring-gauge${className ? ` ${className}` : ''}`} style={style}>
      <svg viewBox="0 0 112 112" role="img" aria-label={ariaLabel}>
        <InstrumentDefs defsId={defsId} />
        <defs>
          {normalized.map((slice, index) => (
            <SliceGradient key={`${slice.key}-grad`} defsId={defsId} index={index} color={slice.color} />
          ))}
        </defs>
        <circle cx="56" cy="56" r="53" fill={`url(#${prefixDefs(defsId, 'bezel')})`} />
        <circle cx="56" cy="56" r="45" fill={`url(#${prefixDefs(defsId, 'well')})`} />
        <circle cx="56" cy="56" r={radius} fill="none" stroke="var(--immersive-arc-track-rim)" strokeWidth="11" />
        {normalized.map((slice, index) => {
          const rawSweep = (slice.pct / 100) * 360;
          const gap = normalized.length > 1 ? 2 : 0;
          const start = cursor + gap / 2;
          const end = Math.min(cursor + rawSweep - gap / 2, start + 359.8);
          cursor += rawSweep;
          if (end <= start) return null;
          return (
            <path
              key={slice.key}
              data-slice={slice.key}
              data-pct={slice.pct}
              d={arcPath(56, 56, radius, start, end)}
              fill="none"
              stroke={`url(#${prefixDefs(defsId, `slice-${index}`)})`}
              strokeWidth="11"
              strokeLinecap="round"
              filter={`url(#${prefixDefs(defsId, 'glow')})`}
            />
          );
        })}
        <path
          d={arcPath(56, 56, 47, -58, 58)}
          fill="none"
          stroke="var(--meter-glass-dome)"
          strokeWidth="2.3"
          strokeLinecap="round"
        />
        <circle cx="56" cy="56" r="52" fill={`url(#${prefixDefs(defsId, 'glass')})`} />
        <circle cx="56" cy="56" r="53" fill="none" stroke="var(--meter-bezel-ring)" strokeWidth="1" />
      </svg>
      <div className="lb-ring-center">{center}</div>
    </div>
  );
}

export function LogbookAwardDial({
  label,
  value,
  detail,
  progressPct,
  toneIndex,
}: {
  label: string;
  value: string;
  detail: string;
  progressPct: number;
  toneIndex: number;
}) {
  const id = useId();
  const defsId = prefixDefs(`lb-award-${id}`, 'defs');
  const pct = Math.max(0, Math.min(100, progressPct));
  const sweep = pct > 0 ? Math.max(2, (pct / 100) * 270) : 0;
  const color = sliceColor(toneIndex);

  return (
    <div className="lb-award">
      <svg className="lb-award-dial" viewBox="0 0 56 56" role="img" aria-label={`${label} ${value}`}>
        <InstrumentDefs defsId={defsId} />
        <defs>
          <SliceGradient defsId={defsId} index={0} color={color} />
        </defs>
        <circle cx="28" cy="28" r="26" fill={`url(#${prefixDefs(defsId, 'bezel')})`} />
        <circle cx="28" cy="28" r="20.5" fill={`url(#${prefixDefs(defsId, 'well')})`} />
        <path
          d={arcPath(28, 28, 17.5, -135, 135)}
          fill="none"
          stroke="var(--immersive-arc-track-rim)"
          strokeWidth="4"
          strokeLinecap="round"
        />
        {sweep > 0 && (
          <path
            data-award-progress={label}
            data-pct={Math.round(pct)}
            d={arcPath(28, 28, 17.5, -135, -135 + sweep)}
            fill="none"
            stroke={`url(#${prefixDefs(defsId, 'slice-0')})`}
            strokeWidth="4"
            strokeLinecap="round"
            filter={`url(#${prefixDefs(defsId, 'glow')})`}
          />
        )}
        <circle cx="28" cy="28" r="25" fill={`url(#${prefixDefs(defsId, 'glass')})`} />
      </svg>
      <span className="lb-award-num">{value}</span>
      <span className="lb-award-label">{label}</span>
      <small>{detail}</small>
    </div>
  );
}
