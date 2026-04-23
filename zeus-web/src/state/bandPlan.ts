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
import { fetchCurrentBandPlan, fetchBandRegions } from '../api/bands';
import type { BandRegion, BandSegment } from '../api/bands';

export type BandPlanState = {
  currentRegionId: string;
  segments: BandSegment[];
  regions: BandRegion[];
  loading: boolean;
  error: string | null;
  refresh: () => void;
};

export const useBandPlanStore = create<BandPlanState>((set, get) => ({
  currentRegionId: 'IARU_R1',
  segments: [],
  regions: [],
  loading: false,
  error: null,

  refresh: () => {
    if (get().loading) return;
    set({ loading: true, error: null });

    const planPromise = fetchCurrentBandPlan();
    const regionsPromise = fetchBandRegions();

    Promise.all([planPromise, regionsPromise])
      .then(([plan, regions]) => {
        set({
          currentRegionId: plan.regionId,
          segments: plan.segments,
          regions,
          loading: false,
          error: null,
        });
      })
      .catch((err: unknown) => {
        set({
          loading: false,
          error: err instanceof Error ? err.message : 'Failed to load band plan',
        });
      });
  },
}));
