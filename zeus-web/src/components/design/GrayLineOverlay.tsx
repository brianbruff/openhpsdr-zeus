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
import { computeTerminatorLine } from '../../utils/solarCalculations';
import { triplicatePath } from '../../utils/geoUtils';

// Greyline overlay for Leaflet world map. Renders the solar terminator
// (sunrise/sunset line) plus an "enhanced DX zone" polygon between ±5° solar
// altitude, optionally with civil/nautical/astronomical twilight lines.
// Originally from Log4YM, adapted for Zeus's direct L.Map usage.

type GrayLineOverlayProps = {
  map: L.Map | null;
  currentTime: Date;
  /** Show civil/nautical/astronomical twilight lines (default: false for simplicity) */
  showTwilightLines?: boolean;
};

// Zeus amber palette — single hue at #FFA028 per CLAUDE.md
const COLOR_AMBER = '#FFA028';
const COLOR_DX_ZONE = 'rgba(255, 160, 40, 0.2)'; // Amber at low opacity

export function GrayLineOverlay({ map, currentTime, showTwilightLines = false }: GrayLineOverlayProps) {
  const layerGroupRef = useRef<L.LayerGroup | null>(null);

  useEffect(() => {
    if (!map) return;

    // Create layer group on first mount
    if (!layerGroupRef.current) {
      layerGroupRef.current = L.layerGroup().addTo(map);
    }

    const layer = layerGroupRef.current;
    layer.clearLayers();

    // Compute terminator (solar altitude = 0°)
    const terminatorPoints = computeTerminatorLine(currentTime, 0, 2);
    if (terminatorPoints.length === 0) return;

    // Enhanced DX zone: polygon between +5° and -5° solar altitude
    // (the band where low-band DX is most active)
    const upperBoundary = computeTerminatorLine(currentTime, 5, 2);
    const lowerBoundary = computeTerminatorLine(currentTime, -5, 2);

    if (upperBoundary.length > 0 && lowerBoundary.length > 0) {
      // Build closed polygon: upper boundary + reverse of lower boundary
      const dxZonePoints = [...upperBoundary, ...lowerBoundary.reverse()];
      const triplicatedZones = triplicatePath(dxZonePoints);

      for (const path of triplicatedZones) {
        const coords: [number, number][] = path.map((p) => [p.lat, p.lon]);
        L.polygon(coords, {
          color: 'transparent',
          fillColor: COLOR_DX_ZONE,
          fillOpacity: 1, // Opacity baked into color
          weight: 0,
        }).addTo(layer);
      }
    }

    // Main terminator line (solid amber, slightly heavier than day/night version)
    const triplicatedTerminator = triplicatePath(terminatorPoints);
    for (const path of triplicatedTerminator) {
      const coords: [number, number][] = path.map((p) => [p.lat, p.lon]);
      L.polyline(coords, {
        color: COLOR_AMBER,
        weight: 3,
        opacity: 0.85,
        lineCap: 'round',
      }).addTo(layer);
    }

    // Optional twilight lines (civil -6°, nautical -12°, astronomical -18°)
    if (showTwilightLines) {
      const twilightDefs: Array<{ altitude: number; opacity: number; dash: string }> = [
        { altitude: -6, opacity: 0.5, dash: '4, 8' }, // Civil twilight
        { altitude: -12, opacity: 0.4, dash: '4, 10' }, // Nautical twilight
        { altitude: -18, opacity: 0.3, dash: '4, 12' }, // Astronomical twilight
      ];

      for (const { altitude, opacity, dash } of twilightDefs) {
        const twilightPoints = computeTerminatorLine(currentTime, altitude, 2);
        if (twilightPoints.length === 0) continue;

        const triplicatedTwilight = triplicatePath(twilightPoints);
        for (const path of triplicatedTwilight) {
          const coords: [number, number][] = path.map((p) => [p.lat, p.lon]);
          L.polyline(coords, {
            color: COLOR_AMBER,
            weight: 1.5,
            opacity,
            dashArray: dash,
            lineCap: 'round',
          }).addTo(layer);
        }
      }
    }

    return () => {
      if (layerGroupRef.current) {
        layerGroupRef.current.clearLayers();
      }
    };
  }, [map, currentTime, showTwilightLines]);

  return null;
}
