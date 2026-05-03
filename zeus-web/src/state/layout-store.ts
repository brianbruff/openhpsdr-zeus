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

import { create } from 'zustand';
import {
  EMPTY_WORKSPACE_LAYOUT,
  newTileUid,
  parseWorkspaceLayout,
  placeTileInGrid,
  type WorkspaceLayout,
  type WorkspaceTile,
} from '../layout/workspace';
import { DEFAULT_WORKSPACE_LAYOUT } from '../layout/defaultLayout';

interface LayoutState {
  /** The active workspace layout. Never null — falls back to
   *  DEFAULT_WORKSPACE_LAYOUT when the server has nothing or returns junk. */
  workspace: WorkspaceLayout;
  /** True after loadFromServer() has run (success or 404 / network error). */
  isLoaded: boolean;
  loadFromServer: () => Promise<void>;
  /** Replace the entire workspace blob. Triggers a debounced server PUT. */
  setWorkspace: (next: WorkspaceLayout) => void;
  /** Forget the saved layout — back to DEFAULT_WORKSPACE_LAYOUT and DELETE
   *  the server copy. Used by the "Reset Layout" button. */
  resetLayout: () => void;
  /** Debounced PUT to /api/ui/layout. */
  syncToServer: () => void;
  /** sendBeacon-with-fetch-fallback for page-unload persistence. */
  syncToServerBeforeUnload: () => void;
  // Tile mutators — keep workspace state shape & persistence in one place.
  /** Append a fresh tile for `panelId`. New uid is minted; placement uses
   *  defaultSpanFor(panelId) at y = max existing y+h. RGL compacts at
   *  render time. For multi-instance panels (just `meters`), the panelId
   *  may already be present — that's expected. */
  addTile: (panelId: string, opts?: { instanceConfig?: unknown }) => string;
  /** Remove the tile with the given uid. No-op if not found. */
  removeTile: (uid: string) => void;
  /** Replace a tile's grid placement (x/y/w/h). Called from RGL's
   *  onLayoutChange. */
  updateTilePlacement: (
    uid: string,
    layout: Pick<WorkspaceTile, 'x' | 'y' | 'w' | 'h'>,
  ) => void;
  /** Replace a tile's instanceConfig blob. Called from MetersPanel's
   *  setConfig path. */
  updateTileInstanceConfig: (uid: string, instanceConfig: unknown) => void;
  // Add-Panel modal visibility — lifted into the store so the trigger
  // button can live in the App.tsx control row (after AF gain) while the
  // modal itself still renders inside the workspace.
  addPanelOpen: boolean;
  setAddPanelOpen: (open: boolean) => void;
}

let debounceTimer: ReturnType<typeof setTimeout> | null = null;

export const useLayoutStore = create<LayoutState>((set, get) => ({
  workspace: DEFAULT_WORKSPACE_LAYOUT,
  isLoaded: false,
  addPanelOpen: false,
  setAddPanelOpen: (open) => set({ addPanelOpen: open }),

  loadFromServer: async () => {
    try {
      const res = await fetch('/api/ui/layout');
      if (res.status === 404 || !res.ok) {
        set({ workspace: DEFAULT_WORKSPACE_LAYOUT, isLoaded: true });
        return;
      }
      const dto = (await res.json()) as { layoutJson: string };
      let parsed: WorkspaceLayout;
      try {
        parsed = parseWorkspaceLayout(JSON.parse(dto.layoutJson));
      } catch {
        parsed = EMPTY_WORKSPACE_LAYOUT;
      }
      // parseWorkspaceLayout returns EMPTY when the saved blob is from an
      // older schema (or otherwise unparseable). Render the default in that
      // case but DO NOT delete the server copy — a different browser may
      // still be on the matching schema, and the next save here will
      // overwrite cleanly.
      const next =
        parsed.tiles.length === 0 ? DEFAULT_WORKSPACE_LAYOUT : parsed;
      set({ workspace: next, isLoaded: true });
    } catch {
      set({ workspace: DEFAULT_WORKSPACE_LAYOUT, isLoaded: true });
    }
  },

  setWorkspace: (next) => {
    set({ workspace: next });
    get().syncToServer();
  },

  resetLayout: () => {
    set({ workspace: DEFAULT_WORKSPACE_LAYOUT });
    fetch('/api/ui/layout', { method: 'DELETE' }).catch(() => {});
  },

  syncToServer: () => {
    if (debounceTimer) clearTimeout(debounceTimer);
    debounceTimer = setTimeout(() => {
      const { workspace } = get();
      void fetch('/api/ui/layout', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ layoutJson: JSON.stringify(workspace) }),
      });
    }, 1000);
  },

  syncToServerBeforeUnload: () => {
    const { workspace } = get();
    const body = JSON.stringify({ layoutJson: JSON.stringify(workspace) });
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

  addTile: (panelId, opts) => {
    const { workspace } = get();
    const placement = placeTileInGrid(panelId, workspace.tiles);
    const uid = newTileUid();
    const tile: WorkspaceTile = {
      uid,
      panelId,
      ...placement,
      ...(opts?.instanceConfig !== undefined
        ? { instanceConfig: opts.instanceConfig }
        : {}),
    };
    const next: WorkspaceLayout = {
      ...workspace,
      tiles: [...workspace.tiles, tile],
    };
    set({ workspace: next });
    get().syncToServer();
    return uid;
  },

  removeTile: (uid) => {
    const { workspace } = get();
    if (!workspace.tiles.some((t) => t.uid === uid)) return;
    const next: WorkspaceLayout = {
      ...workspace,
      tiles: workspace.tiles.filter((t) => t.uid !== uid),
    };
    set({ workspace: next });
    get().syncToServer();
  },

  updateTilePlacement: (uid, layout) => {
    const { workspace } = get();
    let changed = false;
    const tiles = workspace.tiles.map((t) => {
      if (t.uid !== uid) return t;
      if (
        t.x === layout.x &&
        t.y === layout.y &&
        t.w === layout.w &&
        t.h === layout.h
      ) {
        return t;
      }
      changed = true;
      return { ...t, ...layout };
    });
    if (!changed) return;
    set({ workspace: { ...workspace, tiles } });
    get().syncToServer();
  },

  updateTileInstanceConfig: (uid, instanceConfig) => {
    const { workspace } = get();
    if (!workspace.tiles.some((t) => t.uid === uid)) return;
    const tiles = workspace.tiles.map((t) =>
      t.uid === uid ? { ...t, instanceConfig } : t,
    );
    set({ workspace: { ...workspace, tiles } });
    get().syncToServer();
  },
}));
