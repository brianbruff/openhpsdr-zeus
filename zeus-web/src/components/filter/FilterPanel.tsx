// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Filter visualization PRD §3.1 Phase 1 — compact filter panel.
// Renders a width readout and a chip row (F1..F10 + VAR1/VAR2) for the
// current mode. Clicking a chip calls POST /api/filter with the slot's
// Lo/Hi and preset name. Phase 1: no drag handles, no Lo/Hi nudge
// controls, no advanced toggle button, no out-of-band colouring.

import { useCallback, useEffect, useState } from 'react';
import { useConnectionStore } from '../../state/connection-store';
import { setFilter, getFilterPresets, setFilterAdvancedPaneOpen, type FilterPresetDto } from '../../api/client';
import { getPresetsForMode, formatFilterWidth, type FilterPresetSlot } from './filterPresets';

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

  // Per-mode VAR1/VAR2 overrides fetched from the server. Seeded on mount
  // and after any VAR* write. Falls back to the local Thetis-default table
  // while the fetch is in flight or when the server is unreachable.
  const [serverPresets, setServerPresets] = useState<FilterPresetDto[] | null>(null);

  useEffect(() => {
    let cancelled = false;
    getFilterPresets(mode)
      .then((presets) => { if (!cancelled) setServerPresets(presets); })
      .catch(() => { /* server presets unavailable; fall back to local defaults */ });
    return () => { cancelled = true; };
  }, [mode]);

  // Merge server overrides for VAR slots into the local preset table. Server
  // VAR* overrides take precedence; fixed slots are always from the local table.
  const presets: readonly FilterPresetSlot[] = (() => {
    const local = getPresetsForMode(mode);
    if (!serverPresets) return local;
    return local.map((slot) => {
      if (!slot.isVar) return slot;
      const srv = serverPresets.find((s) => s.slotName === slot.slotName);
      return srv ? { ...slot, lowHz: srv.lowHz, highHz: srv.highHz } : slot;
    });
  })();

  const activeSlot = filterPresetName ?? null;
  const widthLabel = formatFilterWidth(filterLow, filterHigh);

  const selectPreset = useCallback(
    (slot: FilterPresetSlot) => {
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

  // FM has no presets — hide chip row.
  if (presets.length === 0) return null;

  return (
    <div className="ctrl-group" style={{ minWidth: 320 }}>
      <div
        className="label-xs ctrl-lbl"
        style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}
      >
        <span>FILTER</span>
        <span
          className="mono"
          style={{ color: 'var(--accent)', fontWeight: 600, fontSize: 11, letterSpacing: 0.5 }}
        >
          {widthLabel}
        </span>
      </div>
      <div className="btn-row wrap" style={{ gap: 3 }}>
        {presets.map((slot) => (
          <button
            key={slot.slotName}
            type="button"
            onClick={() => selectPreset(slot)}
            className={`btn sm ${activeSlot === slot.slotName ? 'active' : ''}`}
            title={`${slot.slotName}: ${slot.lowHz >= 0 ? '+' : ''}${slot.lowHz} / +${slot.highHz} Hz`}
          >
            {slot.slotName === 'VAR1' || slot.slotName === 'VAR2'
              ? slot.slotName
              : slot.label}
          </button>
        ))}
        <button
          type="button"
          onClick={toggleAdvanced}
          className={`btn sm hide-mobile ${advancedOpen ? 'active' : ''}`}
          title={advancedOpen ? 'Close advanced filter ribbon' : 'Open advanced filter ribbon'}
          aria-pressed={advancedOpen}
          style={{ marginLeft: 4 }}
        >
          {advancedOpen ? '≡ ×' : '≡'}
        </button>
      </div>
    </div>
  );
}
