// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

import type { CSSProperties } from 'react';
import {
  DISPLAY_DECIMATION_MAX,
  DISPLAY_DECIMATION_MIN,
  DISPLAY_FRAME_RATES,
  DISPLAY_MAX_FRAME_RATE_HZ_MAX,
  DISPLAY_MAX_FRAME_RATE_HZ_MIN,
  WATERFALL_UPDATE_PERIOD_MAX,
  WATERFALL_UPDATE_PERIOD_MIN,
  useDisplaySettingsStore,
} from '../state/display-settings-store';

const DECIMATION_PRESETS = [1, 2, 4, 8, 16] as const;
const WATERFALL_INTERVAL_PRESETS = [1, 2, 3, 4, 8] as const;

export function DisplayPerformancePanel() {
  const displayMaxFrameRateHz = useDisplaySettingsStore((s) => s.displayMaxFrameRateHz);
  const displayDecimation = useDisplaySettingsStore((s) => s.displayDecimation);
  const waterfallUpdatePeriod = useDisplaySettingsStore((s) => s.waterfallUpdatePeriod);
  const setDisplayMaxFrameRateHz = useDisplaySettingsStore((s) => s.setDisplayMaxFrameRateHz);
  const setDisplayDecimation = useDisplaySettingsStore((s) => s.setDisplayDecimation);
  const setWaterfallUpdatePeriod = useDisplaySettingsStore((s) => s.setWaterfallUpdatePeriod);
  const waterfallDelayMs =
    (1000 / Math.max(1, displayMaxFrameRateHz)) * Math.max(1, waterfallUpdatePeriod);

  return (
    <section>
      <div style={sectionHead}>
        <h3 style={sectionH3}>Display Refresh</h3>
        <p style={sectionP}>Panadapter cadence, bin decimation, and waterfall row interval.</p>
      </div>

      <div style={card}>
        <div style={controlRow}>
          <label style={label} htmlFor="display-max-fps">Main display FPS</label>
          <input
            id="display-max-fps"
            type="number"
            min={DISPLAY_MAX_FRAME_RATE_HZ_MIN}
            max={DISPLAY_MAX_FRAME_RATE_HZ_MAX}
            step={1}
            value={displayMaxFrameRateHz}
            onChange={(ev) => {
              const next = ev.currentTarget.valueAsNumber;
              if (Number.isFinite(next)) void setDisplayMaxFrameRateHz(next);
            }}
            style={numberInput}
          />
        </div>
        <div style={presetGroup} role="group" aria-label="Display refresh rate presets">
          {DISPLAY_FRAME_RATES.map((rate) => (
            <PresetButton
              key={rate}
              active={Math.abs(displayMaxFrameRateHz - rate) < 0.5}
              label={`${rate}`}
              suffix="Hz"
              onClick={() => void setDisplayMaxFrameRateHz(rate)}
            />
          ))}
        </div>

        <div style={controlRow}>
          <label style={label} htmlFor="display-decimation">Display decimation</label>
          <input
            id="display-decimation"
            type="number"
            min={DISPLAY_DECIMATION_MIN}
            max={DISPLAY_DECIMATION_MAX}
            step={1}
            value={displayDecimation}
            onChange={(ev) => {
              const next = ev.currentTarget.valueAsNumber;
              if (Number.isFinite(next)) void setDisplayDecimation(next);
            }}
            style={numberInput}
          />
        </div>
        <div style={presetGroup} role="group" aria-label="Display decimation presets">
          {DECIMATION_PRESETS.map((value) => (
            <PresetButton
              key={value}
              active={displayDecimation === value}
              label={`${value}x`}
              onClick={() => void setDisplayDecimation(value)}
            />
          ))}
        </div>

        <div style={controlRow}>
          <label style={label} htmlFor="waterfall-update-period">Waterfall every</label>
          <input
            id="waterfall-update-period"
            type="number"
            min={WATERFALL_UPDATE_PERIOD_MIN}
            max={WATERFALL_UPDATE_PERIOD_MAX}
            step={1}
            value={waterfallUpdatePeriod}
            onChange={(ev) => {
              const next = ev.currentTarget.valueAsNumber;
              if (Number.isFinite(next)) void setWaterfallUpdatePeriod(next);
            }}
            style={numberInput}
          />
          <span style={inlineUnit}>frames</span>
        </div>
        <div style={presetGroup} role="group" aria-label="Waterfall update interval presets">
          {WATERFALL_INTERVAL_PRESETS.map((value) => (
            <PresetButton
              key={value}
              active={waterfallUpdatePeriod === value}
              label={`${value}`}
              onClick={() => void setWaterfallUpdatePeriod(value)}
            />
          ))}
        </div>
        <span style={hint}>
          Waterfall delay: {waterfallDelayMs.toFixed(1)} ms. Higher FPS and lower decimation increase display load.
        </span>
      </div>
    </section>
  );
}

function PresetButton({
  active,
  label,
  suffix,
  onClick,
}: {
  active: boolean;
  label: string;
  suffix?: string;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      aria-pressed={active}
      onClick={onClick}
      style={{
        ...rateButton,
        ...(active ? activeRateButton : null),
      }}
    >
      {label}{suffix ? ` ${suffix}` : ''}
    </button>
  );
}

const sectionHead: CSSProperties = {
  display: 'flex',
  alignItems: 'baseline',
  flexWrap: 'wrap',
  gap: 10,
  marginBottom: 10,
};

const sectionH3: CSSProperties = {
  margin: 0,
  fontSize: 11,
  fontWeight: 700,
  letterSpacing: 0,
  textTransform: 'uppercase',
  color: 'var(--fg-0)',
};

const sectionP: CSSProperties = {
  margin: 0,
  flex: '1 1 260px',
  fontSize: 12,
  lineHeight: 1.5,
  color: 'var(--fg-2)',
};

const card: CSSProperties = {
  display: 'grid',
  gap: 10,
  padding: 10,
  border: '1px solid var(--line)',
  borderRadius: 'var(--r-md)',
  background: 'linear-gradient(180deg, var(--bg-1), var(--bg-0))',
};

const controlRow: CSSProperties = {
  display: 'grid',
  gridTemplateColumns: 'minmax(120px, 1fr) 86px auto',
  alignItems: 'center',
  gap: 8,
};

const label: CSSProperties = {
  minWidth: 0,
  fontSize: 11,
  fontWeight: 700,
  color: 'var(--fg-1)',
};

const numberInput: CSSProperties = {
  width: '100%',
  height: 30,
  border: '1px solid var(--line)',
  borderRadius: 6,
  background: 'var(--bg-0)',
  color: 'var(--fg-0)',
  fontSize: 12,
  fontWeight: 800,
  padding: '0 8px',
};

const inlineUnit: CSSProperties = {
  fontSize: 11,
  color: 'var(--fg-3)',
};

const presetGroup: CSSProperties = {
  display: 'grid',
  gridTemplateColumns: 'repeat(auto-fit, minmax(54px, 1fr))',
  gap: 6,
};

const rateButton: CSSProperties = {
  minWidth: 0,
  height: 32,
  border: '1px solid var(--line)',
  borderRadius: 6,
  background: 'var(--bg-0)',
  color: 'var(--fg-1)',
  fontSize: 11,
  fontWeight: 800,
  letterSpacing: 0,
  cursor: 'pointer',
};

const activeRateButton: CSSProperties = {
  borderColor: 'color-mix(in srgb, var(--accent) 72%, var(--line))',
  background: 'color-mix(in srgb, var(--accent) 18%, var(--bg-0))',
  color: 'var(--fg-0)',
};

const hint: CSSProperties = {
  fontSize: 11,
  lineHeight: 1.35,
  color: 'var(--fg-3)',
};
