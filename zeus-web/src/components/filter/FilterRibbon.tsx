// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Filter ribbon — drops down above the panadapter when toggled. Right rail
// holds three sections inside the existing card width:
//
//   FAVORITES — 3 drop-target chips, always populated. Drag any preset (or
//               VAR1) onto a slot to swap.
//   PRESETS   — F1..F10 + VAR1 + VAR2 from the Thetis-default preset table
//               (with server-side VAR overrides applied). Each chip is
//               draggable into a favorite slot.
//   CUSTOM    — Lo/Hi Hz inputs that arm VAR1 and persist its stored width;
//               drag the VAR1 chip into a favorite to pin a custom width.
//
// Left side (top readouts row, mini-pan, hint) is unchanged.
//
// Layout invariant: the ribbon's vertical footprint is set by the mini-pan
// height. The right rail must fit inside that height — chips are deliberately
// compact (4×3 grid, ~22px tall) so the full preset table + favorites + custom
// row all fit without forcing the ribbon to grow.

import { useCallback, useEffect, useMemo, useState } from 'react';
import { useConnectionStore } from '../../state/connection-store';
import {
  setFilter,
  setFilterAdvancedPaneOpen,
  setFilterPresetOverride,
  getFilterPresets,
  type FilterPresetDto,
  type RxMode,
} from '../../api/client';
import {
  formatAbsFreq,
  getPresetsForMode,
  nudgeStepHz,
  type FilterPresetSlot,
} from './filterPresets';
import { FilterMiniPan } from './FilterMiniPan';
import { useFilterFavoritesStore, useFavoritesForMode } from '../../state/filter-favorites-store';

const LOCAL_STORAGE_KEY = 'zeus.filter.advancedPaneOpen';
const DRAG_MIME = 'application/x-zeus-filter-slot';

const CUSTOM_MIN = 0;
const CUSTOM_MAX = 10000;

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

function isSymmetricMode(mode: RxMode): boolean {
  return mode === 'AM' || mode === 'SAM' || mode === 'DSB' || mode === 'FM';
}

function signedToAbs(mode: RxMode, low: number, high: number): { lo: number; hi: number } {
  if (isSymmetricMode(mode)) {
    return { lo: 0, hi: Math.max(Math.abs(low), Math.abs(high)) };
  }
  return {
    lo: Math.min(Math.abs(low), Math.abs(high)),
    hi: Math.max(Math.abs(low), Math.abs(high)),
  };
}

function absToSigned(mode: RxMode, loAbs: number, hiAbs: number): { low: number; high: number } {
  const lo = Math.max(CUSTOM_MIN, Math.min(CUSTOM_MAX, Math.round(loAbs)));
  const hi = Math.max(CUSTOM_MIN, Math.min(CUSTOM_MAX, Math.round(hiAbs)));
  const [lCap, hCap] = lo <= hi ? [lo, hi] : [hi, lo];
  switch (mode) {
    case 'USB': case 'DIGU': case 'CWU':
      return { low: lCap, high: hCap };
    case 'LSB': case 'DIGL': case 'CWL':
      return { low: -hCap, high: -lCap };
    case 'AM': case 'SAM': case 'DSB': case 'FM':
      return { low: -hCap, high: hCap };
  }
}

// Merge server VAR overrides on top of the local Thetis-default preset table.
function mergePresets(mode: RxMode, server: FilterPresetDto[] | null): FilterPresetSlot[] {
  const local = getPresetsForMode(mode);
  if (!server) return local.slice();
  return local.map((slot) => {
    if (!slot.isVar) return slot;
    const srv = server.find((s) => s.slotName === slot.slotName);
    return srv ? { ...slot, lowHz: srv.lowHz, highHz: srv.highHz } : slot;
  });
}

export function FilterRibbon({ embedded = false }: { embedded?: boolean } = {}) {
  const mode = useConnectionStore((s) => s.mode);
  const filterLow = useConnectionStore((s) => s.filterLowHz);
  const filterHigh = useConnectionStore((s) => s.filterHighHz);
  const filterPresetName = useConnectionStore((s) => s.filterPresetName);
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const open = useConnectionStore((s) => s.filterAdvancedPaneOpen);
  const applyState = useConnectionStore((s) => s.applyState);
  const loadFavorites = useFilterFavoritesStore((s) => s.load);
  const updateFavorites = useFilterFavoritesStore((s) => s.update);
  const favoriteSlotNames = useFavoritesForMode(mode);

  const [serverPresets, setServerPresets] = useState<FilterPresetDto[] | null>(null);
  const [dragSlot, setDragSlot] = useState<string | null>(null);
  const [dragOverFav, setDragOverFav] = useState<number | null>(null);

  useEffect(() => { void loadFavorites(mode); }, [mode, loadFavorites]);

  useEffect(() => {
    let cancelled = false;
    getFilterPresets(mode)
      .then((list) => { if (!cancelled) setServerPresets(list); })
      .catch(() => { /* fall back to local table */ });
    return () => { cancelled = true; };
  }, [mode]);

  const presets = useMemo(() => mergePresets(mode, serverPresets), [mode, serverPresets]);
  const lowAbs = vfoHz + filterLow;
  const highAbs = vfoHz + filterHigh;
  const widthKHz = Math.abs(filterHigh - filterLow) / 1000;

  const favoritePresets: (FilterPresetSlot | undefined)[] = favoriteSlotNames.map((name) =>
    presets.find((p) => p.slotName === name),
  );

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

  const closeRibbon = useCallback(() => {
    useConnectionStore.setState({ filterAdvancedPaneOpen: false });
    cachePaneOpenLocal(false);
    setFilterAdvancedPaneOpen(false).catch(() => {});
  }, []);

  // Drop a slot onto favorite-index `idx`. Swaps if dropped slot already
  // exists in favorites; otherwise displaces the slot at idx.
  const onDropFavorite = useCallback((idx: number, slotName: string) => {
    const next = [...favoriteSlotNames];
    const existingIdx = next.indexOf(slotName);
    if (existingIdx === idx) return;
    const displaced = next[idx];
    if (existingIdx >= 0 && displaced !== undefined) {
      next[existingIdx] = displaced;
    }
    next[idx] = slotName;
    void updateFavorites(mode, next);
  }, [favoriteSlotNames, mode, updateFavorites]);

  // CUSTOM Lo/Hi inputs — armed against VAR1.
  const var1 = presets.find((p) => p.slotName === 'VAR1');
  const var1Active = filterPresetName === 'VAR1';
  const seedLow = var1Active ? filterLow : (var1?.lowHz ?? filterLow);
  const seedHigh = var1Active ? filterHigh : (var1?.highHz ?? filterHigh);
  const seedAbs = signedToAbs(mode, seedLow, seedHigh);
  const [loDraft, setLoDraft] = useState<string>(String(seedAbs.lo));
  const [hiDraft, setHiDraft] = useState<string>(String(seedAbs.hi));

  // Reseed the drafts when the source values change (mode flip, VAR1 override
  // edit elsewhere, server reconciliation).
  useEffect(() => {
    const abs = signedToAbs(mode, seedLow, seedHigh);
    setLoDraft(String(abs.lo));
    setHiDraft(String(abs.hi));
  }, [mode, seedLow, seedHigh]);

  const commitCustom = useCallback(async () => {
    const loAbs = Number.parseInt(loDraft, 10);
    const hiAbs = Number.parseInt(hiDraft, 10);
    if (!Number.isFinite(loAbs) || !Number.isFinite(hiAbs)) return;
    const { low, high } = absToSigned(mode, loAbs, hiAbs);
    if (high <= low + 50) return;
    useConnectionStore.setState({
      filterLowHz: low,
      filterHighHz: high,
      filterPresetName: 'VAR1',
    });
    try {
      await setFilter(low, high, 'VAR1').then(applyState);
      await setFilterPresetOverride(mode, 'VAR1', low, high);
      // Refresh preset list so VAR1 chip shows the new values.
      const fresh = await getFilterPresets(mode);
      setServerPresets(fresh);
    } catch { /* next state poll reconciles */ }
  }, [loDraft, hiDraft, mode, applyState]);

  const onCustomKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter') e.currentTarget.blur();
  };

  // Keyboard arrow nudging — only when ribbon is open and we're not focused
  // on the CUSTOM inputs.
  useEffect(() => {
    if (!embedded && !open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.target instanceof HTMLInputElement) return;
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
  if (presets.length === 0) return null;

  const currentWidth = Math.abs(filterHigh - filterLow);
  const isLowDisabled = isSymmetricMode(mode);

  const startDrag = (e: React.DragEvent, slotName: string) => {
    e.dataTransfer.setData(DRAG_MIME, slotName);
    e.dataTransfer.effectAllowed = 'move';
    setDragSlot(slotName);
  };
  const endDrag = () => { setDragSlot(null); setDragOverFav(null); };

  const onFavDragOver = (idx: number) => (e: React.DragEvent) => {
    if (!e.dataTransfer.types.includes(DRAG_MIME)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
    if (dragOverFav !== idx) setDragOverFav(idx);
  };
  const onFavDragLeave = () => setDragOverFav(null);
  const onFavDrop = (idx: number) => (e: React.DragEvent) => {
    const slotName = e.dataTransfer.getData(DRAG_MIME);
    if (!slotName) return;
    e.preventDefault();
    onDropFavorite(idx, slotName);
    setDragOverFav(null);
    setDragSlot(null);
  };

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
        {/* Left column: top readout row, full-width mini-pan, footer hint */}
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

        {/* Right column: favorites + presets + custom */}
        <div className="filter-ribbon__presets">
          <div className="filter-ribbon__section-label">FAVORITES</div>
          <div className="filter-ribbon__favorites">
            {favoritePresets.map((slot, idx) => {
              const slotName = favoriteSlotNames[idx];
              const isActive = slot && filterPresetName === slot.slotName;
              const slotWidth = slot ? Math.abs(slot.highHz - slot.lowHz) : -1;
              const widthMatches = slot && Math.abs(slotWidth - currentWidth) <= 20;
              return (
                <button
                  key={`fav-${idx}`}
                  type="button"
                  onClick={() => slot && selectPreset(slot)}
                  onDragOver={onFavDragOver(idx)}
                  onDragLeave={onFavDragLeave}
                  onDrop={onFavDrop(idx)}
                  className={`filter-ribbon__chip filter-ribbon__chip--fav ${isActive || widthMatches ? 'is-active' : ''} ${dragOverFav === idx ? 'is-drop-target' : ''}`}
                  title={slot ? `${slot.slotName}: drag a preset here to replace` : `Empty slot — drop a preset here`}
                  aria-label={`Favorite slot ${idx + 1}: ${slot ? slot.label : slotName}`}
                >
                  {slot ? slot.label : slotName}
                </button>
              );
            })}
          </div>

          <div className="filter-ribbon__section-label">PRESETS</div>
          <div className="filter-ribbon__preset-grid">
            {presets.map((slot) => {
              const slotWidth = Math.abs(slot.highHz - slot.lowHz);
              const isActive = filterPresetName === slot.slotName
                || (Math.abs(slotWidth - currentWidth) <= 20 && !slot.isVar);
              const label = slot.isVar ? slot.slotName : slot.label;
              return (
                <button
                  key={slot.slotName}
                  type="button"
                  draggable
                  onDragStart={(e) => startDrag(e, slot.slotName)}
                  onDragEnd={endDrag}
                  onClick={() => selectPreset(slot)}
                  title={`${slot.slotName}: ${slot.lowHz >= 0 ? '+' : ''}${slot.lowHz} / ${slot.highHz >= 0 ? '+' : ''}${slot.highHz} Hz · drag onto a favorite to pin`}
                  className={`filter-ribbon__chip ${isActive ? 'is-active' : ''} ${dragSlot === slot.slotName ? 'is-dragging' : ''}`}
                >
                  {label}
                </button>
              );
            })}
          </div>

          <div className="filter-ribbon__section-label">CUSTOM</div>
          <div className="filter-ribbon__custom-row">
            <input
              type="number"
              min={CUSTOM_MIN}
              max={CUSTOM_MAX}
              step={50}
              value={loDraft}
              onChange={(e) => setLoDraft(e.currentTarget.value)}
              onBlur={commitCustom}
              onKeyDown={onCustomKeyDown}
              disabled={isLowDisabled}
              aria-label="Custom filter low edge in Hz"
              className="filter-ribbon__custom-input mono"
            />
            <span className="filter-ribbon__custom-sep">–</span>
            <input
              type="number"
              min={CUSTOM_MIN}
              max={CUSTOM_MAX}
              step={50}
              value={hiDraft}
              onChange={(e) => setHiDraft(e.currentTarget.value)}
              onBlur={commitCustom}
              onKeyDown={onCustomKeyDown}
              aria-label="Custom filter high edge in Hz"
              className="filter-ribbon__custom-input mono"
            />
            <span className="filter-ribbon__custom-unit">Hz</span>
          </div>
        </div>
      </div>
    </div>
  );
}
