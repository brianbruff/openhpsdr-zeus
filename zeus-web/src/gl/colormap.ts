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

// Waterfall colour palettes. All three are resampled into 256-entry RGBA
// LUTs by linear interpolation between anchor stops; the GL sampler does the
// rest at draw time.
//
// - Blue: classic SDR palette (black → dark blue → cyan → green → yellow →
//   red → white). Keeps the noise-floor visually quiet and highlights peaks crisp.
// - Inferno: matplotlib-style dark-to-red-to-yellow. Perceptually uniform,
//   high dynamic range, matches the Thetis default.
// - Viridis: matplotlib perceptual default (purple → teal → green → yellow).
//   Colour-blind-safe and preserves ordering under greyscale reduction.
//
// Anchor colours for inferno/viridis are sampled from matplotlib's
// published RGB tables (BSD-compatible) at 5 stops each — dense enough that
// the 256-entry linear resample is visually indistinguishable from the
// continuous form for our use case.

export type ColormapId = 'blue' | 'inferno' | 'viridis';

export type ColormapSpec = {
  id: ColormapId;
  label: string;
};

export const COLORMAPS: readonly ColormapSpec[] = [
  { id: 'blue', label: 'Blue' },
  { id: 'inferno', label: 'Inferno' },
  { id: 'viridis', label: 'Viridis' },
];

type Anchor = [number, [number, number, number]];

const BLUE_ANCHORS: Anchor[] = [
  [0.0, [0, 0, 0]],
  [0.15, [0, 0, 128]],
  [0.3, [0, 0, 255]],
  [0.45, [0, 255, 255]],
  [0.6, [0, 255, 0]],
  [0.75, [255, 255, 0]],
  [0.88, [255, 0, 0]],
  [1.0, [255, 255, 255]],
];

const INFERNO_ANCHORS: Anchor[] = [
  [0.0, [0, 0, 4]],
  [0.2, [40, 11, 84]],
  [0.4, [101, 21, 110]],
  [0.6, [190, 55, 82]],
  [0.8, [236, 121, 35]],
  [0.95, [252, 200, 45]],
  [1.0, [252, 255, 164]],
];

const VIRIDIS_ANCHORS: Anchor[] = [
  [0.0, [68, 1, 84]],
  [0.25, [59, 82, 139]],
  [0.5, [33, 144, 141]],
  [0.75, [94, 201, 98]],
  [1.0, [253, 231, 37]],
];

function sampleAnchors(anchors: Anchor[], t: number): [number, number, number] {
  let lo = anchors[0]!;
  let hi = anchors[anchors.length - 1]!;
  for (let k = 0; k < anchors.length - 1; k++) {
    const a = anchors[k]!;
    const b = anchors[k + 1]!;
    if (t >= a[0] && t <= b[0]) {
      lo = a;
      hi = b;
      break;
    }
  }
  const span = hi[0] - lo[0];
  const f = span > 0 ? (t - lo[0]) / span : 0;
  return [
    Math.round(lo[1][0] + (hi[1][0] - lo[1][0]) * f),
    Math.round(lo[1][1] + (hi[1][1] - lo[1][1]) * f),
    Math.round(lo[1][2] + (hi[1][2] - lo[1][2]) * f),
  ];
}

function buildLut(anchors: Anchor[]): Uint8Array {
  const buf = new Uint8Array(256 * 4);
  for (let i = 0; i < 256; i++) {
    const [r, g, b] = sampleAnchors(anchors, i / 255);
    buf[i * 4 + 0] = r;
    buf[i * 4 + 1] = g;
    buf[i * 4 + 2] = b;
    buf[i * 4 + 3] = 255;
  }
  return buf;
}

// Lazy-cached so palette swaps at runtime don't re-interpolate each time.
const CACHE: Partial<Record<ColormapId, Uint8Array>> = {};

export function lutFor(id: ColormapId): Uint8Array {
  const cached = CACHE[id];
  if (cached) return cached;
  const anchors =
    id === 'inferno'
      ? INFERNO_ANCHORS
      : id === 'viridis'
        ? VIRIDIS_ANCHORS
        : BLUE_ANCHORS;
  const lut = buildLut(anchors);
  CACHE[id] = lut;
  return lut;
}
