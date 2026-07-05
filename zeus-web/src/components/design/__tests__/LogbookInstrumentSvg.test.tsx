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
import { render } from '../../meters/__tests__/harness';
import { LogbookRingGauge } from '../LogbookInstrumentSvg';

describe('LogbookRingGauge', () => {
  it('renders SVG slices and keeps the center percent label visible', () => {
    const { container, unmount } = render(
      createElement(LogbookRingGauge, {
        ariaLabel: 'Mode mix',
        slices: [
          { key: 'FT8', label: 'FT8', value: 97, pct: 97 },
          { key: 'USB', label: 'USB', value: 3, pct: 3 },
        ],
        center: createElement('span', null, '97%'),
      }),
    );

    const slices = container.querySelectorAll('path[data-slice]');
    expect(slices).toHaveLength(2);
    expect(slices[0]?.getAttribute('data-slice')).toBe('FT8');
    expect(slices[0]?.getAttribute('data-pct')).toBe('97');
    expect(container.textContent).toContain('97%');

    unmount();
  });
});
