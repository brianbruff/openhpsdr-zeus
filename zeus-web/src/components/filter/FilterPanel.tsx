// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Unified filter panel with favorites. Shows 3 favorite filter presets plus
// a "..." button to open the full preset selector modal where favorites can
// be configured.

import { useCallback, useEffect, useState } from 'react';
import { useConnectionStore } from '../../state/connection-store';
import { setFilter, getFilterPresets, getFavoriteFilterSlots, type FilterPresetDto } from '../../api/client';
import { formatFilterWidth, formatCutOffset } from './filterPresets';
import { FilterPresetSelector } from './FilterPresetSelector';

export function FilterPanel() {
  const mode = useConnectionStore((s) => s.mode);
  const filterLow = useConnectionStore((s) => s.filterLowHz);
  const filterHigh = useConnectionStore((s) => s.filterHighHz);
  const filterPresetName = useConnectionStore((s) => s.filterPresetName);
  const applyState = useConnectionStore((s) => s.applyState);

  const [favoritePresets, setFavoritePresets] = useState<FilterPresetDto[]>([]);
  const [selectorOpen, setSelectorOpen] = useState(false);

  // Load favorite presets when mode changes
  useEffect(() => {
    let cancelled = false;

    Promise.all([
      getFilterPresets(mode),
      getFavoriteFilterSlots(mode),
    ])
      .then(([allPresets, favoriteSlots]) => {
        if (!cancelled) {
          // Filter to only favorites
          const favorites = allPresets.filter((p) => favoriteSlots.includes(p.slotName));
          setFavoritePresets(favorites);
        }
      })
      .catch(() => {
        // Fallback to empty on error
        if (!cancelled) setFavoritePresets([]);
      });

    return () => { cancelled = true; };
  }, [mode]);

  const activeSlot = filterPresetName ?? null;
  const widthLabel = formatFilterWidth(filterLow, filterHigh);

  const selectPreset = useCallback(
    (slot: FilterPresetDto) => {
      useConnectionStore.setState({
        filterLowHz: slot.lowHz,
        filterHighHz: slot.highHz,
        filterPresetName: slot.slotName,
      });
      setFilter(slot.lowHz, slot.highHz, slot.slotName)
        .then(applyState)
        .catch(() => { /* next state poll reconciles */ });
    },
    [applyState],
  );

  // FM has no presets — hide filter panel entirely
  if (mode === 'FM') return null;

  return (
    <>
      <div className="ctrl-group filter-bar" style={{ minWidth: 400 }}>
        <div className="label-xs ctrl-lbl">FILTER</div>
        <div className="filter-bar__readout" role="group" aria-label="Filter edges and width">
          <div className="filter-bar__cell filter-bar__cell--lo">
            <div className="filter-bar__key">LOW CUT</div>
            <div className="filter-bar__val mono">{formatCutOffset(filterLow)}</div>
          </div>
          <div className="filter-bar__cell filter-bar__cell--width">
            <div className="filter-bar__key">WIDTH</div>
            <div className="filter-bar__val filter-bar__val--accent mono">{widthLabel}</div>
          </div>
          <div className="filter-bar__cell filter-bar__cell--hi">
            <div className="filter-bar__key">HIGH CUT</div>
            <div className="filter-bar__val mono">{formatCutOffset(filterHigh)}</div>
          </div>
        </div>
        <div className="btn-row wrap" style={{ gap: 3, width: 400 }}>
          {favoritePresets.map((slot) => (
            <button
              key={slot.slotName}
              type="button"
              onClick={() => selectPreset(slot)}
              className={`btn sm ${activeSlot === slot.slotName ? 'active' : ''}`}
              title={`${slot.slotName}: ${slot.lowHz >= 0 ? '+' : ''}${slot.lowHz} / ${slot.highHz >= 0 ? '+' : ''}${slot.highHz} Hz`}
            >
              {slot.label}
            </button>
          ))}
          <button
            type="button"
            onClick={() => setSelectorOpen(true)}
            className="btn sm"
            title="Show all filter presets"
            style={{ marginLeft: 4 }}
          >
            ⋯
          </button>
        </div>
      </div>

      <FilterPresetSelector
        isOpen={selectorOpen}
        onClose={() => setSelectorOpen(false)}
      />
    </>
  );
}
