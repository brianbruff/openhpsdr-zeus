// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Unified filter ribbon — the full panel opened via the ≡ button.
// Left column: BANDWIDTH / LOW CUT / PASSBAND / HIGH CUT readouts + mini-pan.
// Right column: all presets (F1–F10, VAR1/VAR2) with ★ star toggles to
// manage the three favorite quick-access slots shown in the toolbar.

import { useCallback, useEffect, useState } from 'react';
import { useConnectionStore } from '../../state/connection-store';
import { setFilter, setFilterAdvancedPaneOpen, getFilterPresets, type FilterPresetDto } from '../../api/client';
import {
  getPresetsForMode,
  formatAbsFreq,
  nudgeStepHz,
  FILTER_MAX_FAVORITES,
  type FilterPresetSlot,
} from './filterPresets';
import { useFavoriteFilters } from './useFavoriteFilters';
import { FilterMiniPan } from './FilterMiniPan';

const LOCAL_STORAGE_KEY = 'zeus.filter.advancedPaneOpen';

function cachePaneOpenLocal(open: boolean) {
  try { window.localStorage.setItem(LOCAL_STORAGE_KEY, open ? '1' : '0'); } catch { /* ok */ }
}

export function useFilterRibbonOpenSync() {
  useEffect(() => {
    try {
      const cached = window.localStorage.getItem(LOCAL_STORAGE_KEY);
      if (cached === '1') {
        useConnectionStore.setState({ filterAdvancedPaneOpen: true });
      }
    } catch { /* ok */ }
  }, []);
}

export function FilterRibbon({ embedded = false }: { embedded?: boolean } = {}) {
  const mode = useConnectionStore((s) => s.mode);
  const filterLow = useConnectionStore((s) => s.filterLowHz);
  const filterHigh = useConnectionStore((s) => s.filterHighHz);
  const filterPresetName = useConnectionStore((s) => s.filterPresetName);
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const open = useConnectionStore((s) => s.filterAdvancedPaneOpen);
  const applyState = useConnectionStore((s) => s.applyState);

  const lowAbs = vfoHz + filterLow;
  const highAbs = vfoHz + filterHigh;
  const widthKHz = Math.abs(filterHigh - filterLow) / 1000;

  // Fetch server VAR slot overrides
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

  const { favorites, toggleFavorite, atMax } = useFavoriteFilters(mode);

  const selectPreset = useCallback((slot: FilterPresetSlot) => {
    useConnectionStore.setState({
      filterLowHz: slot.lowHz,
      filterHighHz: slot.highHz,
      filterPresetName: slot.slotName,
    });
    setFilter(slot.lowHz, slot.highHz, slot.slotName)
      .then(applyState)
      .catch(() => {});
  }, [applyState]);

  const armCustom = useCallback(() => {
    useConnectionStore.setState({ filterPresetName: 'VAR1' });
    setFilter(filterLow, filterHigh, 'VAR1')
      .then(applyState)
      .catch(() => {});
  }, [applyState, filterLow, filterHigh]);

  const closeRibbon = useCallback(() => {
    useConnectionStore.setState({ filterAdvancedPaneOpen: false });
    cachePaneOpenLocal(false);
    setFilterAdvancedPaneOpen(false).catch(() => {});
  }, []);

  useEffect(() => {
    if (!embedded && !open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && !embedded) { closeRibbon(); return; }
      if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
      const step = nudgeStepHz(mode) * (e.shiftKey ? 10 : 1);
      const dir = e.key === 'ArrowRight' ? 1 : -1;
      const s = useConnectionStore.getState();
      const newHi = s.filterHighHz + dir * step;
      if (newHi <= s.filterLowHz + 50) return;
      const slot = s.filterPresetName && /^VAR[12]$/.test(s.filterPresetName) ? s.filterPresetName : 'VAR1';
      useConnectionStore.setState({ filterHighHz: newHi, filterPresetName: slot });
      setFilter(s.filterLowHz, newHi, slot).then(applyState).catch(() => {});
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [embedded, open, mode, applyState, closeRibbon]);

  if (!embedded && !open) return null;
  if (allPresets.length === 0) return null;

  return (
    <div
      className={`filter-ribbon ${embedded ? 'filter-ribbon--embedded' : ''}`}
      role="region"
      aria-label="Advanced filter ribbon"
    >
      {!embedded && (
        <button
          type="button"
          aria-label="Close filter ribbon"
          onClick={closeRibbon}
          className="filter-ribbon__close"
        >
          ×
        </button>
      )}

      <div className="filter-ribbon__body">
        {/* Left column: readout row + mini-pan + hint */}
        <div className="filter-ribbon__main">
          <div className="filter-ribbon__topRow">
            <div className="filter-ribbon__topCol filter-ribbon__topCol--bw">
              <div className="filter-ribbon__label">BANDWIDTH</div>
            </div>
            <div className="filter-ribbon__topCol filter-ribbon__topCol--lo">
              <div className="filter-ribbon__label">LOW CUT</div>
              <div className="filter-ribbon__freq">{formatAbsFreq(lowAbs)}</div>
            </div>
            <div className="filter-ribbon__topCol filter-ribbon__topCol--pb">
              <div className="filter-ribbon__label">PASSBAND</div>
              <div className="filter-ribbon__passband">
                <span className="filter-ribbon__passband-value">{widthKHz.toFixed(2)}</span>
                <span className="filter-ribbon__passband-unit">kHz</span>
              </div>
            </div>
            <div className="filter-ribbon__topCol filter-ribbon__topCol--hi">
              <div className="filter-ribbon__label">HIGH CUT</div>
              <div className="filter-ribbon__freq">{formatAbsFreq(highAbs)}</div>
            </div>
          </div>

          <div className="filter-ribbon__minipan">
            <FilterMiniPan />
          </div>

          <div className="filter-ribbon__hint">
            DRAG EDGES TO ADJUST&nbsp;&nbsp;·&nbsp;&nbsp;DRAG INSIDE TO MOVE
          </div>
        </div>

        {/* Right column: full preset list with star toggles */}
        <div className="filter-ribbon__presets">
          <div className="filter-ribbon__label filter-ribbon__label--icon">
            <span className="filter-ribbon__presets-icon">≡</span>
            <span>PRESETS</span>
            <span className="filter-ribbon__fav-count">
              {favorites.length}/{FILTER_MAX_FAVORITES} ★
            </span>
          </div>
          <div className="filter-ribbon__preset-list">
            {allPresets.map((slot) => {
              const active = filterPresetName === slot.slotName;
              const isFavorite = favorites.includes(slot.slotName);
              const canStar = isFavorite || !atMax;
              return (
                <div key={slot.slotName} className="filter-ribbon__preset-row">
                  <button
                    type="button"
                    onClick={() => selectPreset(slot)}
                    title={`${slot.slotName}: ${slot.lowHz}..${slot.highHz} Hz`}
                    className={`filter-ribbon__chip ${active ? 'is-active' : ''}`}
                  >
                    <span className="filter-ribbon__chip-name">{slot.slotName}</span>
                    <span className="filter-ribbon__chip-label">
                      {slot.slotName === 'VAR1' || slot.slotName === 'VAR2' ? slot.slotName : slot.label}
                    </span>
                  </button>
                  <button
                    type="button"
                    onClick={() => toggleFavorite(slot.slotName)}
                    disabled={!canStar}
                    className={`filter-ribbon__star ${isFavorite ? 'is-favorite' : ''}`}
                    title={isFavorite ? 'Remove from favorites' : atMax ? `Max ${FILTER_MAX_FAVORITES} favorites` : 'Add to favorites'}
                    aria-pressed={isFavorite}
                    aria-label={isFavorite ? `Unmark ${slot.slotName} as favorite` : `Mark ${slot.slotName} as favorite`}
                  >
                    {isFavorite ? '★' : '☆'}
                  </button>
                </div>
              );
            })}
          </div>
          <button
            type="button"
            onClick={armCustom}
            title="Arm custom edit — active slot becomes VAR1"
            className={`filter-ribbon__custom ${filterPresetName === 'VAR1' || filterPresetName === 'VAR2' ? 'is-active' : ''}`}
          >
            <span>CUSTOM</span>
            <span className="filter-ribbon__custom-icon" aria-hidden>✎</span>
          </button>
        </div>
      </div>
    </div>
  );
}
