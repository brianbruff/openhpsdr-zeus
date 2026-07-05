// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Render smoke + content checks for the popped-out Logbook workspace pieces:
// the station-detail card and the analytics dashboard.

import { describe, expect, it } from 'vitest';
import { createElement } from 'react';
import { render } from '../meters/__tests__/harness';
import { LogbookDetail } from './LogbookDetail';
import { LogbookDashboard } from './LogbookDashboard';
import type { LogEntry } from '../../api/log';

function entry(over: Partial<LogEntry> & Pick<LogEntry, 'id' | 'callsign'>): LogEntry {
  return {
    qsoDateTimeUtc: '2026-05-24T15:12:00Z',
    name: null,
    frequencyMhz: 14.074,
    band: '20m',
    mode: 'FT8',
    rstSent: '-07',
    rstRcvd: '-07',
    grid: 'FN31',
    country: null,
    dxcc: null,
    cqZone: null,
    ituZone: null,
    state: null,
    comment: null,
    createdUtc: '2026-05-24T15:12:05Z',
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

describe('LogbookDetail', () => {
  it('shows the empty state when no QSO is focused', () => {
    const { container, unmount } = render(createElement(LogbookDetail, { entry: null }));
    expect(container.textContent).toContain('Select a QSO');
    unmount();
  });

  it('renders the focused QSO fields and QRZ status', () => {
    const e = entry({
      id: '1',
      callsign: 'K2L',
      name: '13 Colonies Special Event',
      country: 'United States',
      state: 'South Carolina',
      grid: 'EM93',
      ituZone: 8,
      cqZone: 5,
      qrzLogId: 'X1',
      comment: 'Great signal',
    });
    const { container, unmount } = render(createElement(LogbookDetail, { entry: e }));
    const text = container.textContent ?? '';
    expect(text).toContain('K2L');
    expect(text).toContain('14.074');
    expect(text).toContain('FT8');
    expect(text).toContain('Great signal');
    expect(text).toContain('✓ QRZ');
    expect(container.querySelector('.lbd-qrz-link')?.getAttribute('href')).toContain('/db/K2L');
    unmount();
  });
});

describe('LogbookDashboard', () => {
  it('renders totals, bands, modes and countries from the entry set', () => {
    const entries = [
      entry({ id: '1', callsign: 'A', band: '20m', mode: 'FT8', country: 'United States', dxcc: 291, state: 'CT' }),
      entry({ id: '2', callsign: 'B', band: '40m', mode: 'SSB', country: 'Canada', dxcc: 1, state: 'CA', grid: 'CM87' }),
      entry({ id: '3', callsign: 'C', band: '20m', mode: 'FT8', country: 'United States', dxcc: 291, state: 'NY', grid: 'FN30' }),
    ];
    const { container, unmount } = render(
      createElement(LogbookDashboard, { entries, totalCount: 1248, fullyLoaded: true }),
    );
    const text = container.textContent ?? '';
    expect(text).toContain('1,248'); // headline total from totalCount, not the loaded slice
    expect(text).toContain('Total QSOs');
    expect(text).toContain('Bands');
    expect(text).toContain('Modes');
    expect(text).toContain('United States');
    // The dashboard now uses SVG instruments; the globe lives in the hero row.
    expect(container.querySelectorAll('path[data-slice]').length).toBeGreaterThan(0);
    expect(container.querySelectorAll('.lb-heat-cell')).toHaveLength(26 * 7);
    expect(container.querySelectorAll('path[data-award-progress]')).toHaveLength(3);
    expect(container.querySelector('.zeus-globe')).toBeNull();
    unmount();
  });

  it('shows a loading hint until the full log is hydrated', () => {
    const { container, unmount } = render(
      createElement(LogbookDashboard, { entries: [], totalCount: 5000, fullyLoaded: false }),
    );
    expect(container.textContent).toContain('Loading full log');
    unmount();
  });
});
