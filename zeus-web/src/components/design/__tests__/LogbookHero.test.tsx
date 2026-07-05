// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Licensed under the GNU General Public License v2.0-or-later. See the LICENSE
// file at the repository root for the full text.

import { createElement } from 'react';
import { describe, expect, it } from 'vitest';
import type { LogEntry } from '../../../api/log';
import { act, render } from '../../meters/__tests__/harness';
import { LogbookHero } from '../LogbookHero';

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
    dxcc: 291,
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

describe('LogbookHero', () => {
  it('toggles between the globe and DX radar views', () => {
    const entries = [
      entry({ id: '1', callsign: 'K2L', grid: 'FN31', dxcc: 291 }),
      entry({ id: '2', callsign: 'VE7LGP', grid: 'CN89', dxcc: 1 }),
    ];
    const { container, unmount } = render(
      createElement(LogbookHero, { entries, totalCount: 31 }),
    );

    expect(container.textContent).toContain('Worked The World');
    expect(container.textContent).toContain('31');
    expect(container.querySelector('.zeus-globe')).not.toBeNull();

    const radarButton = Array.from(container.querySelectorAll('button'))
      .find((button) => button.textContent === 'DX Radar');
    expect(radarButton).toBeDefined();

    act(() => {
      radarButton?.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    });

    expect(radarButton?.getAttribute('aria-pressed')).toBe('true');
    expect(container.querySelector('.logbook-dx-radar')).not.toBeNull();
    expect(container.querySelector('.zeus-globe')).toBeNull();

    unmount();
  });
});
