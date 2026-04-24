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

import { create } from 'zustand';
import type { ColormapId } from '../gl/colormap';

// Fixed defaults used when autoRange is off and no user-saved range is
// present. -140..-50 dBFS sits the noise floor where operators expect to
// read it (bottom of the left-hand scale near ~140 dB), matching Thetis's
// out-of-box panadapter feel. A user's drag-shift is persisted to
// localStorage and takes over on reload — see `shiftDbRange`.
export const FIXED_DB_MIN = -140;
export const FIXED_DB_MAX = -50;

// TX panadapter defaults — kept separate from RX so the user can drag the
// scale while keyed without disturbing their RX noise-floor view. Matches
// Thetis's `TXSpectrumGridMin = -80` / `TXSpectrumGridMax = 20` (Display.cs:
// 1881-1897). Speech peaks land inside this window; a user who wants to
// hide silence-time floor pumping raises TX_DB_MIN via the drag gesture.
export const TX_FIXED_DB_MIN = -80;
export const TX_FIXED_DB_MAX = 20;

const STORAGE_KEY = 'zeus.display.dbRange';
const TX_STORAGE_KEY = 'zeus.display.txDbRange';
const CONTRAST_STORAGE_KEY = 'zeus.display.contrast';

// Allowed range for the waterfall gamma. Outside this band the display
// either flattens to a single colour (very low) or clips weak signal to
// black (very high). Default 1.0 = identity.
export const CONTRAST_MIN = 0.5;
export const CONTRAST_MAX = 4.0;
export const CONTRAST_DEFAULT = 1.0;

function readSavedContrast(): number {
  try {
    if (typeof localStorage === 'undefined') return CONTRAST_DEFAULT;
    const raw = localStorage.getItem(CONTRAST_STORAGE_KEY);
    if (!raw) return CONTRAST_DEFAULT;
    const v = Number(raw);
    if (!Number.isFinite(v) || v < CONTRAST_MIN || v > CONTRAST_MAX) return CONTRAST_DEFAULT;
    return v;
  } catch {
    return CONTRAST_DEFAULT;
  }
}

function writeSavedContrast(v: number): void {
  try {
    if (typeof localStorage === 'undefined') return;
    localStorage.setItem(CONTRAST_STORAGE_KEY, String(v));
  } catch {
    // quota / private mode — silently accept
  }
}

function readSavedRange(): { dbMin: number; dbMax: number } {
  try {
    if (typeof localStorage === 'undefined') return { dbMin: FIXED_DB_MIN, dbMax: FIXED_DB_MAX };
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return { dbMin: FIXED_DB_MIN, dbMax: FIXED_DB_MAX };
    const parsed = JSON.parse(raw);
    const dbMin = typeof parsed?.dbMin === 'number' ? parsed.dbMin : FIXED_DB_MIN;
    const dbMax = typeof parsed?.dbMax === 'number' ? parsed.dbMax : FIXED_DB_MAX;
    if (!(dbMin < dbMax) || !Number.isFinite(dbMin) || !Number.isFinite(dbMax)) {
      return { dbMin: FIXED_DB_MIN, dbMax: FIXED_DB_MAX };
    }
    return { dbMin, dbMax };
  } catch {
    return { dbMin: FIXED_DB_MIN, dbMax: FIXED_DB_MAX };
  }
}

function writeSavedRange(dbMin: number, dbMax: number): void {
  try {
    if (typeof localStorage === 'undefined') return;
    localStorage.setItem(STORAGE_KEY, JSON.stringify({ dbMin, dbMax }));
  } catch {
    // quota exceeded / private mode — accept silently, the in-memory state
    // is still the source of truth for this session.
  }
}

function readSavedTxRange(): { txDbMin: number; txDbMax: number } {
  try {
    if (typeof localStorage === 'undefined') return { txDbMin: TX_FIXED_DB_MIN, txDbMax: TX_FIXED_DB_MAX };
    const raw = localStorage.getItem(TX_STORAGE_KEY);
    if (!raw) return { txDbMin: TX_FIXED_DB_MIN, txDbMax: TX_FIXED_DB_MAX };
    const parsed = JSON.parse(raw);
    const txDbMin = typeof parsed?.txDbMin === 'number' ? parsed.txDbMin : TX_FIXED_DB_MIN;
    const txDbMax = typeof parsed?.txDbMax === 'number' ? parsed.txDbMax : TX_FIXED_DB_MAX;
    if (!(txDbMin < txDbMax) || !Number.isFinite(txDbMin) || !Number.isFinite(txDbMax)) {
      return { txDbMin: TX_FIXED_DB_MIN, txDbMax: TX_FIXED_DB_MAX };
    }
    return { txDbMin, txDbMax };
  } catch {
    return { txDbMin: TX_FIXED_DB_MIN, txDbMax: TX_FIXED_DB_MAX };
  }
}

function writeSavedTxRange(txDbMin: number, txDbMax: number): void {
  try {
    if (typeof localStorage === 'undefined') return;
    localStorage.setItem(TX_STORAGE_KEY, JSON.stringify({ txDbMin, txDbMax }));
  } catch {
    // quota exceeded / private mode — accept silently.
  }
}

// Exponential smoothing constant for the auto-range tracker. 0.1 trades
// flicker resistance for responsiveness — band-change artifacts fade over
// ~30 frames at 30 Hz (~1 s).
const SMOOTHING = 0.1;

// Give the auto-tracked range a little headroom so the tops of strong
// signals don't clip to the brightest colour and the noise-floor doesn't
// sit right at the darkest index.
const AUTO_FLOOR_MARGIN_DB = 8;
const AUTO_CEIL_MARGIN_DB = 6;

// Guard against degenerate ranges (e.g. silent input producing p5==p95).
const MIN_SPAN_DB = 20;

export type DisplaySettingsState = {
  autoRange: boolean;
  // Panadapter dB window. Driven by the DbScale gesture (manual) and/or the
  // AUTO toggle (EMA-tracked). The waterfall has its own window — see
  // wfDbMin/wfDbMax below — so dragging the panadapter scale no longer
  // affects waterfall brightness.
  dbMin: number;
  dbMax: number;
  // Waterfall dB window. Always EMA-tracks the wfDb percentiles so the
  // colour mapping stays sensible across band-condition changes; the
  // operator's only direct knob on the waterfall is the contrast (γ) slider.
  wfDbMin: number;
  wfDbMax: number;
  // Separate dB range for TX panadapter (rendered during MOX/TUN). Thetis
  // parity — see TX_FIXED_DB_MIN/MAX constants.
  txDbMin: number;
  txDbMax: number;
  colormap: ColormapId;
  contrast: number;
  setAutoRange: (v: boolean) => void;
  setColormap: (id: ColormapId) => void;
  setContrast: (gamma: number) => void;
  updateAutoRange: (wfDb: Float32Array) => void;
  // Always-on EMA tracker for the waterfall window.
  updateWfRange: (wfDb: Float32Array) => void;
  // Shift dbMin and dbMax together by `deltaDb`. Used by the draggable dB
  // scale overlay on the panadapter with content-follows-finger semantics:
  // drag DOWN raises both limits so the trace slides DOWN on the canvas.
  // Clamps absolute values to Thetis's ±200 dB window.
  shiftDbRange: (deltaDb: number) => void;
  // Same as shiftDbRange but for the TX-specific range.
  shiftTxDbRange: (deltaDb: number) => void;
};

const DB_ABS_LIMIT = 200;

const initialRange = readSavedRange();
const initialTxRange = readSavedTxRange();

export const useDisplaySettingsStore = create<DisplaySettingsState>((set, get) => ({
  autoRange: false,
  dbMin: initialRange.dbMin,
  dbMax: initialRange.dbMax,
  wfDbMin: FIXED_DB_MIN,
  wfDbMax: FIXED_DB_MAX,
  txDbMin: initialTxRange.txDbMin,
  txDbMax: initialTxRange.txDbMax,
  colormap: 'blue',
  contrast: readSavedContrast(),
  setAutoRange: (autoRange) => {
    if (autoRange) {
      set({ autoRange: true });
    } else {
      // Snap back to the user's saved range if they have one, otherwise to
      // the factory fixed range. Matches the mental model of "auto is a
      // temporary override; off restores what I set".
      const saved = readSavedRange();
      set({ autoRange: false, dbMin: saved.dbMin, dbMax: saved.dbMax });
    }
  },
  setColormap: (colormap) => set({ colormap }),
  setContrast: (gamma) => {
    const clamped = Math.max(CONTRAST_MIN, Math.min(CONTRAST_MAX, gamma));
    set({ contrast: clamped });
    writeSavedContrast(clamped);
  },
  shiftDbRange: (deltaDb) => {
    // While AUTO is on, the live dbMin/dbMax are EMA-smoothed band-tracking
    // outputs (often messy floats and a tighter span than the user's saved
    // FIXED range). Promoting those into localStorage would lock the user
    // into a transient AUTO snapshot. Instead, mirror setAutoRange(false):
    // start from the last persisted FIXED range, apply the shift to that.
    const { autoRange, dbMin, dbMax } = get();
    const baseMin = autoRange ? readSavedRange().dbMin : dbMin;
    const baseMax = autoRange ? readSavedRange().dbMax : dbMax;
    const nextMin = Math.max(-DB_ABS_LIMIT, Math.min(DB_ABS_LIMIT, baseMin + deltaDb));
    const nextMax = Math.max(-DB_ABS_LIMIT, Math.min(DB_ABS_LIMIT, baseMax + deltaDb));
    set({ autoRange: false, dbMin: nextMin, dbMax: nextMax });
    writeSavedRange(nextMin, nextMax);
  },
  shiftTxDbRange: (deltaDb) => {
    const { txDbMin, txDbMax } = get();
    const nextMin = Math.max(-DB_ABS_LIMIT, Math.min(DB_ABS_LIMIT, txDbMin + deltaDb));
    const nextMax = Math.max(-DB_ABS_LIMIT, Math.min(DB_ABS_LIMIT, txDbMax + deltaDb));
    set({ txDbMin: nextMin, txDbMax: nextMax });
    writeSavedTxRange(nextMin, nextMax);
  },
  updateAutoRange: (wfDb) => {
    if (!get().autoRange || wfDb.length === 0) return;
    const [p5, p95] = percentiles(wfDb);
    let targetMin = p5 - AUTO_FLOOR_MARGIN_DB;
    let targetMax = p95 + AUTO_CEIL_MARGIN_DB;
    if (targetMax - targetMin < MIN_SPAN_DB) {
      const mid = 0.5 * (targetMin + targetMax);
      targetMin = mid - MIN_SPAN_DB / 2;
      targetMax = mid + MIN_SPAN_DB / 2;
    }
    const { dbMin, dbMax } = get();
    set({
      dbMin: dbMin * (1 - SMOOTHING) + targetMin * SMOOTHING,
      dbMax: dbMax * (1 - SMOOTHING) + targetMax * SMOOTHING,
    });
  },
  updateWfRange: (wfDb) => {
    if (wfDb.length === 0) return;
    const [p5, p95] = percentiles(wfDb);
    let targetMin = p5 - AUTO_FLOOR_MARGIN_DB;
    let targetMax = p95 + AUTO_CEIL_MARGIN_DB;
    if (targetMax - targetMin < MIN_SPAN_DB) {
      const mid = 0.5 * (targetMin + targetMax);
      targetMin = mid - MIN_SPAN_DB / 2;
      targetMax = mid + MIN_SPAN_DB / 2;
    }
    const { wfDbMin, wfDbMax } = get();
    set({
      wfDbMin: wfDbMin * (1 - SMOOTHING) + targetMin * SMOOTHING,
      wfDbMax: wfDbMax * (1 - SMOOTHING) + targetMax * SMOOTHING,
    });
  },
}));

// p5/p95 via a sorted copy. For the ~1024-sample widths we see in
// production this is well under 1 ms; a quickselect would be overkill.
function percentiles(arr: Float32Array): [number, number] {
  const n = arr.length;
  const sorted = Float32Array.from(arr);
  sorted.sort();
  const lowIdx = Math.min(n - 1, Math.max(0, Math.floor(0.05 * n)));
  const highIdx = Math.min(n - 1, Math.max(0, Math.floor(0.95 * n)));
  return [sorted[lowIdx] ?? FIXED_DB_MIN, sorted[highIdx] ?? FIXED_DB_MAX];
}
