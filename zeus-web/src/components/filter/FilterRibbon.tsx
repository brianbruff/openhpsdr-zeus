// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Filter visualization PRD §3.2 — advanced filter ribbon. Matches the
// mockup at docs/pics/filterpanel_mockup.png: dark-chrome floating panel
// with BANDWIDTH / LOW CUT / PASSBAND / HIGH CUT columns, a 10 kHz
// mini-panadapter, a 3×2 preset-bandwidth grid plus CUSTOM, close (×).
//
// Rendered via React portal into document.body so the ribbon is purely
// additive — it does not participate in the main-app CSS grid and cannot
// disturb the existing panadapter / waterfall / workspace layout.

import { useCallback, useEffect } from 'react';
import { createPortal } from 'react-dom';
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

export function FilterRibbon() {
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
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') { closeRibbon(); return; }
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
  }, [open, mode, applyState, closeRibbon]);

  if (!open) return null;
  if (presets.length === 0) return null;

  // Mockup-matching palette. var(--accent) is the Zeus blue (#4a9eff).
  const labelStyle: React.CSSProperties = {
    fontSize: 10,
    fontWeight: 500,
    letterSpacing: 1.6,
    color: '#7c9fc9',
    textTransform: 'uppercase',
  };
  const freqStyle: React.CSSProperties = {
    fontSize: 22,
    fontWeight: 400,
    color: '#e8edf5',
    letterSpacing: 0.6,
    marginTop: 4,
    fontVariantNumeric: 'tabular-nums',
  };

  return createPortal(
    <div
      className="filter-ribbon"
      role="region"
      aria-label="Advanced filter ribbon"
      style={{
        position: 'fixed',
        top: 140,
        left: 16,
        right: 16,
        zIndex: 250,
        padding: '12px 16px 14px',
        borderRadius: 8,
        background: '#0b1017',
        backgroundColor: '#0b1017',
        border: '1px solid #1a2230',
        boxShadow: '0 10px 30px rgba(0, 0, 0, 0.55)',
        color: '#e8edf5',
        fontFamily: 'inherit',
        isolation: 'isolate',
      }}
    >
      {/* Close × (top-right) */}
      <button
        type="button"
        aria-label="Close filter ribbon"
        onClick={closeRibbon}
        style={{
          position: 'absolute',
          top: 8,
          right: 10,
          width: 24,
          height: 24,
          padding: 0,
          background: 'transparent',
          border: 'none',
          color: '#7c9fc9',
          cursor: 'pointer',
          fontSize: 18,
          lineHeight: 1,
        }}
      >
        ×
      </button>

      <div
        style={{
          display: 'grid',
          gridTemplateColumns: '90px 130px 1fr 130px minmax(320px, 1.2fr) 210px',
          columnGap: 20,
          alignItems: 'center',
          minHeight: 140,
        }}
      >
        {/* BANDWIDTH header column */}
        <div style={{ alignSelf: 'center' }}>
          <div style={labelStyle}>BANDWIDTH</div>
        </div>

        {/* LOW CUT */}
        <div style={{ alignSelf: 'center' }}>
          <div style={labelStyle}>LOW CUT</div>
          <div style={freqStyle}>{formatAbsFreq(lowAbs)}</div>
        </div>

        {/* PASSBAND — focal element */}
        <div style={{ textAlign: 'center', alignSelf: 'center' }}>
          <div style={labelStyle}>PASSBAND</div>
          <div
            style={{
              marginTop: 2,
              fontVariantNumeric: 'tabular-nums',
              letterSpacing: 0.5,
              color: '#ffffff',
            }}
          >
            <span style={{ fontSize: 30, fontWeight: 500 }}>
              {widthKHz.toFixed(2)}
            </span>
            <span style={{ fontSize: 13, marginLeft: 6, color: '#a9b9d3', fontWeight: 400 }}>
              kHz
            </span>
          </div>
        </div>

        {/* HIGH CUT */}
        <div style={{ textAlign: 'right', alignSelf: 'center' }}>
          <div style={labelStyle}>HIGH CUT</div>
          <div style={freqStyle}>{formatAbsFreq(highAbs)}</div>
        </div>

        {/* Mini-panadapter — fixed height so the preset column beside it has
            enough room without stretching. */}
        <div style={{ position: 'relative', alignSelf: 'center', height: 120 }}>
          <FilterMiniPan />
        </div>

        {/* PRESET BANDWIDTHS column */}
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6, alignSelf: 'center' }}>
          <div style={{ ...labelStyle, display: 'flex', alignItems: 'center', gap: 6 }}>
            <span style={{ opacity: 0.7 }}>≡</span>
            <span>PRESET BANDWIDTHS</span>
          </div>
          <div
            style={{
              display: 'grid',
              gridTemplateColumns: '1fr 1fr 1fr',
              gap: 6,
            }}
          >
            {presets.map((slot) => {
              const active = filterPresetName === slot.slotName;
              const widthK = Math.abs(slot.highHz - slot.lowHz) / 1000;
              return (
                <button
                  key={slot.slotName}
                  type="button"
                  onClick={() => selectPreset(slot)}
                  title={`${slot.slotName}: ${slot.lowHz}/${slot.highHz} Hz`}
                  style={{
                    padding: '6px 8px',
                    fontSize: 11,
                    fontWeight: 500,
                    borderRadius: 4,
                    border: `1px solid ${active ? '#4a9eff' : '#223046'}`,
                    background: active ? 'rgba(74, 158, 255, 0.14)' : 'rgba(18, 26, 40, 0.8)',
                    color: active ? '#ffffff' : '#a9b9d3',
                    cursor: 'pointer',
                    fontVariantNumeric: 'tabular-nums',
                    letterSpacing: 0.4,
                  }}
                >
                  {widthK % 1 === 0 ? `${widthK.toFixed(1)} kHz` : `${widthK.toFixed(1)} kHz`}
                </button>
              );
            })}
          </div>
          <button
            type="button"
            onClick={armCustom}
            title="Arm custom edit — active slot becomes VAR1"
            style={{
              padding: '6px 8px',
              fontSize: 11,
              fontWeight: 500,
              letterSpacing: 1.4,
              borderRadius: 4,
              border: `1px solid ${filterPresetName === 'VAR1' || filterPresetName === 'VAR2' ? '#4a9eff' : '#223046'}`,
              background: 'rgba(18, 26, 40, 0.8)',
              color: filterPresetName === 'VAR1' || filterPresetName === 'VAR2' ? '#ffffff' : '#a9b9d3',
              cursor: 'pointer',
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
              gap: 6,
            }}
          >
            <span>CUSTOM</span>
            <span style={{ opacity: 0.6 }}>✎</span>
          </button>
        </div>
      </div>

      {/* Footer hint — centered, uppercase, muted */}
      <div
        style={{
          marginTop: 6,
          textAlign: 'center',
          fontSize: 9.5,
          letterSpacing: 1.6,
          color: '#5a7598',
          textTransform: 'uppercase',
        }}
      >
        DRAG EDGES TO ADJUST&nbsp;&nbsp;•&nbsp;&nbsp;DRAG INSIDE TO MOVE
      </div>
    </div>,
    document.body,
  );
}
