// SPDX-License-Identifier: GPL-2.0-or-later
//
// Shared singleton ref to the currently-mounted Leaflet map instance.
//
// Both layout paths (classic CSS-grid in App.tsx, RGL tile in HeroPanel.tsx)
// render <LeafletWorldMap> via the same component but in different React
// trees. Keyboard shortcuts and other global interactions need to drive the
// active map without prop-drilling through every intermediate component.
// This module exposes a tiny module-level ref + getter that any consumer can
// read. The component populates it via mapRef={ACTIVE_MAP_REF} on mount and
// mapRef nulls itself on unmount, so reads are always live.

import type { MutableRefObject } from 'react';
import type L from 'leaflet';

export const ACTIVE_MAP_REF: MutableRefObject<L.Map | null> = {
  current: null,
};
