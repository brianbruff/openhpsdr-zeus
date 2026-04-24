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

import { createContext, useContext, useEffect, type ReactNode } from 'react';
import { useBandPlanStore } from '../state/bandPlan';
import {
  binarySearchSegment,
  modeMatchesRestriction,
  type BandRegion,
  type BandSegment,
} from '../api/bands';
import type { RxMode } from '../api/client';

// Public API exposed to consumers (filter overlay, future TX guard, VFO label).
export type BandPlanContextValue = {
  currentRegionId: string;
  currentDisplayName: string;
  segments: BandSegment[];
  regions: BandRegion[];
  loaded: boolean;
  getSegment: (freqHz: number) => BandSegment | null;
  inBand: (freqHz: number, mode: RxMode) => boolean;
};

const BandPlanContext = createContext<BandPlanContextValue>({
  currentRegionId: '',
  currentDisplayName: '',
  segments: [],
  regions: [],
  loaded: false,
  getSegment: () => null,
  inBand: () => false,
});

export function BandPlanProvider({ children }: { children: ReactNode }) {
  const { segments, regions, currentRegionId, currentDisplayName, loaded, refresh } =
    useBandPlanStore();

  // Fetch on mount.
  useEffect(() => {
    void refresh();
  }, [refresh]);

  const getSegment = (freqHz: number): BandSegment | null =>
    binarySearchSegment(segments, freqHz);

  const inBand = (freqHz: number, mode: RxMode): boolean => {
    const seg = getSegment(freqHz);
    if (!seg || seg.allocation !== 'Amateur') return false;
    return modeMatchesRestriction(seg.modeRestriction, mode);
  };

  return (
    <BandPlanContext.Provider
      value={{ currentRegionId, currentDisplayName, segments, regions, loaded, getSegment, inBand }}
    >
      {children}
    </BandPlanContext.Provider>
  );
}

export function useBandPlan(): BandPlanContextValue {
  return useContext(BandPlanContext);
}
