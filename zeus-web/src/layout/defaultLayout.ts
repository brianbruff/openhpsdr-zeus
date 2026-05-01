// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Default workspace layout for the react-grid-layout (RGL) substrate. 12-col
// grid; positions chosen to approximate the previous flexlayout-react default
// shape so operators see the same panel set in roughly the same places after
// the substrate swap (LAYOUT_SCHEMA_VERSION 5→6 wipes the saved layout JSON
// on first load).
//
// ASCII sanity check (columns 0..11):
//
//   ┌───────────────────────────────────────────────┬─────────────┐  y=0
//   │              filter (0..8, h=2)                │             │
//   ├───────────────────────────────────────────────┤    vfo      │  y=2
//   │                                                │  (h=4)      │
//   │                                                ├─────────────┤  y=4
//   │                                                │   smeter    │
//   │              hero (0..8, h=12)                 │   (h=2)     │  y=6
//   │                                                ├─────────────┤
//   │                                                │     dsp     │
//   │                                                │   (h=3)     │  y=9
//   │                                                ├─────────────┤
//   │                                                │             │
//   │                                                │  azimuth    │
//   │                                                │   (h=8)     │  y=14
//   ├──────────────┬────────────┬────────────────────┤             │
//   │     qrz      │  logbook   │   txmeters         │             │
//   │   (h=6)      │   (h=6)    │     (h=6)          │             │  y=17
//   │              │            │                    ├─────────────┤
//   │              │            │                    │   step (h=3)│
//   └──────────────┴────────────┴────────────────────┴─────────────┘  y=20

import type { WorkspaceLayout } from './workspace';

export const DEFAULT_WORKSPACE_LAYOUT: WorkspaceLayout = {
  schemaVersion: 6,
  tiles: [
    // Stable uids (not random) for the default layout — lets a future
    // migration map "the old default 'qrz' tile" to a new layout without
    // losing operator overrides.
    { uid: 'tile-filter',   panelId: 'filter',   x: 0, y: 0,  w: 9, h: 2 },
    { uid: 'tile-hero',     panelId: 'hero',     x: 0, y: 2,  w: 9, h: 12 },
    { uid: 'tile-qrz',      panelId: 'qrz',      x: 0, y: 14, w: 3, h: 6 },
    { uid: 'tile-logbook',  panelId: 'logbook',  x: 3, y: 14, w: 3, h: 6 },
    { uid: 'tile-txmeters', panelId: 'txmeters', x: 6, y: 14, w: 3, h: 6 },
    { uid: 'tile-vfo',      panelId: 'vfo',      x: 9, y: 0,  w: 3, h: 4 },
    { uid: 'tile-smeter',   panelId: 'smeter',   x: 9, y: 4,  w: 3, h: 2 },
    { uid: 'tile-dsp',      panelId: 'dsp',      x: 9, y: 6,  w: 3, h: 3 },
    { uid: 'tile-azimuth',  panelId: 'azimuth',  x: 9, y: 9,  w: 3, h: 8 },
    { uid: 'tile-step',     panelId: 'step',     x: 9, y: 17, w: 3, h: 3 },
  ],
};
