// SPDX-License-Identifier: GPL-2.0-or-later
//
// Per-toolbar favorite-slot store for the Mode/Band/Step pickers in the
// control strip. Each kind keeps an ordered list of three slot keys; the
// dropdown lets the operator drag any option onto a slot to pin it. Step
// also stores the currently-selected step value so the toolbar widget and
// the side-stack widget agree on a single tuning step.

import { create } from 'zustand';
import { persist } from 'zustand/middleware';

export type ToolbarFavKind = 'mode' | 'band' | 'step';

const DEFAULT_MODE: readonly string[] = ['USB', 'LSB', 'CWU'];
const DEFAULT_BAND: readonly string[] = ['40m', '20m', '15m'];
const DEFAULT_STEP: readonly string[] = ['100', '500', '1000'];
const DEFAULT_STEP_HZ = 500;

type ToolbarFavoritesState = {
  mode: string[];
  band: string[];
  step: string[];
  stepHz: number;
  setFavorites: (kind: ToolbarFavKind, slots: string[]) => void;
  setStepHz: (hz: number) => void;
};

export const useToolbarFavoritesStore = create<ToolbarFavoritesState>()(
  persist(
    (set) => ({
      mode: [...DEFAULT_MODE],
      band: [...DEFAULT_BAND],
      step: [...DEFAULT_STEP],
      stepHz: DEFAULT_STEP_HZ,
      setFavorites: (kind, slots) => {
        if (slots.length !== 3) return;
        if (kind === 'mode') set({ mode: slots });
        else if (kind === 'band') set({ band: slots });
        else if (kind === 'step') set({ step: slots });
      },
      setStepHz: (hz) => set({ stepHz: hz }),
    }),
    { name: 'zeus.toolbar.favorites' },
  ),
);
