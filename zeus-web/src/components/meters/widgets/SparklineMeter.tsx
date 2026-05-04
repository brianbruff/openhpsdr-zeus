// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Rolling sample buffer rendered as an SVG polyline. 60 samples by default,
// pushed each render — at the 5 Hz / 10 Hz wire rates that's 12 s / 6 s of
// recent history.
//
// Uses the same color rules as HBarMeter so the line tracks the operator's
// expectation across widget kinds (red on TX overdrive, accent blue for
// nominal level / AGC, amber for RX signal strength).

import { useEffect, useRef, useState } from 'react';
import type { MeterReadingDef } from '../meterCatalog';
import type { WidgetSettings } from '../metersConfig';
import { _isSilent, _fillColorForValue } from './HBarMeter';

const DEFAULT_BUFFER = 60;

interface SparklineMeterProps {
  value: number;
  def: MeterReadingDef;
  settings: WidgetSettings;
  width?: number;
  height?: number;
  bufferSize?: number;
}

export function SparklineMeter({
  value,
  def,
  settings,
  width = 160,
  height = 42,
  bufferSize = DEFAULT_BUFFER,
}: SparklineMeterProps) {
  const bufRef = useRef<number[]>([]);
  const [, force] = useState(0);

  useEffect(() => {
    const buf = bufRef.current;
    buf.push(value);
    while (buf.length > bufferSize) buf.shift();
    force((n) => (n + 1) % 1024);
  }, [value, bufferSize]);

  const min = settings.min ?? def.defaultMin;
  const max = settings.max ?? def.defaultMax;
  const span = Math.max(1e-6, max - min);
  const isSignalGradient = def.colorToken === 'amber-signal';
  const stroke = isSignalGradient ? '#FFA028' : _fillColorForValue(def, value);

  const buf = bufRef.current;
  const points: string[] = [];
  for (let i = 0; i < buf.length; i++) {
    const v = buf[i];
    if (v === undefined || !isFinite(v) || _isSilent(v)) continue;
    const x = (i / Math.max(1, bufferSize - 1)) * width;
    const fy = Math.max(0, Math.min(1, (v - min) / span));
    const y = height - fy * height;
    points.push(`${x.toFixed(1)},${y.toFixed(1)}`);
  }

  return (
    <svg
      width={width}
      height={height}
      style={{
        background: 'var(--meter-bg)',
        border: '1px solid var(--panel-border)',
        borderRadius: 'var(--r-xs)',
      }}
      aria-hidden="true"
    >
      {/* faint mid-line at the axis midpoint */}
      <line
        x1={0}
        x2={width}
        y1={height / 2}
        y2={height / 2}
        stroke="var(--panel-border)"
        strokeWidth={1}
      />
      {points.length >= 2 && (
        <polyline
          points={points.join(' ')}
          fill="none"
          stroke={stroke}
          strokeWidth={1.5}
          strokeLinejoin="round"
          strokeLinecap="round"
        />
      )}
    </svg>
  );
}
