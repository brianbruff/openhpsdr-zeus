// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Unified compact filter bar — always visible in the control strip.
// Shows the filter readout (LOW CUT / WIDTH / HIGH CUT), the ≡ button to
// open/close the full filter panel, and the three favorite preset quick-access
// buttons. Favorites are managed from within the ribbon (star buttons) and
// default to 2.7 / 2.9 / 3.3 kHz for SSB modes.

import { useCallback, useEffect, useState } from 'react';
import { useConnectionStore } from '../../state/connection-store';
import { setFilter, getFilterPresets, setFilterAdvancedPaneOpen, type FilterPresetDto } from '../../api/client';
import { getPresetsForMode, formatFilterWidth, formatCutOffset, type FilterPresetSlot } from './filterPresets';
import { useFavoriteFilters } from './useFavoriteFilters';

const LOCAL_STORAGE_KEY = 'zeus.filter.advancedPaneOpen';

export function FilterPanel() {
  const mode = useConnectionStore((s) => s.mode);
  const filterLow = useConnectionStore((s) => s.filterLowHz);
  const filterHigh = useConnectionStore((s) => s.filterHighHz);
  const filterPresetName = useConnectionStore((s) => s.filterPresetName);
  const advancedOpen = useConnectionStore((s) => s.filterAdvancedPaneOpen);
  const applyState = useConnectionStore((s) => s.applyState);

  const toggleAdvanced = useCallback(() => {
    const next = !advancedOpen;
    useConnectionStore.setState({ filterAdvancedPaneOpen: next });
    try { window.localStorage.setItem(LOCAL_STORAGE_KEY, next ? '1' : '0'); } catch { /* ok */ }
    setFilterAdvancedPaneOpen(next).catch(() => {});
  }, [advancedOpen]);

  const [serverPresets, setServerPresets] = useState<FilterPresetDto[] | null>(null);
  useEffect(() => {
    let cancelled = false;
    getFilterPresets(mode)
      .then((presets) => { if (!cancelled) setServerPresets(presets); })
      .catch(() => {});
    return () => { cancelled = true; };
  }, [mode]);

  const allPresets: readonly FilterPresetSlot[] = (() => {
    const local = getPresetsForMode(mode);
    if (!serverPresets) return local;
    return local.map((slot) => {
      if (!slot.isVar) return slot;
      const srv = serverPresets.find((s) => s.slotName === slot.slotName);
      return srv ? { ...slot, lowHz: srv.lowHz, highHz: srv.highHz } : slot;
    });
  })();

  const { favorites } = useFavoriteFilters(mode);

  const activeSlot = filterPresetName ?? null;
  const widthLabel = formatFilterWidth(filterLow, filterHigh);
  const hasPresets = allPresets.length > 0;

  const favoriteSlots = favorites
    .map((name) => allPresets.find((s) => s.slotName === name))
    .filter((s): s is FilterPresetSlot => s != null);

  const selectPreset = useCallback(
    (slot: FilterPresetSlot) => {
      useConnectionStore.setState({
        filterLowHz: slot.lowHz,
        filterHighHz: slot.highHz,
        filterPresetName: slot.slotName,
      });
      setFilter(slot.lowHz, slot.highHz, slot.slotName)
        .then(applyState)
        .catch(() => {});
    },
    [applyState],
  );

  return (
    <div className="ctrl-group filter-bar" style={{ minWidth: 280 }}>
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
      <div className="btn-row" style={{ gap: 4 }}>
        <button
          type="button"
          onClick={toggleAdvanced}
          disabled={!hasPresets}
          className={`btn sm hide-mobile ${advancedOpen ? 'active' : ''}`}
          title={advancedOpen ? 'Close filter panel' : 'Open filter panel'}
          aria-pressed={advancedOpen}
        >
          {advancedOpen ? '≡ ×' : '≡'}
        </button>
        {favoriteSlots.map((slot) => (
          <button
            key={slot.slotName}
            type="button"
            onClick={() => selectPreset(slot)}
            className={`btn sm ${activeSlot === slot.slotName ? 'active' : ''}`}
            title={`${slot.slotName}: ${formatFilterWidth(slot.lowHz, slot.highHz)}`}
          >
            {slot.label}
          </button>
        ))}
      </div>
    </div>
  );
}
