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

import { create } from 'zustand';

// Opaque flexlayout-react JSON blob — we don't strongly-type the tree.
type FlexLayoutJson = Record<string, unknown>;

interface LayoutState {
  layout: FlexLayoutJson | null;
  isLoaded: boolean;
  loadFromServer: () => Promise<void>;
  setLayout: (json: FlexLayoutJson) => void;
  resetLayout: () => void;
  syncToServer: () => void;
  syncToServerBeforeUnload: () => void;
}

let debounceTimer: ReturnType<typeof setTimeout> | null = null;

export const useLayoutStore = create<LayoutState>((set, get) => ({
  layout: null,
  isLoaded: false,

  loadFromServer: async () => {
    try {
      const res = await fetch('/api/ui/layout');
      if (res.status === 404) {
        set({ isLoaded: true });
        return;
      }
      if (!res.ok) {
        set({ isLoaded: true });
        return;
      }
      const dto = (await res.json()) as { layoutJson: string };
      set({ layout: JSON.parse(dto.layoutJson) as FlexLayoutJson, isLoaded: true });
    } catch {
      set({ isLoaded: true });
    }
  },

  setLayout: (json) => {
    set({ layout: json });
    get().syncToServer();
  },

  resetLayout: () => {
    set({ layout: null });
    fetch('/api/ui/layout', { method: 'DELETE' }).catch(() => {});
  },

  syncToServer: () => {
    if (debounceTimer) clearTimeout(debounceTimer);
    debounceTimer = setTimeout(() => {
      const { layout } = get();
      if (!layout) return;
      void fetch('/api/ui/layout', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ layoutJson: JSON.stringify(layout) }),
      });
    }, 1000);
  },

  syncToServerBeforeUnload: () => {
    const { layout } = get();
    if (!layout) return;
    const body = JSON.stringify({ layoutJson: JSON.stringify(layout) });
    const blob = new Blob([body], { type: 'application/json' });
    if (!navigator.sendBeacon('/api/ui/layout-beacon', blob)) {
      void fetch('/api/ui/layout', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body,
        keepalive: true,
      });
    }
  },
}));
