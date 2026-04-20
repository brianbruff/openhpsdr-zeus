import { beforeEach, describe, expect, it } from 'vitest';
import {
  FIXED_DB_MAX,
  FIXED_DB_MIN,
  useDisplaySettingsStore,
} from './display-settings-store';

function resetStore() {
  useDisplaySettingsStore.setState({
    autoRange: false,
    dbMin: FIXED_DB_MIN,
    dbMax: FIXED_DB_MAX,
  });
}

describe('display-settings-store', () => {
  beforeEach(resetStore);

  it('returns the fixed range by default', () => {
    const { dbMin, dbMax, autoRange } = useDisplaySettingsStore.getState();
    expect(autoRange).toBe(false);
    expect(dbMin).toBe(FIXED_DB_MIN);
    expect(dbMax).toBe(FIXED_DB_MAX);
  });

  it('ignores updateAutoRange while autoRange is off', () => {
    const arr = new Float32Array(64).fill(-90);
    useDisplaySettingsStore.getState().updateAutoRange(arr);
    const { dbMin, dbMax } = useDisplaySettingsStore.getState();
    expect(dbMin).toBe(FIXED_DB_MIN);
    expect(dbMax).toBe(FIXED_DB_MAX);
  });

  it('snaps back to fixed range when turned off', () => {
    const s = useDisplaySettingsStore.getState();
    s.setAutoRange(true);
    useDisplaySettingsStore.setState({ dbMin: -80, dbMax: -40 });
    useDisplaySettingsStore.getState().setAutoRange(false);
    const { dbMin, dbMax } = useDisplaySettingsStore.getState();
    expect(dbMin).toBe(FIXED_DB_MIN);
    expect(dbMax).toBe(FIXED_DB_MAX);
  });

  it('drifts dbMin/dbMax toward percentile target with smoothing', () => {
    useDisplaySettingsStore.getState().setAutoRange(true);

    // Flat noise floor at -95, plus a top 5% of strong peaks at -50.
    const n = 200;
    const arr = new Float32Array(n);
    for (let i = 0; i < n; i++) arr[i] = i < n * 0.95 ? -95 : -50;

    for (let k = 0; k < 200; k++) {
      useDisplaySettingsStore.getState().updateAutoRange(arr);
    }

    const { dbMin, dbMax } = useDisplaySettingsStore.getState();
    // After many iterations the smoothed range converges on
    // (p5 - AUTO_FLOOR_MARGIN, p95 + AUTO_CEIL_MARGIN). p5 = -95, p95 = -50,
    // so targets are ≈ -103 and ≈ -44. Both should have moved well away
    // from the fixed defaults (-120, -30) toward the data.
    expect(dbMin).toBeGreaterThan(-110);
    expect(dbMax).toBeLessThan(-40);
    expect(dbMin).toBeLessThan(dbMax);
  });

  it('enforces a minimum span when the signal is flat', () => {
    useDisplaySettingsStore.getState().setAutoRange(true);
    const flat = new Float32Array(128).fill(-80);
    for (let k = 0; k < 400; k++) {
      useDisplaySettingsStore.getState().updateAutoRange(flat);
    }
    const { dbMin, dbMax } = useDisplaySettingsStore.getState();
    expect(dbMax - dbMin).toBeGreaterThanOrEqual(19.9);
  });

  it('handles an empty array without producing NaN', () => {
    useDisplaySettingsStore.getState().setAutoRange(true);
    useDisplaySettingsStore.getState().updateAutoRange(new Float32Array(0));
    const { dbMin, dbMax } = useDisplaySettingsStore.getState();
    expect(Number.isFinite(dbMin)).toBe(true);
    expect(Number.isFinite(dbMax)).toBe(true);
  });
});
