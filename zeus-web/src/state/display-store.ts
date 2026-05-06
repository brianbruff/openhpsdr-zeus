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
import type { DecodedFrame } from '../realtime/frame';

/**
 * Per-RxId display state for one panadapter / waterfall slice. The single-
 * slice path (RxId 0 only — the default Hermes / ANAN behaviour) populates
 * exactly one map entry. The multi-slice path (HermesLite 2 with multi-RX
 * enabled) populates one entry per active DDC, each carrying its own
 * centerHz, hzPerPixel, and IQ pixel arrays.
 *
 * The shape mirrors the previous flat `DisplayState` field set; all that
 * changed is the indirection through `slices.get(rxId)`.
 */
export interface SliceDisplayState {
  width: number;
  centerHz: bigint;
  hzPerPixel: number;
  panDb: Float32Array | null;
  wfDb: Float32Array | null;
  panValid: boolean;
  wfValid: boolean;
  /** Per-slice frame seq from the wire. Components dedupe redraws on this
   *  so a frame for a different RxId doesn't trigger a no-op repaint. */
  lastSeq: number;
}

/**
 * Stable empty-slice reference. Selectors return this when the operator has
 * subscribed to an RxId that has not received a frame yet (e.g. RxId=1
 * before HL2 multi-slice is enabled). Returning a stable Object.is-equal
 * reference keeps React selector consumers from re-rendering each pushFrame
 * for an empty slice.
 */
export const EMPTY_SLICE_STATE: SliceDisplayState = Object.freeze({
  width: 0,
  centerHz: 0n,
  hzPerPixel: 0,
  panDb: null,
  wfDb: null,
  panValid: false,
  wfValid: false,
  lastSeq: 0,
}) as SliceDisplayState;

export type DisplayState = {
  connected: boolean;
  /**
   * Per-RxId slice state. The Map reference is replaced on every pushFrame
   * so full-store subscribers fire on any frame; per-slice consumers should
   * select via `useSliceDisplay(rxId)` (or `slices.get(rxId)`) and dedupe
   * on the slice's `lastSeq` so a frame for a different RxId doesn't
   * trigger redraw work on slices that didn't change.
   */
  slices: Map<number, SliceDisplayState>;
  setConnected: (c: boolean) => void;
  pushFrame: (f: DecodedFrame) => void;
};

export const useDisplayStore = create<DisplayState>((set) => ({
  connected: false,
  slices: new Map(),
  setConnected: (connected) => set({ connected }),
  pushFrame: (f) =>
    set((state) => {
      // DecodedFrame.rxId is always present (decoder reads it as Uint8); the
      // ?? 0 is belt-and-braces for any synthetic frame paths that omit it.
      const rxId = f.rxId ?? 0;
      const next: SliceDisplayState = {
        width: f.width,
        centerHz: f.centerHz,
        hzPerPixel: f.hzPerPixel,
        panDb: f.panDb,
        wfDb: f.wfDb,
        panValid: f.panValid,
        wfValid: f.wfValid,
        lastSeq: f.seq,
      };
      // Replace the Map (not mutate) so Zustand subscribers fire and React
      // selector hooks re-evaluate. The slice key receives the new object;
      // every other slice keeps its previous reference, so selectors that
      // pin to a different rxId stay stable.
      const slices = new Map(state.slices);
      slices.set(rxId, next);
      return { slices };
    }),
}));

/**
 * Reactively read the slice state for a given RxId. Returns
 * `EMPTY_SLICE_STATE` (stable reference) when no frame for that RxId has
 * arrived yet, so consumers can read `.panDb`, `.centerHz`, etc. without
 * null-guarding the slice object itself.
 *
 * Re-renders only when this RxId's slice changes — frames for other slices
 * leave the consumer's selector output Object.is-equal.
 */
export function useSliceDisplay(rxId: number): SliceDisplayState {
  return useDisplayStore((s) => s.slices.get(rxId) ?? EMPTY_SLICE_STATE);
}

/**
 * Imperative accessor for non-React readers — gesture handlers, FilterMiniPan
 * per-frame draw, etc. Returns `EMPTY_SLICE_STATE` when the RxId has no
 * entry yet. Pair with `useDisplayStore.subscribe(...)` for change
 * notifications when used outside a hook.
 */
export function getSliceState(rxId: number): SliceDisplayState {
  return useDisplayStore.getState().slices.get(rxId) ?? EMPTY_SLICE_STATE;
}

export function subscribeFrames(cb: (s: DisplayState) => void): () => void {
  return useDisplayStore.subscribe(cb);
}
