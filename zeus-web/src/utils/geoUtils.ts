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

// Geo utilities for handling antimeridian wraparound in map overlays.
// Originally from Log4YM, ported to Zeus for greyline/day-night rendering.

/**
 * Unwrap longitude discontinuities by detecting large jumps and offsetting
 * subsequent values. This ensures polylines render continuously when they
 * cross the ±180° antimeridian.
 *
 * Example: [-170, 170] becomes [-170, -190] so Leaflet draws a short arc
 * eastward instead of wrapping the long way around the world.
 */
export function unwrapLongitudes(lons: number[]): number[] {
  if (lons.length === 0) return [];

  const unwrapped = [lons[0]!];
  let offset = 0;

  for (let i = 1; i < lons.length; i++) {
    const prev = lons[i - 1]!;
    const curr = lons[i]!;
    const diff = curr - prev;

    // If longitude jumps by more than 180°, we've crossed the antimeridian
    if (diff > 180) {
      offset -= 360;
    } else if (diff < -180) {
      offset += 360;
    }

    unwrapped.push(curr + offset);
  }

  return unwrapped;
}

/**
 * Generate three copies of a polyline at -360°, 0°, and +360° longitude
 * offsets. This ensures the overlay remains continuous when the map is panned
 * across the world boundary. Leaflet automatically clips to the visible
 * viewport, so only the copy(s) within view are rendered.
 */
export function triplicatePath<T extends { lon: number }>(
  points: T[],
): Array<Array<{ lat: number; lon: number }>> {
  if (points.length === 0) return [];

  const paths: Array<Array<{ lat: number; lon: number }>> = [];

  for (const offset of [-360, 0, 360]) {
    paths.push(
      points.map((p) => ({
        lat: 'lat' in p ? (p.lat as number) : 0,
        lon: p.lon + offset,
      })),
    );
  }

  return paths;
}
