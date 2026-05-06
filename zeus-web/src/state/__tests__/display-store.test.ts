// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Per-RxId display store (issue #251). Verifies that:
//   - pushFrame for RxId=0 only populates slice[0]; the bit-identical
//     single-slice path consumers see no change in behaviour.
//   - pushFrame for RxId=1 populates a separate slice without touching
//     slice[0] (so an RX0 panel keeps rendering its own data).
//   - the slice Map is replaced (not mutated) on every push so Zustand
//     subscribers fire and React selectors re-evaluate.
//   - useSliceDisplay returns the stable EMPTY_SLICE_STATE for slices
//     that haven't received a frame yet.

import { beforeEach, describe, expect, it } from 'vitest';
import {
  EMPTY_SLICE_STATE,
  getSliceState,
  useDisplayStore,
} from '../display-store';
import type { DecodedFrame } from '../../realtime/frame';

function makeFrame(rxId: number, seq: number): DecodedFrame {
  return {
    msgType: 0x01,
    headerFlags: 0,
    seq,
    tsUnixMs: 0,
    rxId,
    bodyFlags: 0x03, // panValid + wfValid
    panValid: true,
    wfValid: true,
    width: 4,
    centerHz: BigInt(14_200_000 + rxId * 1_000_000),
    hzPerPixel: 100,
    panDb: new Float32Array([-100, -90, -80, -70]),
    wfDb: new Float32Array([-100, -90, -80, -70]),
  };
}

describe('display-store / per-RxId slices', () => {
  beforeEach(() => {
    useDisplayStore.setState({
      connected: false,
      slices: new Map(),
    });
  });

  it('pushFrame for RxId=0 populates only slice[0]', () => {
    useDisplayStore.getState().pushFrame(makeFrame(0, 1));
    const state = useDisplayStore.getState();
    expect(state.slices.size).toBe(1);
    expect(state.slices.get(0)?.lastSeq).toBe(1);
    expect(state.slices.get(0)?.centerHz).toBe(BigInt(14_200_000));
    expect(state.slices.get(1)).toBeUndefined();
  });

  it('pushFrame for RxId=1 leaves slice[0] untouched (different objects)', () => {
    useDisplayStore.getState().pushFrame(makeFrame(0, 1));
    const slice0BeforeRef = useDisplayStore.getState().slices.get(0);
    useDisplayStore.getState().pushFrame(makeFrame(1, 2));
    const after = useDisplayStore.getState();
    // Slice[0] reference unchanged → React selectors keyed on slice[0]
    // do not re-render.
    expect(after.slices.get(0)).toBe(slice0BeforeRef);
    // Slice[1] now exists with the RX1 frame's center.
    expect(after.slices.get(1)?.centerHz).toBe(BigInt(15_200_000));
    expect(after.slices.get(1)?.lastSeq).toBe(2);
  });

  it('replaces the slices Map identity on every push', () => {
    useDisplayStore.getState().pushFrame(makeFrame(0, 1));
    const mapA = useDisplayStore.getState().slices;
    useDisplayStore.getState().pushFrame(makeFrame(0, 2));
    const mapB = useDisplayStore.getState().slices;
    // New Map → full-store subscribers (Panadapter, Waterfall) wake up.
    expect(mapA).not.toBe(mapB);
  });

  it('getSliceState returns EMPTY_SLICE_STATE when the slice has no data', () => {
    expect(getSliceState(7)).toBe(EMPTY_SLICE_STATE);
  });

  it('treats f.rxId === undefined as RxId=0 (synthetic-frame fallback)', () => {
    // Cast: DecodedFrame.rxId is required, but the store guards with ?? 0
    // for any synthetic / partial frame paths that omit it.
    const frame = { ...makeFrame(0, 5), rxId: undefined as unknown as number };
    useDisplayStore.getState().pushFrame(frame);
    expect(useDisplayStore.getState().slices.get(0)?.lastSeq).toBe(5);
  });
});
