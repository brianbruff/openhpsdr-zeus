// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

import { describe, expect, it } from 'vitest';
import {
  METER_CATALOG,
  METER_FILTERS,
  METER_READINGS,
  MeterReadingId,
  meterMatchesFilter,
} from '../meterCatalog';

describe('meterCatalog', () => {
  it('has a catalog entry for every MeterReadingId', () => {
    const ids = Object.values(MeterReadingId);
    for (const id of ids) {
      const def = METER_CATALOG[id];
      expect(def, `missing entry for ${id}`).toBeDefined();
      expect(def.id).toBe(id);
      expect(def.label.length).toBeGreaterThan(0);
      expect(def.short.length).toBeGreaterThan(0);
    }
  });

  it('has 27 readings (matches plan §3.1)', () => {
    expect(METER_READINGS.length).toBe(27);
  });

  it('every entry uses a known color token', () => {
    const allowed = new Set(['amber-signal', 'power', 'tx', 'accent']);
    for (const def of METER_READINGS) {
      expect(allowed.has(def.colorToken), `${def.id} has bad colorToken`).toBe(
        true,
      );
    }
  });

  it('every entry has sane axis defaults (min < max)', () => {
    for (const def of METER_READINGS) {
      expect(def.defaultMin).toBeLessThan(def.defaultMax);
    }
  });

  it('rx-signal entries default to amber-signal token', () => {
    for (const def of METER_READINGS) {
      if (def.category === 'rx-signal') {
        expect(def.colorToken).toBe('amber-signal');
      }
    }
  });

  it('TxFwdWatts is power-yellow by default', () => {
    expect(METER_CATALOG[MeterReadingId.TxFwdWatts].colorToken).toBe('power');
  });

  it('TxSwr defaults to a digital widget kind', () => {
    expect(METER_CATALOG[MeterReadingId.TxSwr].defaultKind).toBe('digital');
  });

  it('TxFwdWatts defaults to a dial widget kind', () => {
    expect(METER_CATALOG[MeterReadingId.TxFwdWatts].defaultKind).toBe('dial');
  });

  it('library filter "rx" includes signal/adc/agc and excludes tx', () => {
    for (const def of METER_READINGS) {
      const isRx =
        def.category === 'rx-signal' ||
        def.category === 'rx-adc' ||
        def.category === 'rx-agc';
      expect(meterMatchesFilter(def, 'rx')).toBe(isRx);
    }
  });

  it('library filter "tx" excludes RX entries', () => {
    for (const def of METER_READINGS) {
      const isTx = def.category.startsWith('tx-');
      expect(meterMatchesFilter(def, 'tx')).toBe(isTx);
    }
  });

  it('library filter "all" includes everything', () => {
    for (const def of METER_READINGS) {
      expect(meterMatchesFilter(def, 'all')).toBe(true);
    }
  });

  it('METER_FILTERS contains the six expected chips in order', () => {
    expect([...METER_FILTERS]).toEqual([
      'all',
      'rx',
      'tx',
      'power',
      'stage',
      'agc',
    ]);
  });

  it('warn/danger thresholds are ordered (warn < danger) when both present', () => {
    for (const def of METER_READINGS) {
      if (def.warnAt !== undefined && def.dangerAt !== undefined) {
        expect(
          def.warnAt < def.dangerAt,
          `${def.id} warn>=danger`,
        ).toBe(true);
      }
    }
  });
});
