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
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF), with contributions from (DH1KLM); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { create } from 'zustand';

// Opaque flexlayout-react JSON blob — we don't strongly-type the tree.
type FlexLayoutJson = Record<string, unknown>;

// Bump whenever DEFAULT_LAYOUT gains/loses a panel, or when weights
// change in a way existing users should pick up on next load. Stored in
// localStorage; on mismatch we discard the server-side layout and fall
// through to DEFAULT_LAYOUT.
//   v2 (2026-04-24): added 'filter' bandwidth-filter panel above hero.
//   v3 (2026-04-24): shrunk filter tabset weight for tighter default.
const LAYOUT_SCHEMA_VERSION = 3;
const VERSION_KEY = 'zeus.layout.schemaVersion';

function getStoredVersion(): number {
  try {
    const v = window.localStorage.getItem(VERSION_KEY);
    return v ? parseInt(v, 10) : 0;
  } catch {
    return 0;
  }
}

function setStoredVersion(v: number) {
  try { window.localStorage.setItem(VERSION_KEY, String(v)); } catch { /* ok */ }
}

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
    // Stale schema: discard any server-side layout so DEFAULT_LAYOUT wins.
    if (getStoredVersion() !== LAYOUT_SCHEMA_VERSION) {
      await fetch('/api/ui/layout', { method: 'DELETE' }).catch(() => {});
      setStoredVersion(LAYOUT_SCHEMA_VERSION);
      set({ layout: null, isLoaded: true });
      return;
    }
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
