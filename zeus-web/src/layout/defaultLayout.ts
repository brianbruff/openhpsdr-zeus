// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Default workspace layout for the react-grid-layout (RGL) substrate. 12-col
// grid. Bumped to schemaVersion 7: removed logbook + tuning-step from the
// default tile set and relocated TX Stage Meters into the right-hand
// column. Operators can still add any of those panels back via "+ Add".
//
// ASCII sanity check (columns 0..11):
//
//   ┌───────────────────────────────────────────────┬─────────────┐  y=0
//   │              filter (0..8, h=2)                │    vfo      │
//   ├───────────────────────────────────────────────┤   (h=4)     │  y=2
//   │                                                ├─────────────┤
//   │                                                │   smeter    │  y=4
//   │                                                │   (h=2)     │
//   │              hero (0..8, h=18)                 ├─────────────┤  y=6
//   │                                                │     dsp     │
//   │                                                │   (h=3)     │  y=9
//   │                                                ├─────────────┤
//   │                                                │  txmeters   │
//   │                                                │   (h=6)     │  y=15
//   │                                                ├─────────────┤
//   │                                                │  azimuth    │
//   │                                                │   (h=5)     │
//   ├───────────────────────────────────────────────┤             │
//   │                  qrz (0..8, h=2)              │             │
//   └───────────────────────────────────────────────┴─────────────┘  y=20

import type { WorkspaceLayout } from './workspace';

export const DEFAULT_WORKSPACE_LAYOUT: WorkspaceLayout = {
  schemaVersion: 7,
  tiles: [
    // Stable uids (not random) for the default layout — lets a future
    // migration map "the old default 'qrz' tile" to a new layout without
    // losing operator overrides.
    { uid: 'tile-filter',   panelId: 'filter',   x: 0, y: 0,  w: 9, h: 2 },
    { uid: 'tile-hero',     panelId: 'hero',     x: 0, y: 2,  w: 9, h: 16 },
    { uid: 'tile-qrz',      panelId: 'qrz',      x: 0, y: 18, w: 9, h: 2 },
    { uid: 'tile-vfo',      panelId: 'vfo',      x: 9, y: 0,  w: 3, h: 4 },
    { uid: 'tile-smeter',   panelId: 'smeter',   x: 9, y: 4,  w: 3, h: 2 },
    { uid: 'tile-dsp',      panelId: 'dsp',      x: 9, y: 6,  w: 3, h: 3 },
    { uid: 'tile-txmeters', panelId: 'txmeters', x: 9, y: 9,  w: 3, h: 6 },
    { uid: 'tile-azimuth',  panelId: 'azimuth',  x: 9, y: 15, w: 3, h: 5 },
  ],
};
