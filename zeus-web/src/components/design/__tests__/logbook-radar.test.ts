// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Licensed under the GNU General Public License v2.0-or-later. See the LICENSE
// file at the repository root for the full text.

import { describe, expect, it } from 'vitest';
import { KM_TO_MILES, projectRadarPoint } from '../logbook-radar';

describe('projectRadarPoint', () => {
  it('projects bearing north to the top of the radar', () => {
    const point = projectRadarPoint(0, 1000 / KM_TO_MILES, 100, 1000);

    expect(point.x).toBeCloseTo(0, 5);
    expect(point.y).toBeCloseTo(-100, 5);
    expect(point.miles).toBeCloseTo(1000, 5);
    expect(point.clipped).toBe(false);
  });

  it('projects bearing east to the right and clips over-range contacts', () => {
    const east = projectRadarPoint(90, 500 / KM_TO_MILES, 100, 1000);
    const south = projectRadarPoint(180, 2000 / KM_TO_MILES, 100, 1000);

    expect(east.x).toBeCloseTo(50, 5);
    expect(east.y).toBeCloseTo(0, 5);
    expect(south.x).toBeCloseTo(0, 5);
    expect(south.y).toBeCloseTo(100, 5);
    expect(south.clipped).toBe(true);
  });
});
