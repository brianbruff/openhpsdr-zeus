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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

import { create } from 'zustand';
import {
  fetchCurrent,
  fetchRegions,
  fetchPlan,
  setCurrentRegion,
  putPlan,
  deletePlan,
  type BandRegion,
  type BandSegment,
  type BandPlanDto,
} from '../api/bands';

type BandPlanState = {
  regions: BandRegion[];
  currentRegionId: string;
  currentDisplayName: string;
  segments: BandSegment[];
  loaded: boolean;
  inflight: boolean;
  error: string | null;

  // Load current region + region catalog on mount or after WS notification.
  refresh: () => Promise<void>;
  // Fetch segments for any region (used by editor to preview other regions).
  fetchRegionPlan: (regionId: string) => Promise<BandPlanDto>;
  // Change the active region (POST /api/bands/current).
  changeRegion: (regionId: string) => Promise<void>;
  // Save an override (PUT /api/bands/plan).
  saveOverride: (regionId: string, segments: BandSegment[]) => Promise<void>;
  // Reset to defaults (DELETE /api/bands/plan/:regionId).
  resetOverride: (regionId: string) => Promise<void>;
};

export const useBandPlanStore = create<BandPlanState>((set, get) => ({
  regions: [],
  currentRegionId: '',
  currentDisplayName: '',
  segments: [],
  loaded: false,
  inflight: false,
  error: null,

  refresh: async () => {
    if (get().inflight) return;
    set({ inflight: true, error: null });
    try {
      const [current, regions] = await Promise.all([fetchCurrent(), fetchRegions()]);
      set({
        regions,
        currentRegionId: current.regionId,
        currentDisplayName: current.displayName,
        segments: current.segments,
        loaded: true,
        inflight: false,
      });
    } catch (err) {
      set({ error: err instanceof Error ? err.message : String(err), inflight: false });
    }
  },

  fetchRegionPlan: async (regionId: string) => {
    return fetchPlan(regionId);
  },

  changeRegion: async (regionId: string) => {
    set({ inflight: true, error: null });
    try {
      const result = await setCurrentRegion(regionId);
      set({
        currentRegionId: result.regionId,
        currentDisplayName: result.displayName,
        segments: result.segments,
        inflight: false,
      });
    } catch (err) {
      set({ error: err instanceof Error ? err.message : String(err), inflight: false });
      throw err;
    }
  },

  saveOverride: async (regionId: string, segments: BandSegment[]) => {
    set({ inflight: true, error: null });
    try {
      await putPlan(regionId, segments);
      // Refresh current plan if the edited region is the active one.
      if (get().currentRegionId === regionId) {
        const current = await fetchCurrent();
        set({ segments: current.segments, inflight: false });
      } else {
        set({ inflight: false });
      }
    } catch (err) {
      set({ error: err instanceof Error ? err.message : String(err), inflight: false });
      throw err;
    }
  },

  resetOverride: async (regionId: string) => {
    set({ inflight: true, error: null });
    try {
      await deletePlan(regionId);
      if (get().currentRegionId === regionId) {
        const current = await fetchCurrent();
        set({ segments: current.segments, inflight: false });
      } else {
        set({ inflight: false });
      }
    } catch (err) {
      set({ error: err instanceof Error ? err.message : String(err), inflight: false });
      throw err;
    }
  },
}));
