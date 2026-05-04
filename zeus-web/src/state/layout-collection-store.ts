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
  parseWorkspaceLayout,
  type WorkspaceLayout,
} from '../layout/workspace';
import { DEFAULT_WORKSPACE_LAYOUT } from '../layout/defaultLayout';

export interface NamedLayout {
  id: string;
  name: string;
  workspace: WorkspaceLayout;
}

interface LayoutCollectionState {
  /** All available layouts. Never empty — always includes at least one default. */
  layouts: NamedLayout[];
  /** ID of the currently active layout. */
  activeLayoutId: string;
  /** True after loadFromServer() has run. */
  isLoaded: boolean;
  /** Load layout collection from server. */
  loadFromServer: () => Promise<void>;
  /** Switch to a different layout by ID. */
  setActiveLayout: (id: string) => void;
  /** Get the currently active layout workspace. */
  getActiveWorkspace: () => WorkspaceLayout;
  /** Update the active layout's workspace. */
  updateActiveWorkspace: (workspace: WorkspaceLayout) => void;
  /** Add a new layout with the given name. Returns the new layout ID. */
  addLayout: (name: string) => string;
  /** Delete a layout by ID. Cannot delete the last remaining layout. */
  deleteLayout: (id: string) => void;
  /** Reset the active layout to its default state. */
  resetActiveLayout: () => void;
  /** Rename a layout. */
  renameLayout: (id: string, newName: string) => void;
  /** Debounced PUT to server. */
  syncToServer: () => void;
  /** sendBeacon-with-fetch-fallback for page-unload persistence. */
  syncToServerBeforeUnload: () => void;
}

let debounceTimer: ReturnType<typeof setTimeout> | null = null;

function generateLayoutId(): string {
  return `layout-${Date.now()}-${Math.random().toString(36).slice(2, 9)}`;
}

const DEFAULT_LAYOUT_ID = 'default-layout';

function createDefaultLayout(): NamedLayout {
  return {
    id: DEFAULT_LAYOUT_ID,
    name: 'Default Layout',
    workspace: DEFAULT_WORKSPACE_LAYOUT,
  };
}

export const useLayoutCollectionStore = create<LayoutCollectionState>((set, get) => ({
  layouts: [createDefaultLayout()],
  activeLayoutId: DEFAULT_LAYOUT_ID,
  isLoaded: false,

  loadFromServer: async () => {
    try {
      const res = await fetch('/api/ui/layout-collection');
      if (res.status === 404 || !res.ok) {
        // No saved collection — use defaults
        set({ layouts: [createDefaultLayout()], activeLayoutId: DEFAULT_LAYOUT_ID, isLoaded: true });
        return;
      }
      const dto = (await res.json()) as {
        layouts: Array<{ id: string; name: string; layoutJson: string }>;
        activeLayoutId: string;
      };

      const layouts: NamedLayout[] = dto.layouts.map((l) => {
        let workspace: WorkspaceLayout;
        try {
          workspace = parseWorkspaceLayout(JSON.parse(l.layoutJson));
          // If parsing returns empty (schema mismatch), fall back to default
          if (workspace.tiles.length === 0) {
            workspace = DEFAULT_WORKSPACE_LAYOUT;
          }
        } catch {
          workspace = DEFAULT_WORKSPACE_LAYOUT;
        }
        return {
          id: l.id,
          name: l.name,
          workspace,
        };
      });

      // Ensure we always have at least one layout
      if (layouts.length === 0) {
        layouts.push(createDefaultLayout());
      }

      // Validate activeLayoutId exists in the collection
      const activeId = layouts.some((l) => l.id === dto.activeLayoutId)
        ? dto.activeLayoutId
        : (layouts[0]?.id ?? DEFAULT_LAYOUT_ID);

      set({ layouts, activeLayoutId: activeId, isLoaded: true });
    } catch {
      set({ layouts: [createDefaultLayout()], activeLayoutId: DEFAULT_LAYOUT_ID, isLoaded: true });
    }
  },

  setActiveLayout: (id) => {
    const { layouts } = get();
    if (!layouts.some((l) => l.id === id)) return;
    set({ activeLayoutId: id });
    get().syncToServer();
  },

  getActiveWorkspace: () => {
    const { layouts, activeLayoutId } = get();
    const active = layouts.find((l) => l.id === activeLayoutId);
    return active?.workspace ?? DEFAULT_WORKSPACE_LAYOUT;
  },

  updateActiveWorkspace: (workspace) => {
    const { layouts, activeLayoutId } = get();
    const updated = layouts.map((l) =>
      l.id === activeLayoutId ? { ...l, workspace } : l
    );
    set({ layouts: updated });
    get().syncToServer();
  },

  addLayout: (name) => {
    const id = generateLayoutId();
    const newLayout: NamedLayout = {
      id,
      name,
      workspace: DEFAULT_WORKSPACE_LAYOUT,
    };
    const { layouts } = get();
    set({ layouts: [...layouts, newLayout] });
    get().syncToServer();
    return id;
  },

  deleteLayout: (id) => {
    const { layouts, activeLayoutId } = get();
    if (layouts.length <= 1) {
      // Cannot delete the last layout
      return;
    }
    const filtered = layouts.filter((l) => l.id !== id);
    // If we deleted the active layout, switch to the first remaining one
    const newActiveId = activeLayoutId === id ? (filtered[0]?.id ?? DEFAULT_LAYOUT_ID) : activeLayoutId;
    set({ layouts: filtered, activeLayoutId: newActiveId });
    get().syncToServer();
  },

  resetActiveLayout: () => {
    const { layouts, activeLayoutId } = get();
    const updated = layouts.map((l) =>
      l.id === activeLayoutId ? { ...l, workspace: DEFAULT_WORKSPACE_LAYOUT } : l
    );
    set({ layouts: updated });
    get().syncToServer();
  },

  renameLayout: (id, newName) => {
    const { layouts } = get();
    const updated = layouts.map((l) =>
      l.id === id ? { ...l, name: newName } : l
    );
    set({ layouts: updated });
    get().syncToServer();
  },

  syncToServer: () => {
    if (debounceTimer) clearTimeout(debounceTimer);
    debounceTimer = setTimeout(() => {
      const { layouts, activeLayoutId } = get();
      const dto = {
        layouts: layouts.map((l) => ({
          id: l.id,
          name: l.name,
          layoutJson: JSON.stringify(l.workspace),
        })),
        activeLayoutId,
      };
      void fetch('/api/ui/layout-collection', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(dto),
      });
    }, 1000);
  },

  syncToServerBeforeUnload: () => {
    const { layouts, activeLayoutId } = get();
    const dto = {
      layouts: layouts.map((l) => ({
        id: l.id,
        name: l.name,
        layoutJson: JSON.stringify(l.workspace),
      })),
      activeLayoutId,
    };
    const body = JSON.stringify(dto);
    const blob = new Blob([body], { type: 'application/json' });
    if (!navigator.sendBeacon('/api/ui/layout-collection-beacon', blob)) {
      void fetch('/api/ui/layout-collection', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body,
        keepalive: true,
      });
    }
  },
}));
