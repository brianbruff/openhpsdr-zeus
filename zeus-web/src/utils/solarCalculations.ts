// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// Solar position and terminator calculations for greyline/day-night overlays.
// Pure TypeScript implementation — no external dependencies. Algorithms based
// on standard astronomical formulae (Jean Meeus, Astronomical Algorithms).
// Originally implemented in Log4YM by the same author (EI6LF), ported here
// to keep Zeus self-contained.

const DEG_TO_RAD = Math.PI / 180;
const RAD_TO_DEG = 180 / Math.PI;

// Julian Date helpers
function toJulianDate(date: Date): number {
  const time = date.getTime();
  return time / 86400000 + 2440587.5;
}

// Solar position calculation using simplified VSOP87 / low-precision formulae
// Accuracy: ~0.01° in position, sufficient for greyline visualization
export function getSunPosition(date: Date): { lat: number; lon: number } {
  const jd = toJulianDate(date);
  const n = jd - 2451545.0; // days since J2000.0

  // Mean longitude of the Sun (deg)
  const L = (280.460 + 0.9856474 * n) % 360;

  // Mean anomaly (deg)
  const g = ((357.528 + 0.9856003 * n) % 360) * DEG_TO_RAD;

  // Ecliptic longitude (deg)
  const lambda = (L + 1.915 * Math.sin(g) + 0.020 * Math.sin(2 * g)) % 360;

  // Obliquity of ecliptic (deg)
  const epsilon = (23.439 - 0.0000004 * n) * DEG_TO_RAD;

  // Right ascension (deg)
  const ra = Math.atan2(Math.cos(epsilon) * Math.sin(lambda * DEG_TO_RAD),
                        Math.cos(lambda * DEG_TO_RAD)) * RAD_TO_DEG;

  // Declination (deg)
  const dec = Math.asin(Math.sin(epsilon) * Math.sin(lambda * DEG_TO_RAD)) * RAD_TO_DEG;

  // Greenwich mean sidereal time (deg)
  const gmst = (280.460 + 360.9856474 * n) % 360;

  // Solar longitude (west is positive in astronomy convention)
  const lon = ((ra - gmst + 540) % 360) - 180;

  return { lat: dec, lon };
}

// Compute terminator line at a specific solar altitude
// altitudeDeg: 0 for sunset/sunrise line, -6 for civil twilight, etc.
// stepDeg: longitude step size (smaller = smoother curve, default 2°)
export function computeTerminatorLine(
  date: Date,
  altitudeDeg: number = 0,
  stepDeg: number = 2,
): Array<{ lat: number; lon: number }> {
  const sun = getSunPosition(date);
  const sunLat = sun.lat * DEG_TO_RAD;
  const sunLon = sun.lon * DEG_TO_RAD;

  // Zenith distance corresponding to altitude
  const zenith = (90 - altitudeDeg) * DEG_TO_RAD;

  const points: Array<{ lat: number; lon: number }> = [];

  // Generate terminator points by sweeping longitude
  for (let lon = -180; lon <= 180; lon += stepDeg) {
    const lonRad = lon * DEG_TO_RAD;

    // Spherical law of cosines to find latitude where sun is at given altitude
    // cos(zenith) = sin(sunLat) * sin(lat) + cos(sunLat) * cos(lat) * cos(lon - sunLon)
    const cosZenith = Math.cos(zenith);
    const sinSunLat = Math.sin(sunLat);
    const cosSunLat = Math.cos(sunLat);
    const cosLonDiff = Math.cos(lonRad - sunLon);

    // Solve for sin(lat)
    const a = cosSunLat * cosLonDiff;
    const discriminant = cosZenith * cosZenith - a * a + sinSunLat * sinSunLat;

    if (discriminant < 0) {
      // No solution — terminator doesn't cross this longitude (polar day/night)
      continue;
    }

    const sinLat = (cosZenith * sinSunLat - Math.sqrt(discriminant)) /
                   (1 - a * a / (1 + sinSunLat * sinSunLat));

    const lat = Math.asin(Math.max(-1, Math.min(1, sinLat))) * RAD_TO_DEG;

    // Validate latitude is reasonable
    if (Math.abs(lat) <= 90) {
      points.push({ lat, lon });
    }
  }

  return points;
}

// Get Moon position (simplified low-precision algorithm)
// Accuracy: ~0.5° in position, sufficient for visualization
export function getMoonPosition(date: Date): { lat: number; lon: number } {
  const jd = toJulianDate(date);
  const n = jd - 2451545.0;

  // Moon's mean longitude (deg)
  const L = (218.316 + 13.176396 * n) % 360;

  // Mean anomaly (deg)
  const M = ((134.963 + 13.064993 * n) % 360) * DEG_TO_RAD;

  // Mean distance from ascending node (deg)
  const F = ((93.272 + 13.229350 * n) % 360) * DEG_TO_RAD;

  // Ecliptic longitude (deg)
  const lambda = (L + 6.289 * Math.sin(M)) % 360;

  // Ecliptic latitude (deg)
  const beta = 5.128 * Math.sin(F);

  // Obliquity of ecliptic (deg)
  const epsilon = (23.439 - 0.0000004 * n) * DEG_TO_RAD;

  // Convert to equatorial coordinates
  const lambdaRad = lambda * DEG_TO_RAD;
  const betaRad = beta * DEG_TO_RAD;

  const ra = Math.atan2(
    Math.sin(lambdaRad) * Math.cos(epsilon) - Math.tan(betaRad) * Math.sin(epsilon),
    Math.cos(lambdaRad)
  ) * RAD_TO_DEG;

  const dec = Math.asin(
    Math.sin(betaRad) * Math.cos(epsilon) +
    Math.cos(betaRad) * Math.sin(epsilon) * Math.sin(lambdaRad)
  ) * RAD_TO_DEG;

  // Greenwich mean sidereal time (deg)
  const gmst = (280.460 + 360.9856474 * n) % 360;

  // Moon longitude
  const lon = ((ra - gmst + 540) % 360) - 180;

  return { lat: dec, lon };
}
