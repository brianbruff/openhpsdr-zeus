import { describe, expect, it } from 'vitest';
import { planWaterfallUpdate } from './wf-shift';

const base = {
  lastCenterHz: 14_200_000n,
  lastHzPerPixel: 100,
  lastWidth: 1024,
  nextCenterHz: 14_200_000n,
  nextHzPerPixel: 100,
  nextWidth: 1024,
};

describe('planWaterfallUpdate', () => {
  it('resets on first frame (no prior center)', () => {
    expect(
      planWaterfallUpdate({ ...base, lastCenterHz: null, lastWidth: 0 }),
    ).toEqual({ kind: 'reset', reason: 'first' });
  });

  it('resets when width changes', () => {
    expect(
      planWaterfallUpdate({ ...base, nextWidth: 2048 }),
    ).toEqual({ kind: 'reset', reason: 'width' });
  });

  it('resets when hzPerPixel changes (sampleRate or span change)', () => {
    expect(
      planWaterfallUpdate({ ...base, nextHzPerPixel: 50 }),
    ).toEqual({ kind: 'reset', reason: 'hzPerPixel' });
  });

  it('pushes a row when center is unchanged', () => {
    expect(planWaterfallUpdate(base)).toEqual({ kind: 'push' });
  });

  it('pushes when sub-pixel retune does not cross a column', () => {
    // 50 Hz at 100 Hz/px rounds to 0 — carrier should sit still, no shift.
    // lastCenterHz must stay put so the next 50 Hz step accumulates to a
    // full pixel. The renderer enforces that by only updating lastCenterHz
    // on reset/shift (not on push), so here we just assert the decision.
    expect(
      planWaterfallUpdate({ ...base, nextCenterHz: 14_200_050n }),
    ).toEqual({ kind: 'push' });
  });

  it('shifts right (+shiftPx) when tuning down (oldCenter > newCenter)', () => {
    // 14.200 → 14.199 MHz: 1000 Hz / 100 Hz/px = 10 columns right.
    // Integer-pixel retune — residual matches the new center exactly.
    const d = planWaterfallUpdate({ ...base, nextCenterHz: 14_199_000n });
    expect(d).toEqual({
      kind: 'shift',
      shiftPx: 10,
      residualCenterHz: 14_199_000n,
    });
  });

  it('shifts left (-shiftPx) when tuning up (newCenter > oldCenter)', () => {
    // 14.200 → 14.201 MHz: −1000 Hz / 100 Hz/px = −10 columns.
    const d = planWaterfallUpdate({ ...base, nextCenterHz: 14_201_000n });
    expect(d).toEqual({
      kind: 'shift',
      shiftPx: -10,
      residualCenterHz: 14_201_000n,
    });
  });

  it('preserves sub-pixel residual so fine retunes accumulate', () => {
    // A 150 Hz retune at 100 Hz/px rounds to 1 px, leaves a 50 Hz residual.
    // The planner reports the residual back as the new lastCenterHz so the
    // NEXT retune sees a larger effective delta — a second 150 Hz step
    // will shift another 2 px (not 1), catching up the missed column.
    const d = planWaterfallUpdate({ ...base, nextCenterHz: 14_199_850n });
    expect(d).toEqual({
      kind: 'shift',
      shiftPx: 2,
      residualCenterHz: 14_200_000n - 200n, // 2 * 100 applied
    });
  });

  it('resets when |shift| >= width (retune larger than the visible span)', () => {
    // 100 kHz retune at 100 Hz/px = 1000 columns; width=1024 → reset.
    const d = planWaterfallUpdate({
      ...base,
      nextCenterHz: base.lastCenterHz - 200_000n,
    });
    expect(d).toEqual({ kind: 'reset', reason: 'span' });
  });

  it('treats |shift| exactly equal to width as a reset (nothing to keep)', () => {
    const w = base.lastWidth;
    const hz = BigInt(w * base.lastHzPerPixel);
    const d = planWaterfallUpdate({
      ...base,
      nextCenterHz: base.lastCenterHz - hz,
    });
    expect(d).toEqual({ kind: 'reset', reason: 'span' });
  });
});
