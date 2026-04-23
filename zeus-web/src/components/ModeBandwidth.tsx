// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { useCallback, useEffect, useState } from 'react';
import { setBandwidth, setMode, type RxMode } from '../api/client';
import { useConnectionStore } from '../state/connection-store';

type ModeEntry = { value: RxMode; label: string };

const MODES: readonly ModeEntry[] = [
  { value: 'LSB', label: 'LSB' },
  { value: 'USB', label: 'USB' },
  { value: 'CWL', label: 'CWL' },
  { value: 'CWU', label: 'CWU' },
  { value: 'AM', label: 'AM' },
  { value: 'SAM', label: 'SAM' },
  { value: 'DSB', label: 'DSB' },
  { value: 'FM', label: 'FM' },
  { value: 'DIGL', label: 'DIGL' },
  { value: 'DIGU', label: 'DIGU' },
];

type Preset = { label: string; low: number; high: number };

const CUSTOM_MIN = 0;
const CUSTOM_MAX = 10000;

// Per docs/prd/08-display-sync-and-sideband.md §4: sideband-aware presets.
// Upper-sideband modes are strictly positive; lower-sideband are negative;
// double-sideband/AM/FM are symmetric around zero; CW is a narrow symmetric
// pair around the tone pitch (tone handling is server-side).
function basePresetsFor(mode: RxMode): readonly Preset[] {
  switch (mode) {
    case 'USB':
    case 'DIGU':
      return [
        { label: 'Narrow 2.7k', low: 150, high: 2850 },
        { label: 'Wide 3.0k', low: 150, high: 3200 },
      ];
    case 'LSB':
    case 'DIGL':
      return [
        { label: 'Narrow 2.7k', low: -2850, high: -150 },
        { label: 'Wide 3.0k', low: -3200, high: -150 },
      ];
    case 'AM':
    case 'SAM':
    case 'DSB':
    case 'FM':
      return [
        { label: 'AM 6.6k', low: -3300, high: 3300 },
        { label: 'Wide 8.0k', low: -4000, high: 4000 },
      ];
    case 'CWL':
    case 'CWU':
      return [
        { label: 'CW 250', low: -125, high: 125 },
        { label: 'CW 500', low: -250, high: 250 },
      ];
  }
}

// Issue #39: 3k / 4k / 6k audio-domain presets. Pass-through from 0 Hz to N Hz
// on SSB; symmetric ±N Hz on DSB. Hidden for CW (250/500 Hz is canonical there).
function standardPresetsFor(mode: RxMode): readonly Preset[] {
  const widths = [3000, 4000, 6000] as const;
  switch (mode) {
    case 'USB':
    case 'DIGU':
      return widths.map((w) => ({ label: `${w / 1000}k`, low: 0, high: w }));
    case 'LSB':
    case 'DIGL':
      return widths.map((w) => ({ label: `${w / 1000}k`, low: -w, high: 0 }));
    case 'AM':
    case 'SAM':
    case 'DSB':
    case 'FM':
      return widths.map((w) => ({ label: `${w / 1000}k`, low: -w, high: w }));
    case 'CWL':
    case 'CWU':
      return [];
  }
}

function presetsFor(mode: RxMode): readonly Preset[] {
  return [...basePresetsFor(mode), ...standardPresetsFor(mode)];
}

// Double-sideband modes use a symmetric bandpass. The "Low" input is not
// meaningful there in v1 — the filter is always ±high around the carrier.
function isSymmetricMode(mode: RxMode): boolean {
  return mode === 'AM' || mode === 'SAM' || mode === 'DSB' || mode === 'FM';
}

// Convert a signed (low,high) stored pair into positive audio-domain values
// the user sees in the custom inputs.
function signedToAbs(mode: RxMode, low: number, high: number): { lowAbs: number; highAbs: number } {
  if (isSymmetricMode(mode)) {
    return { lowAbs: 0, highAbs: Math.max(Math.abs(low), Math.abs(high)) };
  }
  // SSB / CW: positive sideband → (low, high) already positive;
  // negative sideband → (low=-hi, high=-lo) so abs is (-high, -low).
  const lo = Math.min(Math.abs(low), Math.abs(high));
  const hi = Math.max(Math.abs(low), Math.abs(high));
  return { lowAbs: lo, highAbs: hi };
}

// Convert user-entered positive audio-domain values into the signed pair the
// backend expects for the current mode.
function absToSigned(mode: RxMode, lowAbs: number, highAbs: number): { low: number; high: number } {
  const lo = Math.max(CUSTOM_MIN, Math.min(CUSTOM_MAX, Math.round(lowAbs)));
  const hi = Math.max(CUSTOM_MIN, Math.min(CUSTOM_MAX, Math.round(highAbs)));
  const [lCap, hCap] = lo <= hi ? [lo, hi] : [hi, lo];
  switch (mode) {
    case 'USB':
    case 'DIGU':
    case 'CWU':
      return { low: lCap, high: hCap };
    case 'LSB':
    case 'DIGL':
    case 'CWL':
      return { low: -hCap, high: -lCap };
    case 'AM':
    case 'SAM':
    case 'DSB':
    case 'FM':
      return { low: -hCap, high: hCap };
  }
}

function isActive(p: Preset, low: number, high: number): boolean {
  return p.low === low && p.high === high;
}

export function ModeBandwidth() {
  const mode = useConnectionStore((s) => s.mode);
  const low = useConnectionStore((s) => s.filterLowHz);
  const high = useConnectionStore((s) => s.filterHighHz);
  const applyState = useConnectionStore((s) => s.applyState);

  // Draft state for the custom inputs so the user can type "3000" a character
  // at a time without us firing setBandwidth on every keystroke.
  const currentAbs = signedToAbs(mode, low, high);
  const [lowDraft, setLowDraft] = useState<string>(String(currentAbs.lowAbs));
  const [highDraft, setHighDraft] = useState<string>(String(currentAbs.highAbs));

  // Sync the drafts when the store-side filter changes (preset click, mode flip,
  // reconciliation from /api/state). We key the reset on the canonical pair so
  // local keystrokes don't fight the sync.
  useEffect(() => {
    const abs = signedToAbs(mode, low, high);
    setLowDraft(String(abs.lowAbs));
    setHighDraft(String(abs.highAbs));
  }, [mode, low, high]);

  const selectMode = useCallback(
    (m: RxMode) => {
      if (m === mode) return;
      useConnectionStore.setState({ mode: m });
      setMode(m)
        .then(applyState)
        .catch(() => {
          /* next state poll will reconcile */
        });
    },
    [mode, applyState],
  );

  const applyFilter = useCallback(
    (nextLow: number, nextHigh: number) => {
      if (nextLow === low && nextHigh === high) return;
      useConnectionStore.setState({ filterLowHz: nextLow, filterHighHz: nextHigh });
      setBandwidth(nextLow, nextHigh)
        .then(applyState)
        .catch(() => {
          /* next state poll will reconcile */
        });
    },
    [low, high, applyState],
  );

  const selectPreset = useCallback(
    (p: Preset) => {
      applyFilter(p.low, p.high);
    },
    [applyFilter],
  );

  const commitCustom = useCallback(() => {
    const loAbs = Number.parseInt(lowDraft, 10);
    const hiAbs = Number.parseInt(highDraft, 10);
    if (!Number.isFinite(loAbs) || !Number.isFinite(hiAbs)) return;
    const { low: nextLow, high: nextHigh } = absToSigned(mode, loAbs, hiAbs);
    applyFilter(nextLow, nextHigh);
  }, [lowDraft, highDraft, mode, applyFilter]);

  const onCustomKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter') {
      e.currentTarget.blur();
    }
  };

  const presets = presetsFor(mode);
  const lowDisabled = isSymmetricMode(mode);

  return (
    <>
      {/* Desktop: horizontal row of mode buttons */}
      <div className="ctrl-group hide-mobile">
        <div className="label-xs ctrl-lbl">MODE</div>
        <div className="btn-row wrap" style={{ width: 236 }}>
          {MODES.map((m) => (
            <button
              key={m.value}
              type="button"
              onClick={() => selectMode(m.value)}
              className={`btn sm ${mode === m.value ? 'active' : ''}`}
            >
              {m.label}
            </button>
          ))}
        </div>
      </div>

      {/* Mobile: dropdown for mode selection */}
      <div className="ctrl-group show-mobile" style={{ display: 'none' }}>
        <div className="label-xs ctrl-lbl">MODE</div>
        <select
          value={mode}
          onChange={(e) => selectMode(e.target.value as RxMode)}
          className="mode-select"
          style={{
            background: 'var(--btn-top)',
            color: 'var(--fg-0)',
            border: '1px solid var(--line)',
            borderRadius: 'var(--r-sm)',
            padding: '4px 8px',
            fontSize: '11px',
            fontWeight: 600,
            cursor: 'pointer',
          }}
        >
          {MODES.map((m) => (
            <option key={m.value} value={m.value}>
              {m.label}
            </option>
          ))}
        </select>
      </div>

      <div className="ctrl-group" style={{ minWidth: 260 }}>
        <div className="label-xs ctrl-lbl">BANDWIDTH</div>
        <div className="btn-row wrap" style={{ alignItems: 'center' }}>
          {presets.map((p) => (
            <button
              key={p.label}
              type="button"
              onClick={() => selectPreset(p)}
              className={`btn sm ${isActive(p, low, high) ? 'active' : ''}`}
            >
              {p.label}
            </button>
          ))}
        </div>
        <div className="btn-row" style={{ alignItems: 'center', marginTop: 4, gap: 4 }}>
          <span className="label-xs" style={{ color: 'var(--fg-3)' }}>CUSTOM</span>
          <input
            type="number"
            min={CUSTOM_MIN}
            max={CUSTOM_MAX}
            step={50}
            value={lowDraft}
            onChange={(e) => setLowDraft(e.currentTarget.value)}
            onBlur={commitCustom}
            onKeyDown={onCustomKeyDown}
            disabled={lowDisabled}
            aria-label="Custom filter low edge in Hz"
            className="mono"
            style={{
              width: 60,
              fontSize: 11,
              padding: '2px 4px',
              background: 'var(--btn-top)',
              color: lowDisabled ? 'var(--fg-3)' : 'var(--fg-0)',
              border: '1px solid var(--line)',
              borderRadius: 'var(--r-sm)',
            }}
          />
          <span className="label-xs" style={{ color: 'var(--fg-3)' }}>–</span>
          <input
            type="number"
            min={CUSTOM_MIN}
            max={CUSTOM_MAX}
            step={50}
            value={highDraft}
            onChange={(e) => setHighDraft(e.currentTarget.value)}
            onBlur={commitCustom}
            onKeyDown={onCustomKeyDown}
            aria-label="Custom filter high edge in Hz"
            className="mono"
            style={{
              width: 60,
              fontSize: 11,
              padding: '2px 4px',
              background: 'var(--btn-top)',
              color: 'var(--fg-0)',
              border: '1px solid var(--line)',
              borderRadius: 'var(--r-sm)',
            }}
          />
          <span className="label-xs mono" style={{ color: 'var(--fg-3)' }}>Hz</span>
          <span className="label-xs mono" style={{ marginLeft: 6, color: 'var(--fg-3)', whiteSpace: 'nowrap' }}>
            [{Math.min(Math.abs(low), Math.abs(high))}…{Math.max(Math.abs(low), Math.abs(high))}]
          </span>
        </div>
      </div>
    </>
  );
}
