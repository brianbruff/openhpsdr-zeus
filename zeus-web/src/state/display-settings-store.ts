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
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { create } from 'zustand';
import type { ColormapId } from '../gl/colormap';
import {
  deleteDisplayImage,
  displayImageUrl,
  fetchDisplaySettings,
  updateDisplaySettings,
  uploadDisplayImage,
} from '../api/display';

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
const WF_STORAGE_KEY = 'zeus.display.wfDbRange';
const RX_TRACE_COLOR_KEY = 'zeus.display.rxTraceColor';

// Legacy localStorage keys — pre-server-side storage. Read once on first
// load to migrate the operator's existing image up to the backend, then
// removed. New code should never read or write these.
const LEGACY_PAN_BG_KEY = 'zeus.display.panBackground';
const LEGACY_BG_IMAGE_KEY = 'zeus.display.backgroundImage';
const LEGACY_BG_FIT_KEY = 'zeus.display.backgroundImageFit';

// Default RX panadapter trace colour — warm amber, matching the original
// hardcoded constant in gl/panadapter.ts. Operators can pick another colour
// from the Display tab; the choice is persisted to localStorage.
export const DEFAULT_RX_TRACE_COLOR = '#FFA028';

function isHexColor(v: unknown): v is string {
  return typeof v === 'string' && /^#[0-9A-Fa-f]{6}$/.test(v);
}

// Panadapter background mode. 'basic' = no overlay (current QRZ-off
// look). 'beam-map' = world-map overlay with terminator lines and beam
// chrome (current QRZ-on look). 'image' = user-supplied still image
// behind a transparent panadapter / waterfall.
export type PanBackgroundMode = 'basic' | 'beam-map' | 'image';

// CSS background-size mapping for the image background.
// 'fit' → contain (entire image visible, may letterbox)
// 'fill' → cover (fills the panel, may crop)
// 'stretch' → 100% 100% (distorts to fit exactly)
export type BackgroundImageFit = 'fit' | 'fill' | 'stretch';

function readRxTraceColor(): string {
  try {
    if (typeof localStorage === 'undefined') return DEFAULT_RX_TRACE_COLOR;
    const raw = localStorage.getItem(RX_TRACE_COLOR_KEY);
    return isHexColor(raw) ? raw.toUpperCase() : DEFAULT_RX_TRACE_COLOR;
  } catch {
    return DEFAULT_RX_TRACE_COLOR;
  }
}
function writeRxTraceColor(v: string): void {
  try { if (typeof localStorage !== 'undefined') localStorage.setItem(RX_TRACE_COLOR_KEY, v); } catch { /* quota */ }
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

function readSavedWfRange(): { wfDbMin: number; wfDbMax: number } {
  try {
    if (typeof localStorage === 'undefined') return { wfDbMin: FIXED_DB_MIN, wfDbMax: FIXED_DB_MAX };
    const raw = localStorage.getItem(WF_STORAGE_KEY);
    if (!raw) return { wfDbMin: FIXED_DB_MIN, wfDbMax: FIXED_DB_MAX };
    const parsed = JSON.parse(raw);
    const wfDbMin = typeof parsed?.wfDbMin === 'number' ? parsed.wfDbMin : FIXED_DB_MIN;
    const wfDbMax = typeof parsed?.wfDbMax === 'number' ? parsed.wfDbMax : FIXED_DB_MAX;
    if (!(wfDbMin < wfDbMax) || !Number.isFinite(wfDbMin) || !Number.isFinite(wfDbMax)) {
      return { wfDbMin: FIXED_DB_MIN, wfDbMax: FIXED_DB_MAX };
    }
    return { wfDbMin, wfDbMax };
  } catch {
    return { wfDbMin: FIXED_DB_MIN, wfDbMax: FIXED_DB_MAX };
  }
}

function writeSavedWfRange(wfDbMin: number, wfDbMax: number): void {
  try {
    if (typeof localStorage === 'undefined') return;
    localStorage.setItem(WF_STORAGE_KEY, JSON.stringify({ wfDbMin, wfDbMax }));
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
  // Panadapter dB window. Driven by the DbScale gesture (manual) and/or
  // the AUTO toggle (EMA-tracked).
  dbMin: number;
  dbMax: number;
  // Waterfall dB window. Independent of the panadapter so the operator
  // can darken/brighten the waterfall colour mapping without disturbing
  // the panadapter's noise-floor view. Driven by its own DbScale slider.
  wfDbMin: number;
  wfDbMax: number;
  // Separate dB range for TX panadapter (rendered during MOX/TUN). Thetis
  // parity — see TX_FIXED_DB_MIN/MAX constants.
  txDbMin: number;
  txDbMax: number;
  colormap: ColormapId;
  // Panadapter background overlay mode + (optional) user image. See the
  // PanBackgroundMode and BackgroundImageFit types above. Persisted on the
  // backend (zeus-prefs.db) so a single setting follows the operator across
  // every browser pointed at the Zeus instance — phones, tablets, multiple
  // desktops. backgroundImage is a server URL with a cache-busting query
  // string, not a data:URL. setBackgroundImage returns false on upload
  // failure (network or server-side rejection).
  panBackground: PanBackgroundMode;
  backgroundImage: string | null;
  backgroundImageFit: BackgroundImageFit;
  // RX panadapter trace colour as #RRGGBB. Drives both the sharp trace line
  // and the fill underneath in gl/panadapter.ts (kept in lockstep).
  rxTraceColor: string;
  setPanBackground: (v: PanBackgroundMode) => Promise<void>;
  setBackgroundImage: (dataUrl: string | null) => Promise<boolean>;
  setBackgroundImageFit: (v: BackgroundImageFit) => Promise<void>;
  setRxTraceColor: (v: string) => void;
  setAutoRange: (v: boolean) => void;
  setColormap: (id: ColormapId) => void;
  updateAutoRange: (wfDb: Float32Array) => void;
  // Shift dbMin and dbMax together by `deltaDb`. Used by the draggable dB
  // scale overlay on the panadapter with content-follows-finger semantics:
  // drag DOWN raises both limits so the trace slides DOWN on the canvas.
  // Clamps absolute values to Thetis's ±200 dB window.
  shiftDbRange: (deltaDb: number) => void;
  // Same as shiftDbRange but for the TX-specific range.
  shiftTxDbRange: (deltaDb: number) => void;
  // Same as shiftDbRange but for the waterfall's independent range.
  shiftWfDbRange: (deltaDb: number) => void;
};

const DB_ABS_LIMIT = 200;

const initialRange = readSavedRange();
const initialTxRange = readSavedTxRange();
const initialWfRange = readSavedWfRange();

export const useDisplaySettingsStore = create<DisplaySettingsState>((set, get) => ({
  autoRange: false,
  dbMin: initialRange.dbMin,
  dbMax: initialRange.dbMax,
  wfDbMin: initialWfRange.wfDbMin,
  wfDbMax: initialWfRange.wfDbMax,
  txDbMin: initialTxRange.txDbMin,
  txDbMax: initialTxRange.txDbMax,
  colormap: 'blue',
  // Defaults until the server-side fetch lands (see hydrateFromServer at the
  // bottom of this file). The operator briefly sees a plain panadapter on
  // first paint instead of their saved image — acceptable trade-off for not
  // shipping the image on every page-load via localStorage.
  panBackground: 'basic',
  backgroundImage: null,
  backgroundImageFit: 'fill',
  rxTraceColor: readRxTraceColor(),
  setPanBackground: async (panBackground) => {
    const prev = get().panBackground;
    set({ panBackground });
    try {
      const result = await updateDisplaySettings(panBackground, get().backgroundImageFit);
      // If the server normalised the value (unknown input → 'basic'), reflect that.
      if (result.mode !== panBackground) set({ panBackground: result.mode });
    } catch {
      set({ panBackground: prev });
    }
  },
  setBackgroundImage: async (dataUrl) => {
    if (dataUrl == null) {
      try {
        const result = await deleteDisplayImage();
        set({
          backgroundImage: null,
          // Server may have transitioned mode if it had been 'image' — but we
          // only update mode if the server says so explicitly via the result.
          panBackground: result.mode,
          backgroundImageFit: result.fit,
        });
        return true;
      } catch {
        return false;
      }
    }
    try {
      const blob = await dataUrlToBlob(dataUrl);
      const result = await uploadDisplayImage(blob);
      set({
        backgroundImage: result.hasImage ? displayImageUrl(Date.now()) : null,
        panBackground: result.mode,
        backgroundImageFit: result.fit,
      });
      return result.hasImage;
    } catch {
      return false;
    }
  },
  setBackgroundImageFit: async (backgroundImageFit) => {
    const prev = get().backgroundImageFit;
    set({ backgroundImageFit });
    try {
      const result = await updateDisplaySettings(get().panBackground, backgroundImageFit);
      if (result.fit !== backgroundImageFit) set({ backgroundImageFit: result.fit });
    } catch {
      set({ backgroundImageFit: prev });
    }
  },
  setRxTraceColor: (v) => {
    if (!isHexColor(v)) return;
    const norm = v.toUpperCase();
    writeRxTraceColor(norm);
    set({ rxTraceColor: norm });
  },
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
  shiftWfDbRange: (deltaDb) => {
    const { wfDbMin, wfDbMax } = get();
    const nextMin = Math.max(-DB_ABS_LIMIT, Math.min(DB_ABS_LIMIT, wfDbMin + deltaDb));
    const nextMax = Math.max(-DB_ABS_LIMIT, Math.min(DB_ABS_LIMIT, wfDbMax + deltaDb));
    set({ wfDbMin: nextMin, wfDbMax: nextMax });
    writeSavedWfRange(nextMin, nextMax);
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

// Decode a data:URL produced by canvas.toDataURL() into a Blob the multipart
// upload can carry. Used by setBackgroundImage to bridge the panel's
// canvas-based compression pipeline to the backend's byte storage.
async function dataUrlToBlob(dataUrl: string): Promise<Blob> {
  const res = await fetch(dataUrl);
  return res.blob();
}

// One-shot hydration from the backend at module load. If the server has
// nothing yet but this browser still holds a legacy localStorage image,
// push it up once and clear local — that's the migration path for operators
// who set a background before the server-side store existed. Either way the
// three legacy keys are removed afterwards so the localStorage stays clean.
async function hydrateFromServer(): Promise<void> {
  let server: Awaited<ReturnType<typeof fetchDisplaySettings>>;
  try {
    server = await fetchDisplaySettings();
  } catch {
    // Backend unreachable; leave defaults in place. Next call to
    // setPanBackground / setBackgroundImage will hit the server.
    return;
  }

  const legacy = readLegacyLocalStorage();
  const serverHasContent =
    server.hasImage || server.mode !== 'basic' || server.fit !== 'fill';

  if (!serverHasContent && legacy && (legacy.image || legacy.mode || legacy.fit)) {
    try {
      if (legacy.mode || legacy.fit) {
        const next = await updateDisplaySettings(
          legacy.mode ?? server.mode,
          legacy.fit ?? server.fit,
        );
        server = next;
      }
      if (legacy.image) {
        const blob = await dataUrlToBlob(legacy.image);
        server = await uploadDisplayImage(blob);
      }
    } catch {
      // Migration failed — leave legacy keys in place so we retry next load.
      return;
    }
  }

  clearLegacyLocalStorage();

  useDisplaySettingsStore.setState({
    panBackground: server.mode,
    backgroundImage: server.hasImage ? displayImageUrl(Date.now()) : null,
    backgroundImageFit: server.fit,
  });
}

function readLegacyLocalStorage(): { mode: PanBackgroundMode | null; fit: BackgroundImageFit | null; image: string | null } | null {
  if (typeof localStorage === 'undefined') return null;
  try {
    const rawMode = localStorage.getItem(LEGACY_PAN_BG_KEY);
    const rawFit = localStorage.getItem(LEGACY_BG_FIT_KEY);
    const rawImg = localStorage.getItem(LEGACY_BG_IMAGE_KEY);
    const mode =
      rawMode === 'basic' || rawMode === 'beam-map' || rawMode === 'image' ? rawMode : null;
    const fit =
      rawFit === 'fit' || rawFit === 'fill' || rawFit === 'stretch' ? rawFit : null;
    const image = rawImg && rawImg.startsWith('data:image/') ? rawImg : null;
    return { mode, fit, image };
  } catch {
    return null;
  }
}

function clearLegacyLocalStorage(): void {
  try {
    if (typeof localStorage === 'undefined') return;
    localStorage.removeItem(LEGACY_PAN_BG_KEY);
    localStorage.removeItem(LEGACY_BG_IMAGE_KEY);
    localStorage.removeItem(LEGACY_BG_FIT_KEY);
  } catch {
    /* private mode — nothing to clean up */
  }
}

void hydrateFromServer();
