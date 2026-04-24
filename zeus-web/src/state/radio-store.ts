// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

import { create } from 'zustand';
import {
  fetchRadioSelection,
  updateRadioSelection,
  type BoardKind,
  type RadioSelection,
} from '../api/radio';
import { usePaStore } from './pa-store';

type RadioStore = {
  selection: RadioSelection;
  loaded: boolean;
  inflight: boolean;
  error: string | null;
  load: () => Promise<void>;
  setPreferred: (preferred: BoardKind) => Promise<void>;
};

// The radio preference is persisted server-side (LiteDB) rather than in
// browser localStorage because PA defaults / drive math resolve board
// kind on the backend. Local storage would drift from the source of truth
// across tabs.
export const useRadioStore = create<RadioStore>((set, get) => ({
  selection: { preferred: 'Auto', connected: 'Unknown', effective: 'Unknown' },
  loaded: false,
  inflight: false,
  error: null,

  load: async () => {
    set({ inflight: true, error: null });
    try {
      const s = await fetchRadioSelection();
      set({ selection: s, loaded: true, inflight: false });
    } catch (err) {
      set({
        error: err instanceof Error ? err.message : String(err),
        inflight: false,
      });
    }
  },

  setPreferred: async (preferred) => {
    // Optimistic update so the dropdown feels instant; rollback on failure.
    const prev = get().selection;
    set({ selection: { ...prev, preferred }, inflight: true, error: null });
    try {
      const s = await updateRadioSelection(preferred);
      set({ selection: s, inflight: false });
      // Reload the PA panel with the PREFERRED as the preview override so
      // an operator who explicitly picks G2 while an HL2 is connected sees
      // G2's defaults in empty rows immediately (discovery still wins for
      // actual drive-byte math — that's the MISMATCH badge's job to flag).
      // Auto = no override → server uses the effective board (connected
      // wins over preferred).
      const override = preferred === 'Auto' ? undefined : preferred;
      await usePaStore.getState().load(override);
    } catch (err) {
      set({
        selection: prev,
        error: err instanceof Error ? err.message : String(err),
        inflight: false,
      });
    }
  },
}));
