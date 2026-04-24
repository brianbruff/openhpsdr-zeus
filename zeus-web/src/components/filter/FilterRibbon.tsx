// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Filter visualization PRD §3.2 — advanced filter ribbon. Matches the
// updated mockup (docs/pics/filterpanel_mockup.png):
//
//   ┌────────────────────────────────────────────────────────────┐
//   │ BANDWIDTH │ LOW CUT │ PASSBAND │ HIGH CUT │  ≡ PRESETS  ×  │
//   │           │  freq   │  2.70 kHz│  freq    │  2.4  2.7  3.6 │
//   │ ───────────────────────────────────────    │  6.0  9.0 12.0 │
//   │ │  spectrum trace with passband box    │   │  [  CUSTOM  ✎ ]│
//   │ └──────────────────────────────────────┘   │                │
//   │  14.249 14.251 14.253 14.255 14.257 …      │                │
//   │      DRAG EDGES TO ADJUST · DRAG INSIDE    │                │
//   └────────────────────────────────────────────────────────────┘
//
// The ribbon lives as a dedicated workspace row above the hero — same
// column width as the panadapter. Preset column is a fixed-width right
// rail; everything else (readouts + mini-pan + hint) stacks vertically
// on the left so the mini-pan gets the full spectrum-strip width.

import { useCallback, useEffect } from 'react';
import { useConnectionStore } from '../../state/connection-store';
import { setFilter, setFilterAdvancedPaneOpen } from '../../api/client';
import {
  formatAbsFreq,
  getRibbonPresetsForMode,
  nudgeStepHz,
  type FilterPresetSlot,
} from './filterPresets';
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

  const presets = getRibbonPresetsForMode(mode);
  const lowAbs = vfoHz + filterLow;
  const highAbs = vfoHz + filterHigh;
  const widthKHz = Math.abs(filterHigh - filterLow) / 1000;

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
  if (presets.length === 0) return null;

  const currentWidth = Math.abs(filterHigh - filterLow);

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

        {/* Right column: presets rail, full ribbon height */}
        <div className="filter-ribbon__presets">
          <div className="filter-ribbon__label filter-ribbon__label--icon">
            <span className="filter-ribbon__presets-icon">≡</span>
            <span>PRESET BANDWIDTHS</span>
          </div>
          <div className="filter-ribbon__preset-grid">
            {presets.map((slot) => {
              const slotWidth = Math.abs(slot.highHz - slot.lowHz);
              const active = Math.abs(slotWidth - currentWidth) <= 20;
              const widthK = slotWidth / 1000;
              return (
                <button
                  key={slot.slotName}
                  type="button"
                  onClick={() => selectPreset(slot)}
                  title={`${widthK.toFixed(1)} kHz (${slot.lowHz}..${slot.highHz} Hz)`}
                  className={`filter-ribbon__chip ${active ? 'is-active' : ''}`}
                >
                  {widthK.toFixed(1)} kHz
                </button>
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
