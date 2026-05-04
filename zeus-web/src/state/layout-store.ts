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
// Multi-layout client store (issue #241). One radio holds a list of named
// layouts; the operator picks the active one from the LeftLayoutBar. The
// underlying workspace tile mutators (addTile / removeTile / …) operate on
// whichever layout is active and debounce a PUT through to
// `/api/ui/layouts`.

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

export interface NamedLayout {
  id: string;
  name: string;
  /** Serialized WorkspaceLayout (parseWorkspaceLayout-ready). */
  layoutJson: string;
}

interface RadioLayoutsResponse {
  radioKey: string;
  layouts: Array<{ id: string; name: string; layoutJson: string; updatedUtc: number }>;
  activeLayoutId: string;
}

interface LayoutState {
  /** Per-radio key the current `layouts` list belongs to. "default" while
   *  no radio is connected. Empty string before the first load. */
  radioKey: string;
  /** All named layouts for `radioKey`. May be empty until the server load
   *  resolves and the Default seed lands. */
  layouts: NamedLayout[];
  /** Id of the active layout. The `workspace` field below mirrors this
   *  layout's parsed WorkspaceLayout so existing FlexWorkspace consumers
   *  don't need to re-parse on every render. */
  activeLayoutId: string;
  /** The active layout's parsed WorkspaceLayout. Always non-null — falls
   *  back to DEFAULT_WORKSPACE_LAYOUT when the server returns nothing. */
  workspace: WorkspaceLayout;
  /** True after loadFromServer() has run (success or 404 / network error). */
  isLoaded: boolean;
  // Add-Panel modal visibility — lifted into the store so the trigger
  // button can live wherever (LeftLayoutBar, FlexWorkspace) while the modal
  // itself still renders inside the workspace.
  addPanelOpen: boolean;
  setAddPanelOpen: (open: boolean) => void;

  /** Switch the radio key and reload the layouts list from the server.
   *  No-op when the key already matches and isLoaded is true. */
  loadForRadio: (radioKey: string) => Promise<void>;
  /** sendBeacon-with-fetch-fallback for page-unload persistence. */
  syncToServerBeforeUnload: () => void;

  // Layout-level mutators (LeftLayoutBar API):
  /** Create a new layout for the current radio, seeded from
   *  DEFAULT_WORKSPACE_LAYOUT, and switch to it. Returns the new id. */
  addLayout: (name: string) => string;
  /** Delete a layout. If it was active, the server promotes the first
   *  remaining layout to active; if there are zero remaining the client
   *  re-seeds Default. */
  removeLayout: (id: string) => void;
  /** Rename an existing layout. */
  renameLayout: (id: string, name: string) => void;
  /** Switch the active layout. Re-parses the layout's JSON into `workspace`
   *  and POSTs the new active id to the server. */
  setActiveLayout: (id: string) => void;
  /** Reset the active layout's tiles to DEFAULT_WORKSPACE_LAYOUT. The
   *  layout itself (id + name) is preserved. */
  resetActiveLayout: () => void;

  // Tile mutators — operate on the active layout. Persisted via the same
  // debounced PUT to /api/ui/layouts.
  /** Append a fresh tile for `panelId`. */
  addTile: (panelId: string, opts?: { instanceConfig?: unknown }) => string;
  /** Remove the tile with the given uid. */
  removeTile: (uid: string) => void;
  /** Replace a tile's grid placement (x/y/w/h). */
  updateTilePlacement: (
    uid: string,
    layout: Pick<WorkspaceTile, 'x' | 'y' | 'w' | 'h'>,
  ) => void;
  /** Replace a tile's instanceConfig blob. */
  updateTileInstanceConfig: (uid: string, instanceConfig: unknown) => void;

  // Back-compat surface (still used by SettingsMenu before #241 lands the
  // LeftLayoutBar). resetLayout calls resetActiveLayout.
  resetLayout: () => void;
}

const DEFAULT_LAYOUT_ID = 'default';
const DEFAULT_LAYOUT_NAME = 'Default';

let saveTimer: ReturnType<typeof setTimeout> | null = null;

function serializeWorkspace(ws: WorkspaceLayout): string {
  return JSON.stringify(ws);
}

function defaultSeedLayout(): NamedLayout {
  return {
    id: DEFAULT_LAYOUT_ID,
    name: DEFAULT_LAYOUT_NAME,
    layoutJson: serializeWorkspace(DEFAULT_WORKSPACE_LAYOUT),
  };
}

function parseLayoutOrDefault(json: string): WorkspaceLayout {
  try {
    const parsed = parseWorkspaceLayout(JSON.parse(json));
    return parsed.tiles.length === 0 ? DEFAULT_WORKSPACE_LAYOUT : parsed;
  } catch {
    return DEFAULT_WORKSPACE_LAYOUT;
  }
}

function findActive(layouts: NamedLayout[], id: string): NamedLayout | undefined {
  return layouts.find((l) => l.id === id);
}

export const useLayoutStore = create<LayoutState>((set, get) => ({
  radioKey: '',
  layouts: [],
  activeLayoutId: DEFAULT_LAYOUT_ID,
  workspace: DEFAULT_WORKSPACE_LAYOUT,
  isLoaded: false,
  addPanelOpen: false,
  setAddPanelOpen: (open) => set({ addPanelOpen: open }),

  loadForRadio: async (radioKey) => {
    const safeKey = radioKey || 'default';
    if (get().radioKey === safeKey && get().isLoaded) return;
    try {
      const res = await fetch(`/api/ui/layouts?radio=${encodeURIComponent(safeKey)}`);
      if (!res.ok) throw new Error(`status ${res.status}`);
      const dto = (await res.json()) as RadioLayoutsResponse;
      let layouts: NamedLayout[] = (dto.layouts ?? []).map((l) => ({
        id: l.id,
        name: l.name,
        layoutJson: l.layoutJson,
      }));
      let activeId = dto.activeLayoutId || layouts[0]?.id || DEFAULT_LAYOUT_ID;
      // Empty radio → seed a Default and persist.
      if (layouts.length === 0) {
        const seed = defaultSeedLayout();
        layouts = [seed];
        activeId = seed.id;
        void fetch('/api/ui/layouts', {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            radioKey: safeKey,
            layoutId: seed.id,
            name: seed.name,
            layoutJson: seed.layoutJson,
          }),
        });
      }
      const active = findActive(layouts, activeId) ?? layouts[0]!;
      set({
        radioKey: safeKey,
        layouts,
        activeLayoutId: active.id,
        workspace: parseLayoutOrDefault(active.layoutJson),
        isLoaded: true,
      });
    } catch {
      // Network or parse failure — render defaults so the UI still works.
      const seed = defaultSeedLayout();
      set({
        radioKey: safeKey,
        layouts: [seed],
        activeLayoutId: seed.id,
        workspace: DEFAULT_WORKSPACE_LAYOUT,
        isLoaded: true,
      });
    }
  },

  syncToServerBeforeUnload: () => {
    const { radioKey, activeLayoutId, layouts, workspace } = get();
    if (!radioKey || !activeLayoutId) return;
    const active = findActive(layouts, activeLayoutId);
    if (!active) return;
    const body = JSON.stringify({
      radioKey,
      layoutId: active.id,
      name: active.name,
      layoutJson: serializeWorkspace(workspace),
    });
    const blob = new Blob([body], { type: 'application/json' });
    if (!navigator.sendBeacon('/api/ui/layout-beacon', blob)) {
      void fetch('/api/ui/layouts', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body,
        keepalive: true,
      });
    }
  },

  addLayout: (name) => {
    const id = `layout-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
    const json = serializeWorkspace(DEFAULT_WORKSPACE_LAYOUT);
    const next: NamedLayout = { id, name: name || 'Untitled', layoutJson: json };
    const layouts = [...get().layouts, next];
    set({
      layouts,
      activeLayoutId: id,
      workspace: parseLayoutOrDefault(json),
    });
    void putNamedLayout(get().radioKey, next);
    void postActiveLayout(get().radioKey, id);
    return id;
  },

  removeLayout: (id) => {
    const { radioKey, layouts, activeLayoutId } = get();
    if (!radioKey) return;
    if (layouts.length <= 1) return; // never delete the last one
    const remaining = layouts.filter((l) => l.id !== id);
    let nextActive = activeLayoutId;
    let nextWorkspace = get().workspace;
    if (activeLayoutId === id) {
      nextActive = remaining[0]!.id;
      nextWorkspace = parseLayoutOrDefault(remaining[0]!.layoutJson);
    }
    set({ layouts: remaining, activeLayoutId: nextActive, workspace: nextWorkspace });
    void fetch(`/api/ui/layouts?radio=${encodeURIComponent(radioKey)}&id=${encodeURIComponent(id)}`, {
      method: 'DELETE',
    });
    if (activeLayoutId === id) {
      void postActiveLayout(radioKey, nextActive);
    }
  },

  renameLayout: (id, name) => {
    const layouts = get().layouts.map((l) => (l.id === id ? { ...l, name } : l));
    set({ layouts });
    const renamed = findActive(layouts, id);
    if (renamed) void putNamedLayout(get().radioKey, renamed);
  },

  setActiveLayout: (id) => {
    const { layouts, radioKey } = get();
    const target = findActive(layouts, id);
    if (!target) return;
    set({
      activeLayoutId: id,
      workspace: parseLayoutOrDefault(target.layoutJson),
    });
    void postActiveLayout(radioKey, id);
  },

  resetActiveLayout: () => {
    const { layouts, activeLayoutId, radioKey } = get();
    const active = findActive(layouts, activeLayoutId);
    if (!active) {
      set({ workspace: DEFAULT_WORKSPACE_LAYOUT });
      return;
    }
    const json = serializeWorkspace(DEFAULT_WORKSPACE_LAYOUT);
    const updated: NamedLayout = { ...active, layoutJson: json };
    const next = layouts.map((l) => (l.id === activeLayoutId ? updated : l));
    set({ layouts: next, workspace: DEFAULT_WORKSPACE_LAYOUT });
    void putNamedLayout(radioKey, updated);
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
    applyWorkspaceMutation(set, get, next);
    return uid;
  },

  removeTile: (uid) => {
    const { workspace } = get();
    if (!workspace.tiles.some((t) => t.uid === uid)) return;
    const next: WorkspaceLayout = {
      ...workspace,
      tiles: workspace.tiles.filter((t) => t.uid !== uid),
    };
    applyWorkspaceMutation(set, get, next);
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
    applyWorkspaceMutation(set, get, { ...workspace, tiles });
  },

  updateTileInstanceConfig: (uid, instanceConfig) => {
    const { workspace } = get();
    if (!workspace.tiles.some((t) => t.uid === uid)) return;
    const tiles = workspace.tiles.map((t) =>
      t.uid === uid ? { ...t, instanceConfig } : t,
    );
    applyWorkspaceMutation(set, get, { ...workspace, tiles });
  },

  resetLayout: () => get().resetActiveLayout(),
}));

function applyWorkspaceMutation(
  set: (partial: Partial<LayoutState>) => void,
  get: () => LayoutState,
  next: WorkspaceLayout,
) {
  // Mirror the new tiles into the active NamedLayout's serialized JSON so
  // a subsequent setActive(id) round-trip doesn't regress the change.
  const { layouts, activeLayoutId } = get();
  const json = serializeWorkspace(next);
  const active = findActive(layouts, activeLayoutId);
  if (active) {
    const updated: NamedLayout = { ...active, layoutJson: json };
    const newLayouts = layouts.map((l) => (l.id === activeLayoutId ? updated : l));
    set({ workspace: next, layouts: newLayouts });
    scheduleSave(get);
  } else {
    // Test / unhydrated state — just update workspace.
    set({ workspace: next });
  }
}

function scheduleSave(get: () => LayoutState) {
  if (saveTimer) clearTimeout(saveTimer);
  saveTimer = setTimeout(() => {
    const { radioKey, layouts, activeLayoutId } = get();
    if (!radioKey) return;
    const active = findActive(layouts, activeLayoutId);
    if (!active) return;
    void putNamedLayout(radioKey, active);
  }, 1000);
}

function putNamedLayout(radioKey: string, layout: NamedLayout): Promise<unknown> {
  if (!radioKey) return Promise.resolve();
  return fetch('/api/ui/layouts', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      radioKey,
      layoutId: layout.id,
      name: layout.name,
      layoutJson: layout.layoutJson,
    }),
  });
}

function postActiveLayout(radioKey: string, layoutId: string): Promise<unknown> {
  if (!radioKey || !layoutId) return Promise.resolve();
  return fetch('/api/ui/layouts/active', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ radioKey, layoutId }),
  });
}

// Re-export EMPTY_WORKSPACE_LAYOUT so existing import sites don't need to
// reach into ../layout/workspace separately.
export { EMPTY_WORKSPACE_LAYOUT };
