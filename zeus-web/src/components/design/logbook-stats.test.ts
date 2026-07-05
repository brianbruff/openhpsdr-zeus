// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

import { describe, expect, it } from 'vitest';
import type { LogEntry } from '../../api/log';
import {
  bandHistogram,
  modeBreakdown,
  topCountries,
  countThisMonth,
  geoPoints,
  awardStats,
  heroStats,
} from './logbook-stats';
import { countryToFlag, flagEmoji } from './logbook-flag';

function entry(over: Partial<LogEntry> & Pick<LogEntry, 'id' | 'callsign'>): LogEntry {
  return {
    qsoDateTimeUtc: '2026-05-24T12:00:00Z',
    name: null,
    frequencyMhz: 14.074,
    band: '20m',
    mode: 'FT8',
    rstSent: '-10',
    rstRcvd: '-12',
    grid: 'FN31',
    country: null,
    dxcc: null,
    cqZone: null,
    ituZone: null,
    state: null,
    comment: null,
    createdUtc: '2026-05-24T12:00:05Z',
    qrzLogId: null,
    qrzUploadedUtc: null,
    tags: null,
    qslSent: null,
    qslRcvd: null,
    qslSentDate: null,
    qslRcvdDate: null,
    lotwQslSentUtc: null,
    lotwQslRcvdUtc: null,
    qrzQslRcvdUtc: null,
    rig: null,
    antenna: null,
    txPowerW: null,
    ...over,
  };
}

describe('bandHistogram', () => {
  it('counts per band and orders low→high frequency', () => {
    const hist = bandHistogram([
      entry({ id: '1', callsign: 'A', band: '20m' }),
      entry({ id: '2', callsign: 'B', band: '40m' }),
      entry({ id: '3', callsign: 'C', band: '20m' }),
      entry({ id: '4', callsign: 'D', band: '160m' }),
    ]);
    expect(hist.map((b) => b.band)).toEqual(['160M', '40M', '20M']);
    expect(hist.find((b) => b.band === '20M')?.count).toBe(2);
  });

  it('ignores blank bands', () => {
    expect(bandHistogram([entry({ id: '1', callsign: 'A', band: '' })])).toEqual([]);
  });
});

describe('modeBreakdown', () => {
  it('computes share and rolls the tail into Other', () => {
    const entries = [
      ...Array.from({ length: 6 }, (_, i) => entry({ id: `f${i}`, callsign: 'A', mode: 'FT8' })),
      ...Array.from({ length: 2 }, (_, i) => entry({ id: `s${i}`, callsign: 'B', mode: 'SSB' })),
      entry({ id: 'c', callsign: 'C', mode: 'CW' }),
      entry({ id: 'r', callsign: 'D', mode: 'RTTY' }),
    ];
    const modes = modeBreakdown(entries, 3);
    expect(modes[0]).toMatchObject({ mode: 'FT8', count: 6, pct: 60 });
    expect(modes[1]).toMatchObject({ mode: 'SSB', count: 2 });
    expect(modes.at(-1)).toMatchObject({ mode: 'Other', count: 1 });
  });

  it('returns empty for no modes', () => {
    expect(modeBreakdown([])).toEqual([]);
  });
});

describe('topCountries', () => {
  it('ranks by count and caps to the limit', () => {
    const entries = [
      entry({ id: '1', callsign: 'A', country: 'United States' }),
      entry({ id: '2', callsign: 'B', country: 'United States' }),
      entry({ id: '3', callsign: 'C', country: 'Canada' }),
      entry({ id: '4', callsign: 'D', country: null }),
    ];
    const top = topCountries(entries, 5);
    expect(top).toEqual([
      { country: 'United States', count: 2 },
      { country: 'Canada', count: 1 },
    ]);
  });
});

describe('countThisMonth', () => {
  it('counts only QSOs in the same UTC calendar month', () => {
    const now = new Date('2026-05-15T00:00:00Z');
    const n = countThisMonth(
      [
        entry({ id: '1', callsign: 'A', qsoDateTimeUtc: '2026-05-01T00:00:00Z' }),
        entry({ id: '2', callsign: 'B', qsoDateTimeUtc: '2026-05-31T23:59:00Z' }),
        entry({ id: '3', callsign: 'C', qsoDateTimeUtc: '2026-04-30T23:59:00Z' }),
        entry({ id: '4', callsign: 'D', qsoDateTimeUtc: '2026-06-01T00:00:00Z' }),
      ],
      now,
    );
    expect(n).toBe(2);
  });
});

describe('geoPoints', () => {
  it('buckets co-located grids into one weighted dot', () => {
    const pts = geoPoints([
      entry({ id: '1', callsign: 'A', grid: 'FN31' }),
      entry({ id: '2', callsign: 'B', grid: 'FN31' }),
      entry({ id: '3', callsign: 'C', grid: 'JO65' }),
      entry({ id: '4', callsign: 'D', grid: null }),
    ]);
    expect(pts).toHaveLength(2);
    const total = pts.reduce((s, p) => s + p.count, 0);
    expect(total).toBe(3);
  });
});

describe('awardStats', () => {
  it('counts worked and confirmed award progress', () => {
    const stats = awardStats([
      entry({ id: '1', callsign: 'A', dxcc: 291, state: 'CT', grid: 'FN31pr', lotwQslRcvdUtc: '2026-06-01T00:00:00Z' }),
      entry({ id: '2', callsign: 'B', dxcc: 291, state: 'CT', grid: 'FN31aa' }),
      entry({ id: '3', callsign: 'C', dxcc: 110, state: 'CA', grid: 'CM87', qslRcvd: 'Y' }),
      entry({ id: '4', callsign: 'D', dxcc: 223, state: 'DX', grid: 'IO63', qrzQslRcvdUtc: '2026-06-02T00:00:00Z' }),
    ]);

    expect(stats).toEqual({
      dxccWorked: 3,
      dxccConfirmed: 3,
      statesWorked: 2,
      gridsWorked: 3,
    });
  });
});

describe('heroStats', () => {
  it('uses the hydrated total and de-duplicates grids, DXCC entities and bands', () => {
    const stats = heroStats(
      [
        entry({ id: '1', callsign: 'A', band: '20m', grid: 'FN31', dxcc: 291 }),
        entry({ id: '2', callsign: 'B', band: '40m', grid: 'FN31aa', dxcc: 291 }),
        entry({ id: '3', callsign: 'C', band: '20m', grid: 'CM87', dxcc: 110 }),
      ],
      1248,
    );

    expect(stats).toEqual({
      qsos: 1248,
      grids: 2,
      dxcc: 2,
      bands: 2,
    });
  });
});

describe('flags', () => {
  it('maps ISO codes to regional-indicator flags', () => {
    expect(flagEmoji('US')).toBe('🇺🇸');
    expect(flagEmoji('??')).toBe('🌐');
  });

  it('resolves country names (with variants) and falls back to a globe', () => {
    expect(countryToFlag('United States')).toBe('🇺🇸');
    expect(countryToFlag('england')).toBe('🇬🇧');
    expect(countryToFlag('Atlantis')).toBe('🌐');
    expect(countryToFlag(null)).toBe('🌐');
  });
});
