// SPDX-License-Identifier: GPL-2.0-or-later
//
// Per-mode favorite filter slot cache, shared between FilterPanel
// (top control strip) and FilterRibbon (drop-down). Reads and writes go
// through this store so a drag-drop edit in the ribbon repaints the top
// strip without either component owning the canonical list.

import { create } from 'zustand';
import {
  getFavoriteFilterSlots,
  setFavoriteFilterSlots,
  type RxMode,
} from '../api/client';
import { defaultFavoritesForMode } from '../components/filter/filterPresets';

type FilterFavoritesState = {
  byMode: Partial<Record<RxMode, string[]>>;
  loading: Partial<Record<RxMode, boolean>>;
  load: (mode: RxMode) => Promise<void>;
  update: (mode: RxMode, slots: string[]) => Promise<void>;
};

export const useFilterFavoritesStore = create<FilterFavoritesState>((set, get) => ({
  byMode: {},
  loading: {},

  load: async (mode) => {
    if (get().byMode[mode] !== undefined || get().loading[mode]) return;
    set((s) => ({ loading: { ...s.loading, [mode]: true } }));
    try {
      const slots = await getFavoriteFilterSlots(mode);
      set((s) => ({
        byMode: { ...s.byMode, [mode]: slots.length === 3 ? slots : [...defaultFavoritesForMode(mode)] },
        loading: { ...s.loading, [mode]: false },
      }));
    } catch {
      set((s) => ({
        byMode: { ...s.byMode, [mode]: [...defaultFavoritesForMode(mode)] },
        loading: { ...s.loading, [mode]: false },
      }));
    }
  },

  update: async (mode, slots) => {
    if (slots.length !== 3) return;
    set((s) => ({ byMode: { ...s.byMode, [mode]: slots } }));
    try {
      await setFavoriteFilterSlots(mode, slots);
    } catch {
      // server rejected; reload to resync
      set((s) => ({ byMode: { ...s.byMode, [mode]: undefined as unknown as string[] } }));
      await get().load(mode);
    }
  },
}));

export function useFavoritesForMode(mode: RxMode): string[] {
  const slots = useFilterFavoritesStore((s) => s.byMode[mode]);
  return slots ?? [...defaultFavoritesForMode(mode)];
}
