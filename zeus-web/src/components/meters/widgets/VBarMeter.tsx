// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Vertical-bar variant of HBarMeter. Same color rules / peak-hold ink;
// different geometry (bar fills bottom-to-top, peak is a horizontal tick
// across the bar at the held position).

import type { CSSProperties } from 'react';
import type { MeterReadingDef } from '../meterCatalog';
import type { WidgetSettings } from '../metersConfig';
import { PEAK_HOLD_FILL, _isSilent, _fillColorForValue } from './HBarMeter';

interface VBarMeterProps {
  value: number;
  peak?: number;
  def: MeterReadingDef;
  settings: WidgetSettings;
  width?: number;
  height?: number;
}

function fractionOf(min: number, max: number, value: number): number {
  if (!isFinite(value)) return 0;
  if (max <= min) return 0;
  return Math.max(0, Math.min(1, (value - min) / (max - min)));
}

export function VBarMeter({
  value,
  peak,
  def,
  settings,
  width = 14,
  height = 80,
}: VBarMeterProps) {
  const min = settings.min ?? def.defaultMin;
  const max = settings.max ?? def.defaultMax;
  const silent = _isSilent(value);
  const f = silent ? 0 : fractionOf(min, max, value);
  const peakF =
    peak !== undefined && !_isSilent(peak) ? fractionOf(min, max, peak) : null;
  const showPeak =
    settings.peakHold !== false && peakF !== null && peakF > f && !silent;
  const color = _fillColorForValue(def, value);
  const isSignalGradient = def.colorToken === 'amber-signal';

  const containerStyle: CSSProperties = {
    position: 'relative',
    width,
    height,
    background: 'var(--meter-bg)',
    border: '1px solid var(--panel-border)',
    borderRadius: 'var(--r-xs)',
    overflow: 'hidden',
  };
  const fillStyle: CSSProperties = {
    position: 'absolute',
    left: 0,
    right: 0,
    bottom: 0,
    height: `${f * 100}%`,
    background: isSignalGradient
      ? 'linear-gradient(0deg, rgba(255,160,40,0.18) 0%, rgba(255,160,40,0.55) 50%, rgba(255,160,40,1) 100%)'
      : color,
    transition: 'height 80ms linear',
    pointerEvents: 'none',
  };

  return (
    <div style={containerStyle} aria-hidden="true">
      <div style={fillStyle} />
      {showPeak && (
        <div
          aria-hidden="true"
          style={{
            position: 'absolute',
            left: 0,
            right: 0,
            bottom: `calc(${(peakF ?? 0) * 100}% - 1px)`,
            height: 2,
            background: PEAK_HOLD_FILL,
            pointerEvents: 'none',
          }}
        />
      )}
    </div>
  );
}
