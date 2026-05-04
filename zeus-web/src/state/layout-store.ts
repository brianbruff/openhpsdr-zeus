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
  newTileUid,
  placeTileInGrid,
  type WorkspaceLayout,
  type WorkspaceTile,
} from '../layout/workspace';
import { useLayoutCollectionStore } from './layout-collection-store';

/**
 * Layout store that forwards operations to the active layout in the
 * layout collection. Maintains API compatibility with existing components.
 */

interface LayoutState {
  /** The active workspace layout. Never null — falls back to default. */
  workspace: WorkspaceLayout;
  /** True after loadFromServer() has run (success or 404 / network error). */
  isLoaded: boolean;
  loadFromServer: () => Promise<void>;
  /** Replace the entire workspace blob. Triggers a debounced server PUT. */
  setWorkspace: (next: WorkspaceLayout) => void;
  /** Forget the saved layout — back to default and DELETE the server copy. */
  resetLayout: () => void;
  /** Debounced PUT to /api/ui/layout-collection. */
  syncToServer: () => void;
  /** sendBeacon-with-fetch-fallback for page-unload persistence. */
  syncToServerBeforeUnload: () => void;
  // Tile mutators
  addTile: (panelId: string, opts?: { instanceConfig?: unknown }) => string;
  removeTile: (uid: string) => void;
  updateTilePlacement: (
    uid: string,
    layout: Pick<WorkspaceTile, 'x' | 'y' | 'w' | 'h'>,
  ) => void;
  updateTileInstanceConfig: (uid: string, instanceConfig: unknown) => void;
  // Add-Panel modal visibility
  addPanelOpen: boolean;
  setAddPanelOpen: (open: boolean) => void;
}

export const useLayoutStore = create<LayoutState>((set) => ({
  get workspace() {
    return useLayoutCollectionStore.getState().getActiveWorkspace();
  },
  get isLoaded() {
    return useLayoutCollectionStore.getState().isLoaded;
  },
  addPanelOpen: false,
  setAddPanelOpen: (open) => set({ addPanelOpen: open }),

  loadFromServer: async () => {
    await useLayoutCollectionStore.getState().loadFromServer();
  },

  setWorkspace: (next) => {
    useLayoutCollectionStore.getState().updateActiveWorkspace(next);
  },

  resetLayout: () => {
    useLayoutCollectionStore.getState().resetActiveLayout();
  },

  syncToServer: () => {
    useLayoutCollectionStore.getState().syncToServer();
  },

  syncToServerBeforeUnload: () => {
    useLayoutCollectionStore.getState().syncToServerBeforeUnload();
  },

  addTile: (panelId, opts) => {
    const collection = useLayoutCollectionStore.getState();
    const workspace = collection.getActiveWorkspace();
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
    collection.updateActiveWorkspace(next);
    return uid;
  },

  removeTile: (uid) => {
    const collection = useLayoutCollectionStore.getState();
    const workspace = collection.getActiveWorkspace();
    if (!workspace.tiles.some((t) => t.uid === uid)) return;
    const next: WorkspaceLayout = {
      ...workspace,
      tiles: workspace.tiles.filter((t) => t.uid !== uid),
    };
    collection.updateActiveWorkspace(next);
  },

  updateTilePlacement: (uid, layout) => {
    const collection = useLayoutCollectionStore.getState();
    const workspace = collection.getActiveWorkspace();
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
    collection.updateActiveWorkspace({ ...workspace, tiles });
  },

  updateTileInstanceConfig: (uid, instanceConfig) => {
    const collection = useLayoutCollectionStore.getState();
    const workspace = collection.getActiveWorkspace();
    if (!workspace.tiles.some((t) => t.uid === uid)) return;
    const tiles = workspace.tiles.map((t) =>
      t.uid === uid ? { ...t, instanceConfig } : t
    );
    collection.updateActiveWorkspace({ ...workspace, tiles });
  },
}));

