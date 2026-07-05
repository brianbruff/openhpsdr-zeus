// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Licensed under the GNU General Public License v2.0-or-later. See the LICENSE
// file at the repository root for the full text.

import { bearingDeg, distanceKm } from './geo';
import type { GeoPoint } from './logbook-stats';

export const KM_TO_MILES = 0.621371;
export const RADAR_MAX_MILES = 12_000;
export const RADAR_RINGS_MILES = [3_000, 6_000, 12_000] as const;

export type RadarProjectedPoint = {
  x: number;
  y: number;
  miles: number;
  clipped: boolean;
};

export type RadarWorkedPoint = RadarProjectedPoint & {
  lat: number;
  lon: number;
  count: number;
  bearing: number;
};

export function projectRadarPoint(
  bearing: number,
  rangeKm: number,
  radius: number,
  maxMiles = RADAR_MAX_MILES,
): RadarProjectedPoint {
  const miles = Math.max(0, rangeKm * KM_TO_MILES);
  const clipped = miles > maxMiles;
  const normalizedRange = Math.min(1, miles / maxMiles) * radius;
  const theta = ((bearing - 90) * Math.PI) / 180;
  return {
    x: Math.cos(theta) * normalizedRange,
    y: Math.sin(theta) * normalizedRange,
    miles,
    clipped,
  };
}

export function workedRadarPoints(
  home: { lat: number; lon: number },
  points: GeoPoint[],
  radius: number,
  maxMiles = RADAR_MAX_MILES,
): RadarWorkedPoint[] {
  return points.map((point) => {
    const bearing = bearingDeg(home.lat, home.lon, point.lat, point.lon);
    const rangeKm = distanceKm(home.lat, home.lon, point.lat, point.lon);
    return {
      ...projectRadarPoint(bearing, rangeKm, radius, maxMiles),
      lat: point.lat,
      lon: point.lon,
      count: point.count,
      bearing,
    };
  });
}
