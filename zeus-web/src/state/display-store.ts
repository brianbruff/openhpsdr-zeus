import { create } from 'zustand';
import type { DecodedFrame } from '../realtime/frame';

export type DisplayState = {
  connected: boolean;
  width: number;
  centerHz: bigint;
  hzPerPixel: number;
  panDb: Float32Array | null;
  wfDb: Float32Array | null;
  panValid: boolean;
  wfValid: boolean;
  lastSeq: number;
  setConnected: (c: boolean) => void;
  pushFrame: (f: DecodedFrame) => void;
};

export const useDisplayStore = create<DisplayState>((set) => ({
  connected: false,
  width: 0,
  centerHz: 0n,
  hzPerPixel: 0,
  panDb: null,
  wfDb: null,
  panValid: false,
  wfValid: false,
  lastSeq: 0,
  setConnected: (connected) => set({ connected }),
  pushFrame: (f) =>
    set({
      width: f.width,
      centerHz: f.centerHz,
      hzPerPixel: f.hzPerPixel,
      panDb: f.panDb,
      wfDb: f.wfDb,
      panValid: f.panValid,
      wfValid: f.wfValid,
      lastSeq: f.seq,
    }),
}));

export function subscribeFrames(cb: (s: DisplayState) => void): () => void {
  return useDisplayStore.subscribe(cb);
}
