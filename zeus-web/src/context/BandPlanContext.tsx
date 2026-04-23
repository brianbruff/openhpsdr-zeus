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

import {
  createContext,
  useContext,
  useEffect,
  useMemo,
  type ReactNode,
} from 'react';
import { useBandPlanStore } from '../state/bandPlan';
import {
  binarySearchSegment,
  modeMatchesRestriction,
  type BandRegion,
  type BandSegment,
} from '../api/bands';
import type { RxMode } from '../api/client';

export interface BandPlanCtx {
  currentRegionId: string;
  regions: BandRegion[];
  segments: BandSegment[];

  getSegment(freqHz: number): BandSegment | null;
  inBand(freqHz: number, mode: RxMode): boolean;
}

export const BandPlanContext = createContext<BandPlanCtx | null>(null);

export function useBandPlan(): BandPlanCtx {
  const ctx = useContext(BandPlanContext);
  if (!ctx) throw new Error('useBandPlan must be used inside BandPlanProvider');
  return ctx;
}

interface Props {
  children: ReactNode;
}

export function BandPlanProvider({ children }: Props) {
  const { currentRegionId, segments, regions, refresh } = useBandPlanStore();

  useEffect(() => {
    refresh();
  }, [refresh]);

  const ctx = useMemo<BandPlanCtx>(() => ({
    currentRegionId,
    regions,
    segments,

    getSegment(freqHz: number): BandSegment | null {
      return binarySearchSegment(segments, freqHz);
    },

    inBand(freqHz: number, mode: RxMode): boolean {
      const seg = binarySearchSegment(segments, freqHz);
      if (!seg || seg.allocation !== 'Amateur') return false;
      return modeMatchesRestriction(mode, seg.modeRestriction);
    },
  }), [currentRegionId, regions, segments]);

  return (
    <BandPlanContext.Provider value={ctx}>
      {children}
    </BandPlanContext.Provider>
  );
}
