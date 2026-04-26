// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Unified filter panel with favorites. Shows 3 favorite filter presets plus
// a "..." button that drops down the existing FilterRibbon (full presets +
// mini-pan + readouts) for the operator to pick from or edit favorites.

import { useCallback, useEffect, useState } from 'react';
import { useConnectionStore } from '../../state/connection-store';
import {
  setFilter,
  setFilterAdvancedPaneOpen,
  getFilterPresets,
  type FilterPresetDto,
} from '../../api/client';
import { useFilterFavoritesStore, useFavoritesForMode } from '../../state/filter-favorites-store';

const RIBBON_OPEN_KEY = 'zeus.filter.advancedPaneOpen';

export function FilterPanel() {
  const mode = useConnectionStore((s) => s.mode);
  const filterPresetName = useConnectionStore((s) => s.filterPresetName);
  const ribbonOpen = useConnectionStore((s) => s.filterAdvancedPaneOpen);
  const applyState = useConnectionStore((s) => s.applyState);
  const loadFavorites = useFilterFavoritesStore((s) => s.load);
  const favoriteSlotNames = useFavoritesForMode(mode);

  const [presets, setPresets] = useState<FilterPresetDto[]>([]);

  useEffect(() => { void loadFavorites(mode); }, [mode, loadFavorites]);

  useEffect(() => {
    let cancelled = false;
    getFilterPresets(mode)
      .then((list) => { if (!cancelled) setPresets(list); })
      .catch(() => { if (!cancelled) setPresets([]); });
    return () => { cancelled = true; };
  }, [mode]);

  const favoritePresets: FilterPresetDto[] = favoriteSlotNames
    .map((name) => presets.find((p) => p.slotName === name))
    .filter((p): p is FilterPresetDto => Boolean(p));

  const activeSlot = filterPresetName ?? null;

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

  const toggleRibbon = useCallback(() => {
    const next = !ribbonOpen;
    useConnectionStore.setState({ filterAdvancedPaneOpen: next });
    try { window.localStorage.setItem(RIBBON_OPEN_KEY, next ? '1' : '0'); } catch { /* ok */ }
    setFilterAdvancedPaneOpen(next).catch(() => { /* next state poll reconciles */ });
  }, [ribbonOpen]);

  if (mode === 'FM') return null;

  return (
    <div className="ctrl-group filter-bar" style={{ minWidth: 220 }}>
      <div className="label-xs ctrl-lbl">FILTER</div>
      <div className="btn-row wrap" style={{ gap: 3 }}>
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
          onClick={toggleRibbon}
          className={`btn sm ${ribbonOpen ? 'active' : ''}`}
          title="Open filter panel"
          aria-expanded={ribbonOpen}
          style={{ marginLeft: 4 }}
        >
          ⋯
        </button>
      </div>
    </div>
  );
}
