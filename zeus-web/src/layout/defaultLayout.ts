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
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// Default flexlayout-react model that replicates the current CSS grid:
//   left column (75%): hero spectrum (70%) + bottom row [QRZ + logbook + tx meters] (30%)
//   right column (25%): VFO + SMeter + DSP + Azimuth + Step (stacked)
//
// Phase 1 — operators who never drag panels see the same screen as today.
// Weights are approximate; flexlayout distributes remaining space proportionally.
export const DEFAULT_LAYOUT = {
  global: {
    tabEnableClose: true,
    tabSetMinHeight: 60,
    tabSetMinWidth: 80,
    tabSetTabStripHeight: 28,
  },
  borders: [],
  layout: {
    type: 'row',
    children: [
      {
        // Left column: filter ribbon on top, hero in the middle, bottom row below
        type: 'row',
        weight: 75,
        children: [
          {
            type: 'tabset',
            weight: 14,
            children: [
              { type: 'tab', name: 'Bandwidth Filter', component: 'filter' },
            ],
          },
          {
            type: 'tabset',
            weight: 58,
            children: [
              { type: 'tab', name: 'Panadapter · World Map', component: 'hero' },
            ],
          },
          {
            // Bottom row: QRZ lookup + logbook + TX meters side by side
            type: 'row',
            weight: 28,
            children: [
              {
                type: 'tabset',
                weight: 30,
                children: [
                  { type: 'tab', name: 'QRZ Lookup', component: 'qrz' },
                ],
              },
              {
                type: 'tabset',
                weight: 40,
                children: [
                  { type: 'tab', name: 'Logbook', component: 'logbook' },
                ],
              },
              {
                type: 'tabset',
                weight: 30,
                children: [
                  { type: 'tab', name: 'TX Stage Meters', component: 'txmeters' },
                ],
              },
            ],
          },
        ],
      },
      {
        // Right column: side stack panels stacked vertically
        type: 'row',
        weight: 25,
        children: [
          {
            type: 'tabset',
            weight: 21,
            children: [
              { type: 'tab', name: 'Frequency · VFO', component: 'vfo' },
            ],
          },
          {
            type: 'tabset',
            weight: 15,
            children: [
              { type: 'tab', name: 'S-Meter', component: 'smeter' },
            ],
          },
          {
            type: 'tabset',
            weight: 15,
            children: [
              { type: 'tab', name: 'DSP', component: 'dsp' },
            ],
          },
          {
            type: 'tabset',
            weight: 40,
            children: [
              { type: 'tab', name: 'Azimuth Map', component: 'azimuth' },
            ],
          },
          {
            type: 'tabset',
            weight: 15,
            children: [
              { type: 'tab', name: 'Tuning Step', component: 'step' },
            ],
          },
        ],
      },
    ],
  },
} as const;
