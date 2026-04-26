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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

import { create } from 'zustand';

export type LayoutMode = 'default' | 'flex';

const LAYOUT_PREF_KEY = 'zeus.layout.mode';

function getStoredLayoutMode(): LayoutMode {
  try {
    // Check for legacy query string ?layout=flex for backward compatibility
    if (typeof window !== 'undefined') {
      const queryLayout = new URLSearchParams(window.location.search).get('layout');
      if (queryLayout === 'flex') {
        // Migrate to localStorage preference and remove query param
        window.localStorage.setItem(LAYOUT_PREF_KEY, 'flex');
        // Remove query string by updating history without page reload
        const url = new URL(window.location.href);
        url.searchParams.delete('layout');
        window.history.replaceState({}, '', url.toString());
        return 'flex';
      }
    }

    // Check localStorage for stored preference
    const stored = window.localStorage.getItem(LAYOUT_PREF_KEY);
    if (stored === 'flex') return 'flex';
    return 'default';
  } catch {
    return 'default';
  }
}

function setStoredLayoutMode(mode: LayoutMode) {
  try {
    window.localStorage.setItem(LAYOUT_PREF_KEY, mode);
  } catch {
    // ok
  }
}

interface LayoutPreferenceState {
  layoutMode: LayoutMode;
  setLayoutMode: (mode: LayoutMode) => void;
}

export const useLayoutPreferenceStore = create<LayoutPreferenceState>((set) => ({
  layoutMode: getStoredLayoutMode(),
  setLayoutMode: (mode) => {
    set({ layoutMode: mode });
    setStoredLayoutMode(mode);
  },
}));
