// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Unified filter control-strip widget. Three favorite filter-preset buttons
// + a "⋯" toggle that opens the FilterRibbon (mini-pan + presets + custom).
// Operators drag any preset chip out of the ribbon onto one of the three
// buttons here to pin it — same UX as the Mode/Band/Step toolbar groups.

import { useCallback, useEffect, useState } from 'react';
import { useConnectionStore } from '../../state/connection-store';
import {
  setFilter,
  setFilterAdvancedPaneOpen,
  getFilterPresets,
  type FilterPresetDto,
} from '../../api/client';
import { useFilterFavoritesStore, useFavoritesForMode } from '../../state/filter-favorites-store';
import { FILTER_DRAG_MIME } from './FilterRibbon';

const RIBBON_OPEN_KEY = 'zeus.filter.advancedPaneOpen';

export function FilterPanel() {
  const mode = useConnectionStore((s) => s.mode);
  const filterPresetName = useConnectionStore((s) => s.filterPresetName);
  const ribbonOpen = useConnectionStore((s) => s.filterAdvancedPaneOpen);
  const applyState = useConnectionStore((s) => s.applyState);
  const loadFavorites = useFilterFavoritesStore((s) => s.load);
  const updateFavorites = useFilterFavoritesStore((s) => s.update);
  const favoriteSlotNames = useFavoritesForMode(mode);

  const [presets, setPresets] = useState<FilterPresetDto[]>([]);
  const [dragOverIdx, setDragOverIdx] = useState<number | null>(null);

  useEffect(() => { void loadFavorites(mode); }, [mode, loadFavorites]);

  useEffect(() => {
    let cancelled = false;
    getFilterPresets(mode)
      .then((list) => { if (!cancelled) setPresets(list); })
      .catch(() => { if (!cancelled) setPresets([]); });
    return () => { cancelled = true; };
  }, [mode]);

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

  // Drop a preset chip from the ribbon onto favorite-index `idx`. Swaps if
  // the dropped slot is already a favorite; otherwise displaces idx.
  const onDrop = useCallback(
    (idx: number, slotName: string) => {
      const next = [...favoriteSlotNames];
      const existing = next.indexOf(slotName);
      if (existing === idx) return;
      const displaced = next[idx];
      if (existing >= 0 && displaced !== undefined) {
        next[existing] = displaced;
      }
      next[idx] = slotName;
      void updateFavorites(mode, next);
    },
    [favoriteSlotNames, mode, updateFavorites],
  );

  const onDragOver = (idx: number) => (e: React.DragEvent) => {
    if (!e.dataTransfer.types.includes(FILTER_DRAG_MIME)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
    if (dragOverIdx !== idx) setDragOverIdx(idx);
  };
  const onDragLeave = () => setDragOverIdx(null);
  const onDropEvt = (idx: number) => (e: React.DragEvent) => {
    const slotName = e.dataTransfer.getData(FILTER_DRAG_MIME);
    if (!slotName) return;
    e.preventDefault();
    onDrop(idx, slotName);
    setDragOverIdx(null);
  };

  if (mode === 'FM') return null;

  return (
    <div className="ctrl-group filter-bar" style={{ minWidth: 220 }}>
      <div className="label-xs ctrl-lbl">FILTER</div>
      <div className="btn-row" style={{ gap: 3 }}>
        {favoriteSlotNames.map((slotName, idx) => {
          const slot = presets.find((p) => p.slotName === slotName);
          const isActive = !!slot && activeSlot === slot.slotName;
          return (
            <button
              key={`fav-${idx}`}
              type="button"
              onClick={() => slot && selectPreset(slot)}
              onDragOver={onDragOver(idx)}
              onDragLeave={onDragLeave}
              onDrop={onDropEvt(idx)}
              className={`btn sm ${isActive ? 'active' : ''} ${dragOverIdx === idx ? 'is-drop-target' : ''}`}
              title={
                slot
                  ? `${slot.slotName}: ${slot.lowHz >= 0 ? '+' : ''}${slot.lowHz} / ${slot.highHz >= 0 ? '+' : ''}${slot.highHz} Hz — drag a preset here to replace`
                  : `Empty slot — drop a preset here`
              }
              aria-label={`Filter favorite ${idx + 1}: ${slot ? slot.label : slotName}`}
            >
              {slot ? slot.label : slotName}
            </button>
          );
        })}
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
