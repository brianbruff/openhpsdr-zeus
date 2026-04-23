// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Filter visualization PRD §3.2 — advanced filter ribbon pane. Desktop-only.
// Shows BANDWIDTH label + LOW CUT / PASSBAND / HIGH CUT columns, a 10 kHz
// mini-panadapter centered on the VFO, and a 6-preset grid mapped to the
// active mode's F-slot table.

import { useCallback, useEffect } from 'react';
import { useConnectionStore } from '../../state/connection-store';
import { setFilter, setFilterAdvancedPaneOpen } from '../../api/client';
import {
  formatRibbonWidth,
  formatAbsFreq,
  getRibbonPresetsForMode,
  nudgeStepHz,
  type FilterPresetSlot,
} from './filterPresets';
import { FilterMiniPan } from './FilterMiniPan';

const LOCAL_STORAGE_KEY = 'zeus.filter.advancedPaneOpen';

// Write the pane-open flag to localStorage so the next page load reflects
// the operator's choice before the state poll arrives, avoiding a flash of
// closed → open on every reload.
function cachePaneOpenLocal(open: boolean) {
  try { window.localStorage.setItem(LOCAL_STORAGE_KEY, open ? '1' : '0'); } catch { /* quota/storage unavailable */ }
}

export function useFilterRibbonOpenSync() {
  // Read the cached flag once on mount and push it into the store so the
  // ribbon renders immediately on reload — the server reconciliation happens
  // in parallel via the state poll.
  useEffect(() => {
    try {
      const cached = window.localStorage.getItem(LOCAL_STORAGE_KEY);
      if (cached === '1') {
        useConnectionStore.setState({ filterAdvancedPaneOpen: true });
      }
    } catch { /* storage unavailable */ }
  }, []);
}

export function FilterRibbon() {
  const mode = useConnectionStore((s) => s.mode);
  const filterLow = useConnectionStore((s) => s.filterLowHz);
  const filterHigh = useConnectionStore((s) => s.filterHighHz);
  const filterPresetName = useConnectionStore((s) => s.filterPresetName);
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const open = useConnectionStore((s) => s.filterAdvancedPaneOpen);
  const applyState = useConnectionStore((s) => s.applyState);

  const presets = getRibbonPresetsForMode(mode);
  const widthLabel = formatRibbonWidth(filterLow, filterHigh);
  const lowAbs = vfoHz + filterLow;
  const highAbs = vfoHz + filterHigh;

  const selectPreset = useCallback((slot: FilterPresetSlot) => {
    useConnectionStore.setState({
      filterLowHz: slot.lowHz,
      filterHighHz: slot.highHz,
      filterPresetName: slot.slotName,
    });
    setFilter(slot.lowHz, slot.highHz, slot.slotName)
      .then(applyState)
      .catch(() => { /* next poll reconciles */ });
  }, [applyState]);

  const armCustom = useCallback(() => {
    // Flip active slot to VAR1 without changing passband — the user can then
    // nudge/drag from their current width.
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

  // Keyboard nudge: arrows move the most-recently-touched edge. Esc closes.
  // We default to nudging the Hi edge on the first keypress; drag-in-ribbon
  // remembers which edge the user last touched and overrides this.
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        closeRibbon();
        return;
      }
      if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
      const step = nudgeStepHz(mode) * (e.shiftKey ? 10 : 1);
      const dir = e.key === 'ArrowRight' ? 1 : -1;
      const s = useConnectionStore.getState();
      // Nudge Hi by default (matches mockup convention); Shift+Down not used.
      const newHi = s.filterHighHz + dir * step;
      if (newHi <= s.filterLowHz + 50) return;
      const slot = s.filterPresetName && /^VAR[12]$/.test(s.filterPresetName) ? s.filterPresetName : 'VAR1';
      useConnectionStore.setState({ filterHighHz: newHi, filterPresetName: slot });
      setFilter(s.filterLowHz, newHi, slot)
        .then(applyState)
        .catch(() => {});
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, mode, applyState, closeRibbon]);

  if (!open) return null;
  if (presets.length === 0) return null;  // FM: ribbon suppressed

  return (
    <div
      className="filter-ribbon"
      style={{
        position: 'relative',
        display: 'flex',
        flexDirection: 'column',
        gap: 4,
        padding: '8px 10px',
        border: '1px solid var(--line)',
        borderRadius: 4,
        background: 'rgba(0,0,0,0.35)',
        margin: '4px 8px',
      }}
    >
      {/* Close button */}
      <button
        type="button"
        aria-label="Close filter ribbon"
        onClick={closeRibbon}
        className="btn ghost sm"
        style={{
          position: 'absolute',
          top: 6,
          right: 6,
          padding: '0 8px',
          lineHeight: '20px',
        }}
      >
        ×
      </button>

      <div
        style={{
          display: 'grid',
          gridTemplateColumns: '90px 120px 1fr 120px minmax(240px, 1.2fr) 130px',
          gap: 10,
          alignItems: 'stretch',
          minHeight: 80,
        }}
      >
        {/* BANDWIDTH section label */}
        <div
          style={{
            display: 'flex',
            flexDirection: 'column',
            justifyContent: 'center',
            alignItems: 'flex-start',
          }}
        >
          <div
            className="label-xs"
            style={{ color: 'var(--accent)', fontWeight: 600, letterSpacing: 1, opacity: 0.7 }}
          >
            BANDWIDTH
          </div>
          <div className="label-xs" style={{ opacity: 0.6, marginTop: 4 }}>
            {filterPresetName ?? '—'}
          </div>
        </div>

        {/* LOW CUT */}
        <RibbonReadout title="LOW CUT" value={formatAbsFreq(lowAbs)} />

        {/* PASSBAND */}
        <RibbonReadout
          title="PASSBAND"
          value={widthLabel}
          focal
        />

        {/* HIGH CUT */}
        <RibbonReadout title="HIGH CUT" value={formatAbsFreq(highAbs)} />

        {/* Mini-panadapter */}
        <div
          style={{
            position: 'relative',
            border: '1px solid var(--line)',
            borderRadius: 3,
            minHeight: 80,
            overflow: 'hidden',
          }}
        >
          <FilterMiniPan />
        </div>

        {/* Preset grid */}
        <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
          <div
            className="label-xs"
            style={{ color: 'var(--accent)', fontWeight: 600, letterSpacing: 0.5, opacity: 0.7 }}
          >
            ≡ PRESETS
          </div>
          <div
            style={{
              display: 'grid',
              gridTemplateColumns: '1fr 1fr 1fr',
              gap: 3,
            }}
          >
            {presets.map((slot) => (
              <button
                key={slot.slotName}
                type="button"
                onClick={() => selectPreset(slot)}
                className={`btn sm ${filterPresetName === slot.slotName ? 'active' : ''}`}
                title={`${slot.slotName}: ${slot.lowHz}/${slot.highHz} Hz`}
                style={{ padding: '2px 4px', fontSize: 11 }}
              >
                {slot.label}
              </button>
            ))}
          </div>
          <button
            type="button"
            onClick={armCustom}
            className={`btn sm ${filterPresetName === 'VAR1' || filterPresetName === 'VAR2' ? 'active' : ''}`}
            title="Arm custom edit — sends current passband into VAR1"
            style={{ padding: '2px 4px', fontSize: 11 }}
          >
            ✎ CUSTOM
          </button>
        </div>
      </div>

      <div
        className="label-xs"
        style={{
          opacity: 0.45,
          letterSpacing: 0.6,
          marginTop: 2,
          textAlign: 'center',
        }}
      >
        DRAG EDGES TO ADJUST · DRAG INSIDE TO MOVE
      </div>
    </div>
  );
}

function RibbonReadout({ title, value, focal = false }: { title: string; value: string; focal?: boolean }) {
  return (
    <div
      style={{
        display: 'flex',
        flexDirection: 'column',
        justifyContent: 'center',
        alignItems: focal ? 'center' : 'flex-start',
      }}
    >
      <div
        className="label-xs"
        style={{ color: 'var(--accent)', fontWeight: 600, letterSpacing: 1, opacity: 0.7 }}
      >
        {title}
      </div>
      <div
        className="mono"
        style={{
          color: 'var(--accent)',
          fontWeight: focal ? 700 : 500,
          fontSize: focal ? 20 : 13,
          letterSpacing: 0.4,
        }}
      >
        {value}
      </div>
    </div>
  );
}
