// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// HL2 multi-slice (multi-RX) operator-preference store. The truth lives on
// the backend in StateDto.MultiSlice; this store hydrates from /api/state on
// load and POSTs through to /api/multi-slice on operator edits. Optimistic
// updates with rollback on failure (parity with `radio-store.setPreferred`).
//
// PS-on conflict: backend logs a warning and returns the snapshot with
// Enabled=false. The store compares the request to the response and surfaces
// `conflict='puresignal'` so the RadioSelector tooltip explains *why* the
// toggle didn't latch.

import { create } from 'zustand';
import {
  fetchState,
  setMultiSliceConfig,
  MULTI_SLICE_DEFAULT,
  type MultiSliceConfigDto,
  type RadioStateDto,
} from '../api/client';

export type MultiSliceConflictReason = 'puresignal' | null;

interface MultiSliceStore {
  config: MultiSliceConfigDto;
  loaded: boolean;
  inflight: boolean;
  error: string | null;
  /** Set to 'puresignal' when a PUT was silently downgraded by the backend
   *  (request enabled=true but response enabled=false while PS is on). The
   *  RadioSelector surfaces this as the "PureSignal in use — disable PS to
   *  enable multi-slice" tooltip. Cleared on the next successful set. */
  conflict: MultiSliceConflictReason;
  /** Hydrate from the radio state DTO returned by `/api/state`. Idempotent;
   *  call on connect / reconnect. */
  load: () => Promise<void>;
  /** Apply a snapshot received from anywhere else (e.g. a setter that
   *  returned the full state DTO). Avoids a redundant fetchState round-trip. */
  applyFromState: (s: RadioStateDto) => void;
  setEnabled: (enabled: boolean) => Promise<void>;
  setNumActiveSlices: (n: number) => Promise<void>;
}

export const useMultiSliceStore = create<MultiSliceStore>((set, get) => ({
  config: { ...MULTI_SLICE_DEFAULT },
  loaded: false,
  inflight: false,
  error: null,
  conflict: null,

  load: async () => {
    set({ inflight: true, error: null });
    try {
      const state = await fetchState();
      set({
        config: state.multiSlice,
        loaded: true,
        inflight: false,
      });
    } catch (err) {
      // /api/state may be unreachable (no server / connecting). Soft-fail
      // to defaults so the UI still renders the single-slice path.
      set({
        loaded: true,
        inflight: false,
        config: { ...MULTI_SLICE_DEFAULT },
        error: err instanceof Error ? err.message : String(err),
      });
    }
  },

  applyFromState: (s) => {
    set({ config: s.multiSlice, loaded: true });
  },

  setEnabled: async (enabled) => {
    const prev = get().config;
    const requested: MultiSliceConfigDto = { ...prev, enabled };
    set({ config: requested, inflight: true, error: null, conflict: null });
    try {
      const state = await setMultiSliceConfig(requested);
      const applied = state.multiSlice;
      // Detect PS-conflict: we asked to enable, server returned disabled.
      // mi0bot's RadioService logs the warning and refuses; we surface it.
      const conflict: MultiSliceConflictReason =
        enabled && !applied.enabled ? 'puresignal' : null;
      set({ config: applied, inflight: false, conflict });
    } catch (err) {
      set({
        config: prev,
        inflight: false,
        error: err instanceof Error ? err.message : String(err),
      });
    }
  },

  setNumActiveSlices: async (n) => {
    const prev = get().config;
    const clamped = Math.max(1, Math.floor(n));
    const requested: MultiSliceConfigDto = {
      ...prev,
      numActiveSlices: clamped,
    };
    set({ config: requested, inflight: true, error: null, conflict: null });
    try {
      const state = await setMultiSliceConfig(requested);
      set({ config: state.multiSlice, inflight: false });
    } catch (err) {
      set({
        config: prev,
        inflight: false,
        error: err instanceof Error ? err.message : String(err),
      });
    }
  },
}));
