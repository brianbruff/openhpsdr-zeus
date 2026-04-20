// Planner for waterfall horizontal shift on VFO change (doc 08 §5,
// deskhpsdr waterfall.c behaviour). Pure logic so it can be unit-tested
// without a WebGL context.
//
// Input is the "last-seen" frame geometry (width, hzPerPixel, centerHz) and
// the incoming frame's same fields. Output tells the renderer what to do:
//   - reset: wipe history (first frame, width or hzPerPixel changed, or
//     the LO moved far enough that the whole visible span is new data)
//   - push:  no translation needed, upload the new row normally
//   - shift: ping-pong horizontal shift by `shiftPx` columns; suppress
//     this tick's row blit and remember the residual sub-pixel delta so
//     subsequent fine retunes accumulate instead of being dropped
//
// Sign convention matches deskhpsdr: shiftPx = round((oldCenter − newCenter)
// / hzPerPixel). The server emits `wfDb` with low freq on the left / high
// freq on the right (see DspPipelineService.Tick — unconditional
// Array.Reverse), so a positive shiftPx means columns slide right.

export type WfShiftInput = {
  lastCenterHz: bigint | null;
  lastHzPerPixel: number;
  lastWidth: number;
  nextCenterHz: bigint;
  nextHzPerPixel: number;
  nextWidth: number;
};

export type WfShiftDecision =
  | { kind: 'reset'; reason: 'first' | 'width' | 'hzPerPixel' | 'span' }
  | { kind: 'push' }
  | { kind: 'shift'; shiftPx: number; residualCenterHz: bigint };

export function planWaterfallUpdate(i: WfShiftInput): WfShiftDecision {
  if (i.lastWidth === 0 || i.lastCenterHz === null) {
    return { kind: 'reset', reason: 'first' };
  }
  if (i.lastWidth !== i.nextWidth) return { kind: 'reset', reason: 'width' };
  if (i.lastHzPerPixel !== i.nextHzPerPixel) {
    return { kind: 'reset', reason: 'hzPerPixel' };
  }
  const deltaHz = Number(i.lastCenterHz - i.nextCenterHz);
  const shiftPx = Math.round(deltaHz / i.nextHzPerPixel);
  if (shiftPx === 0) return { kind: 'push' };
  if (Math.abs(shiftPx) >= i.nextWidth) {
    return { kind: 'reset', reason: 'span' };
  }
  // Apply the integer-pixel shift and roll the sub-pixel remainder forward
  // so a sequence of fine retunes doesn't drop into the rounding gap.
  const appliedHz = BigInt(Math.round(shiftPx * i.nextHzPerPixel));
  const residualCenterHz = i.lastCenterHz - appliedHz;
  return { kind: 'shift', shiftPx, residualCenterHz };
}
