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

import { useEffect, useRef } from 'react';
import L from 'leaflet';
import { computeTerminatorLine, getSunPosition, getMoonPosition } from '../../utils/solarCalculations';
import { triplicatePath } from '../../utils/geoUtils';

// Day/Night overlay for Leaflet world map. Renders a translucent polygon over
// the night side of Earth, with an optional dashed line at the terminator
// (sunrise/sunset line). Originally from Log4YM, adapted for Zeus's direct
// L.Map usage (no MapContainer / useMap hook).

type DayNightOverlayProps = {
  map: L.Map | null;
  currentTime: Date;
  /** Show sun/moon marker icons (default: false to match Zeus minimal aesthetic) */
  showMarkers?: boolean;
};

// Zeus amber palette — single hue at #FFA028 per CLAUDE.md
const COLOR_AMBER = '#FFA028';
const COLOR_NIGHT_FILL = 'rgba(0, 20, 40, 0.4)'; // Dark blue-grey, low opacity
const COLOR_TERMINATOR = COLOR_AMBER; // Amber dashed line

export function DayNightOverlay({ map, currentTime, showMarkers = false }: DayNightOverlayProps) {
  const layerGroupRef = useRef<L.LayerGroup | null>(null);

  useEffect(() => {
    if (!map) return;

    // Create layer group on first mount
    if (!layerGroupRef.current) {
      layerGroupRef.current = L.layerGroup().addTo(map);
    }

    const layer = layerGroupRef.current;
    layer.clearLayers();

    // Compute terminator line (solar altitude = 0°)
    const terminatorPoints = computeTerminatorLine(currentTime, 0, 2);
    if (terminatorPoints.length === 0) return; // Polar day/night edge case

    // Build night-side polygon by extending terminator to poles
    // We create a closed polygon that covers the dark half of Earth
    const sun = getSunPosition(currentTime);
    const isNorthernWinter = sun.lat < 0;

    // Night polygon: terminator + detour to the pole opposite the sun
    const nightPolygonPoints = [...terminatorPoints];

    // Close the polygon via the appropriate pole
    if (isNorthernWinter) {
      // Sun is in southern hemisphere, so night covers north pole
      nightPolygonPoints.push({ lat: 90, lon: 180 });
      nightPolygonPoints.push({ lat: 90, lon: -180 });
    } else {
      // Sun is in northern hemisphere, so night covers south pole
      nightPolygonPoints.push({ lat: -90, lon: 180 });
      nightPolygonPoints.push({ lat: -90, lon: -180 });
    }

    // Render three copies at -360°, 0°, +360° for continuous panning
    const triplicatedPolygons = triplicatePath(nightPolygonPoints);
    for (const path of triplicatedPolygons) {
      const coords: [number, number][] = path.map((p) => [p.lat, p.lon]);
      L.polygon(coords, {
        color: 'transparent', // No stroke to avoid antimeridian seam artifacts
        fillColor: COLOR_NIGHT_FILL,
        fillOpacity: 1, // Opacity baked into color
        weight: 0,
      }).addTo(layer);
    }

    // Terminator line (dashed amber)
    const triplicatedTerminator = triplicatePath(terminatorPoints);
    for (const path of triplicatedTerminator) {
      const coords: [number, number][] = path.map((p) => [p.lat, p.lon]);
      L.polyline(coords, {
        color: COLOR_TERMINATOR,
        weight: 2,
        opacity: 0.7,
        dashArray: '8, 12',
        lineCap: 'round',
      }).addTo(layer);
    }

    // Optional sun/moon markers (off by default)
    if (showMarkers) {
      const moon = getMoonPosition(currentTime);

      // Sun marker (amber, high opacity)
      L.circleMarker([sun.lat, sun.lon], {
        radius: 8,
        color: COLOR_AMBER,
        fillColor: COLOR_AMBER,
        fillOpacity: 0.9,
        weight: 2,
      })
        .bindTooltip('☀️ Sun', { direction: 'top', className: 'lf-tt' })
        .addTo(layer);

      // Moon marker (muted grey)
      L.circleMarker([moon.lat, moon.lon], {
        radius: 6,
        color: '#a5b4c8',
        fillColor: '#a5b4c8',
        fillOpacity: 0.7,
        weight: 1.5,
      })
        .bindTooltip('🌙 Moon', { direction: 'top', className: 'lf-tt' })
        .addTo(layer);
    }

    // Cleanup function removes layer group on unmount
    return () => {
      if (layerGroupRef.current) {
        layerGroupRef.current.clearLayers();
      }
    };
  }, [map, currentTime, showMarkers]);

  // Effect-only component — no DOM of its own
  return null;
}
