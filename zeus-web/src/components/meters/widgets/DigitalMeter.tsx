// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Big numeric readout. Mirrors the existing top-bar chip typography
// (Archivo Narrow, tabular-nums). Color flips to --tx when the value
// crosses dangerAt; --power yellow at warnAt.

import type { CSSProperties } from 'react';
import type { MeterReadingDef } from '../meterCatalog';
import type { WidgetSettings } from '../metersConfig';
import { _isSilent } from './HBarMeter';

interface DigitalMeterProps {
  value: number;
  def: MeterReadingDef;
  settings: WidgetSettings;
}

function colorForValue(def: MeterReadingDef, value: number): string {
  if (!isFinite(value)) return 'var(--fg-2)';
  if (def.dangerAt !== undefined && value >= def.dangerAt) return 'var(--tx)';
  if (def.warnAt !== undefined && value >= def.warnAt) return 'var(--power)';
  return 'var(--fg-0)';
}

function formatValue(def: MeterReadingDef, value: number): string {
  if (_isSilent(value)) return '—';
  switch (def.unit) {
    case 'ratio':
      return value.toFixed(2);
    case 'W':
      return value < 10 ? value.toFixed(2) : value.toFixed(1);
    case 'dB':
    case 'dBFS':
    case 'dBm':
      return value.toFixed(0);
    default:
      return value.toFixed(1);
  }
}

export function DigitalMeter({ value, def, settings }: DigitalMeterProps) {
  const label = settings.label ?? def.short;
  const color = colorForValue(def, value);
  const containerStyle: CSSProperties = {
    display: 'flex',
    flexDirection: 'column',
    gap: 2,
    padding: '6px 10px',
    background: 'var(--meter-bg)',
    border: '1px solid var(--panel-border)',
    borderRadius: 'var(--r-xs)',
    minWidth: 90,
  };
  const labelStyle: CSSProperties = {
    fontSize: 10,
    textTransform: 'uppercase',
    letterSpacing: '0.08em',
    color: 'var(--fg-2)',
    fontFamily: 'var(--font-mono)',
  };
  const valueStyle: CSSProperties = {
    fontSize: 22,
    lineHeight: 1,
    color,
    fontFamily: 'var(--font-mono)',
    fontVariantNumeric: 'tabular-nums',
    fontWeight: 600,
  };
  const unitStyle: CSSProperties = {
    fontSize: 10,
    color: 'var(--fg-3)',
    fontFamily: 'var(--font-mono)',
    marginLeft: 4,
  };
  return (
    <div style={containerStyle}>
      <span style={labelStyle}>{label}</span>
      <span style={valueStyle}>
        {formatValue(def, value)}
        <span style={unitStyle}>{def.unit}</span>
      </span>
    </div>
  );
}
