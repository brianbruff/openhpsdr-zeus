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
// VST host store. Mirrors GET /api/plughost/state plus per-slot parameter
// caches and editor-window geometry hints. NOT persisted: the backend is
// the source of truth and a fresh browser refetches on connect.

import { create } from 'zustand';

import {
  fetchVstHostCatalog,
  fetchVstHostSlot,
  fetchVstHostState,
  hideVstHostSlotEditor,
  loadVstHostSlot,
  removeVstHostSearchPath,
  addVstHostSearchPath,
  setVstHostMaster,
  setVstHostSlotBypass,
  setVstHostSlotParameter,
  showVstHostSlotEditor,
  unloadVstHostSlot,
  VST_HOST_SLOT_COUNT,
  type VstHostCatalogEntry,
  type VstHostParameter,
  type VstHostSlotState,
  type VstHostState,
} from '../api/vst-host';

export type EditorGeometry = {
  width: number;
  height: number;
  // Open / closed reflects what we last heard from the server. Used by the
  // submenu's "Editor open (WxH)" hint and to debounce repeated clicks on
  // a slot's Edit button.
  open: boolean;
};

export type VstHostStoreState = {
  // Lifecycle
  loaded: boolean;
  inflight: boolean;
  loadError: string | null;
  // Snapshot from /api/plughost/state. Pre-seeded with 8 empty slots so
  // first paint isn't blank.
  master: VstHostState;
  // Per-slot parameter cache. Loaded lazily when a slot row expands.
  slotParameters: Map<number, VstHostParameter[]>;
  // Per-slot last error string (load/unload/bypass/edit). Cleared on
  // success and surfaced inline next to the offending control.
  slotErrors: Map<number, string>;
  // Per-slot editor geometry hint pushed by the server on
  // slotEditorResized:N:W:H.
  editors: Map<number, EditorGeometry>;
  // Plugin browser state — lives at the top level so opening / closing
  // keeps the catalog warm.
  catalog: VstHostCatalogEntry[];
  catalogLoaded: boolean;
  catalogInflight: boolean;
  catalogError: string | null;

  // Operator notification surfaces — sidecarExited (transient) and a
  // generic last-error pill.
  notice: string | null;

  // Actions
  refresh: () => Promise<void>;
  setMasterEnabled: (enabled: boolean) => Promise<void>;
  loadSlot: (index: number, path: string) => Promise<void>;
  unloadSlot: (index: number) => Promise<void>;
  setSlotBypass: (index: number, bypass: boolean) => Promise<void>;
  refreshSlot: (index: number) => Promise<void>;
  refreshSlotParameters: (index: number) => Promise<void>;
  setSlotParameter: (
    index: number,
    paramId: number,
    value: number,
  ) => Promise<void>;
  showEditor: (index: number) => Promise<void>;
  hideEditor: (index: number) => Promise<void>;

  // Catalog / search-path actions
  refreshCatalog: (rescan: boolean) => Promise<void>;
  addSearchPath: (path: string) => Promise<void>;
  removeSearchPath: (path: string) => Promise<void>;

  // SignalR-driven event ingest. Called from ws-client when an
  // 0x1A VstHostEvent frame is decoded.
  applyEvent: (tag: string) => void;

  clearNotice: () => void;
  clearSlotError: (index: number) => void;
};

const EMPTY_STATE: VstHostState = {
  masterEnabled: false,
  isRunning: false,
  slots: Array.from({ length: VST_HOST_SLOT_COUNT }, (_, i) => ({
    index: i,
    plugin: null,
    bypass: false,
    parameterCount: 0,
  })),
  customSearchPaths: [],
};

function patchSlot(
  state: VstHostState,
  index: number,
  patch: Partial<VstHostSlotState>,
): VstHostState {
  const slots = state.slots.map((s) =>
    s.index === index ? { ...s, ...patch } : s,
  );
  return { ...state, slots };
}

function errorMessage(err: unknown): string {
  if (err instanceof Error) return err.message;
  return typeof err === 'string' ? err : 'unknown error';
}

export const useVstHostStore = create<VstHostStoreState>((set, get) => ({
  loaded: false,
  inflight: false,
  loadError: null,
  master: EMPTY_STATE,
  slotParameters: new Map(),
  slotErrors: new Map(),
  editors: new Map(),
  catalog: [],
  catalogLoaded: false,
  catalogInflight: false,
  catalogError: null,
  notice: null,

  refresh: async () => {
    set({ inflight: true, loadError: null });
    try {
      const next = await fetchVstHostState();
      set({ master: next, loaded: true, inflight: false });
    } catch (err) {
      set({ inflight: false, loadError: errorMessage(err) });
    }
  },

  setMasterEnabled: async (enabled) => {
    // Optimistic toggle so the UI doesn't lag the click. Roll back on error.
    const prev = get().master;
    set({ master: { ...prev, masterEnabled: enabled }, loadError: null });
    try {
      const next = await setVstHostMaster(enabled);
      set({ master: next });
    } catch (err) {
      set({ master: prev, loadError: errorMessage(err) });
    }
  },

  loadSlot: async (index, path) => {
    const state = get();
    state.clearSlotError(index);
    try {
      await loadVstHostSlot(index, path);
      // SignalR will push slotStateChanged:N which triggers a refreshSlot;
      // we also re-fetch the slot directly so the row updates synchronously
      // even if the WS frame is briefly delayed.
      await get().refreshSlot(index);
    } catch (err) {
      const next = new Map(get().slotErrors);
      next.set(index, errorMessage(err));
      set({ slotErrors: next });
    }
  },

  unloadSlot: async (index) => {
    const state = get();
    state.clearSlotError(index);
    try {
      await unloadVstHostSlot(index);
      // Local prediction so the row goes empty immediately. The server's
      // slotStateChanged:N event will reconcile.
      set({
        master: patchSlot(get().master, index, {
          plugin: null,
          parameterCount: 0,
          bypass: false,
        }),
      });
      const params = new Map(get().slotParameters);
      params.delete(index);
      const editors = new Map(get().editors);
      editors.delete(index);
      set({ slotParameters: params, editors });
    } catch (err) {
      const next = new Map(get().slotErrors);
      next.set(index, errorMessage(err));
      set({ slotErrors: next });
    }
  },

  setSlotBypass: async (index, bypass) => {
    const prev = get().master;
    set({ master: patchSlot(prev, index, { bypass }) });
    try {
      await setVstHostSlotBypass(index, bypass);
    } catch (err) {
      set({ master: prev });
      const next = new Map(get().slotErrors);
      next.set(index, errorMessage(err));
      set({ slotErrors: next });
    }
  },

  refreshSlot: async (index) => {
    try {
      const detail = await fetchVstHostSlot(index);
      set({
        master: patchSlot(get().master, index, {
          plugin: detail.plugin,
          bypass: detail.bypass,
          parameterCount: detail.parameters.length,
        }),
      });
      // If the slot is currently expanded we already have the params open;
      // refresh the cache so the sliders match the server.
      if (get().slotParameters.has(index)) {
        const next = new Map(get().slotParameters);
        next.set(index, detail.parameters);
        set({ slotParameters: next });
      }
    } catch (err) {
      const next = new Map(get().slotErrors);
      next.set(index, errorMessage(err));
      set({ slotErrors: next });
    }
  },

  refreshSlotParameters: async (index) => {
    try {
      const detail = await fetchVstHostSlot(index);
      const next = new Map(get().slotParameters);
      next.set(index, detail.parameters);
      set({ slotParameters: next });
    } catch (err) {
      const next = new Map(get().slotErrors);
      next.set(index, errorMessage(err));
      set({ slotErrors: next });
    }
  },

  setSlotParameter: async (index, paramId, value) => {
    // Optimistic local-cache update so the slider position stays glued to
    // the operator's drag during the round-trip. If the POST fails, the
    // SignalR parameterChanged echo will reconcile to the server's view.
    const prev = get().slotParameters;
    const list = prev.get(index);
    if (list) {
      const updated = list.map((p) =>
        p.id === paramId ? { ...p, currentValue: value } : p,
      );
      const next = new Map(prev);
      next.set(index, updated);
      set({ slotParameters: next });
    }
    try {
      await setVstHostSlotParameter(index, paramId, value);
    } catch (err) {
      set({ slotParameters: prev });
      const next = new Map(get().slotErrors);
      next.set(index, errorMessage(err));
      set({ slotErrors: next });
    }
  },

  showEditor: async (index) => {
    try {
      const outcome = await showVstHostSlotEditor(index);
      const next = new Map(get().editors);
      next.set(index, {
        width: outcome.width,
        height: outcome.height,
        open: true,
      });
      set({ editors: next });
    } catch (err) {
      const next = new Map(get().slotErrors);
      next.set(index, errorMessage(err));
      set({ slotErrors: next });
    }
  },

  hideEditor: async (index) => {
    try {
      await hideVstHostSlotEditor(index);
      const next = new Map(get().editors);
      const cur = next.get(index);
      next.set(index, {
        width: cur?.width ?? 0,
        height: cur?.height ?? 0,
        open: false,
      });
      set({ editors: next });
    } catch (err) {
      const next = new Map(get().slotErrors);
      next.set(index, errorMessage(err));
      set({ slotErrors: next });
    }
  },

  refreshCatalog: async (rescan) => {
    set({ catalogInflight: true, catalogError: null });
    try {
      const list = await fetchVstHostCatalog(rescan);
      set({
        catalog: list,
        catalogLoaded: true,
        catalogInflight: false,
      });
    } catch (err) {
      set({ catalogInflight: false, catalogError: errorMessage(err) });
    }
  },

  addSearchPath: async (path) => {
    try {
      const result = await addVstHostSearchPath(path);
      set({
        master: { ...get().master, customSearchPaths: result.paths },
      });
    } catch (err) {
      set({ catalogError: errorMessage(err) });
    }
  },

  removeSearchPath: async (path) => {
    try {
      const result = await removeVstHostSearchPath(path);
      set({
        master: { ...get().master, customSearchPaths: result.paths },
      });
    } catch (err) {
      set({ catalogError: errorMessage(err) });
    }
  },

  applyEvent: (tag) => {
    // Tags are colon-delimited; the head identifies the event family.
    // See VstHostHostedService.cs ~L530-565 for the emission sites.
    const [head, ...rest] = tag.split(':');
    if (!head) return;
    if (head === 'snapshot') {
      void get().refresh();
      return;
    }
    if (head === 'chainEnabledChanged') {
      const enabled = rest[0] === '1';
      set({ master: { ...get().master, masterEnabled: enabled } });
      return;
    }
    if (head === 'slotStateChanged') {
      const idx = Number.parseInt(rest[0] ?? '', 10);
      if (Number.isFinite(idx)) void get().refreshSlot(idx);
      return;
    }
    if (head === 'slotEditorClosed') {
      const idx = Number.parseInt(rest[0] ?? '', 10);
      if (!Number.isFinite(idx)) return;
      const next = new Map(get().editors);
      const cur = next.get(idx);
      next.set(idx, {
        width: cur?.width ?? 0,
        height: cur?.height ?? 0,
        open: false,
      });
      set({ editors: next });
      return;
    }
    if (head === 'slotEditorResized') {
      const idx = Number.parseInt(rest[0] ?? '', 10);
      const w = Number.parseInt(rest[1] ?? '', 10);
      const h = Number.parseInt(rest[2] ?? '', 10);
      if (!Number.isFinite(idx)) return;
      const next = new Map(get().editors);
      next.set(idx, {
        width: Number.isFinite(w) ? w : 0,
        height: Number.isFinite(h) ? h : 0,
        open: true,
      });
      set({ editors: next });
      return;
    }
    if (head === 'parameterChanged') {
      const idx = Number.parseInt(rest[0] ?? '', 10);
      const pid = Number.parseInt(rest[1] ?? '', 10);
      const val = Number.parseFloat(rest[2] ?? '');
      if (!Number.isFinite(idx) || !Number.isFinite(pid) || !Number.isFinite(val)) {
        return;
      }
      const params = get().slotParameters;
      const list = params.get(idx);
      if (!list) return;
      const updated = list.map((p) =>
        p.id === pid ? { ...p, currentValue: val } : p,
      );
      const next = new Map(params);
      next.set(idx, updated);
      set({ slotParameters: next });
      return;
    }
    if (head === 'sidecarExited') {
      const code = rest[0] ?? '?';
      set({
        master: { ...get().master, isRunning: false, masterEnabled: false },
        notice: `Plugin host stopped (exit ${code}). Re-enable to restart.`,
      });
      return;
    }
    // Unknown tag — keep state untouched but surface for debugging.
    // The store's notice channel is operator-visible; don't pollute it
    // with raw tag names. The catalog/load errors are surfaced inline.
  },

  clearNotice: () => set({ notice: null }),
  clearSlotError: (index) => {
    const next = new Map(get().slotErrors);
    if (next.has(index)) {
      next.delete(index);
      set({ slotErrors: next });
    }
  },
}));
