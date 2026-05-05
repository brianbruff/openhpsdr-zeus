// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Default workspace layout for the react-grid-layout (RGL) substrate. 12-col
// grid. The right column is a stack of fixed-width tiles (vfo/smeter/tx/
// txmeters/dsp); width caps live in panels.ts via maxW so the operator can
// only resize them vertically. The left column is BANDWIDTH FILTER on top
// and the panadapter hero filling the remaining vertical space — both grow
// horizontally with the window. Combined with the responsive rowHeight in
// FlexWorkspace.tsx, the whole layout scales to fill the viewport without
// the operator having to re-tune sizes.
//
// Total height = WORKSPACE_TARGET_ROWS (24) so the default fills the
// available viewport exactly with the responsive rowHeight calculation.
//
// ASCII sanity check (columns 0..11):
//
//   ┌───────────────────────────────────────────────┬─────────────┐  y=0
//   │              filter (0..8, h=6)                │    vfo      │
//   │                                                │   (h=4)     │
//   │                                                ├─────────────┤  y=4
//   │                                                │   smeter    │
//   ├───────────────────────────────────────────────┤   (h=2)     │  y=6
//   │                                                ├─────────────┤
//   │                                                │     tx      │
//   │                                                │   (h=5)     │
//   │                                                ├─────────────┤  y=11
//   │              hero (0..8, h=18)                 │  txmeters   │
//   │                                                │   (h=6)     │
//   │                                                ├─────────────┤  y=17
//   │                                                │     dsp     │
//   │                                                │   (h=7)     │
//   └───────────────────────────────────────────────┴─────────────┘  y=24

import type { WorkspaceLayout } from './workspace';

export const DEFAULT_WORKSPACE_LAYOUT: WorkspaceLayout = {
  schemaVersion: 7,
  tiles: [
    // Stable uids (not random) for the default layout — lets a future
    // migration map "the old default 'vfo' tile" to a new layout without
    // losing operator overrides.
    { uid: 'tile-filter',   panelId: 'filter',   x: 0, y: 0,  w: 9, h: 6 },
    { uid: 'tile-hero',     panelId: 'hero',     x: 0, y: 6,  w: 9, h: 18 },
    { uid: 'tile-vfo',      panelId: 'vfo',      x: 9, y: 0,  w: 3, h: 4 },
    { uid: 'tile-smeter',   panelId: 'smeter',   x: 9, y: 4,  w: 3, h: 2 },
    { uid: 'tile-tx',       panelId: 'tx',       x: 9, y: 6,  w: 3, h: 5 },
    { uid: 'tile-txmeters', panelId: 'txmeters', x: 9, y: 11, w: 3, h: 6 },
    { uid: 'tile-dsp',      panelId: 'dsp',      x: 9, y: 17, w: 3, h: 7 },
  ],
};
