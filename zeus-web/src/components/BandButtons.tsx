import { useCallback, useState, useEffect } from 'react';
import { setVfo } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { BANDS, bandOf } from './design/data';

type BandEntry = {
  name: string;
  centerHz: number;
  rangeStart: number;
  rangeEnd: number;
};

// HF bands only (160m-10m) for Hermes Lite 2 coverage
const HF_BANDS: readonly BandEntry[] = BANDS.slice(0, 10).map((b) => ({
  name: b.n + 'm',
  centerHz: b.center,
  rangeStart: b.range[0],
  rangeEnd: b.range[1],
}));

// LocalStorage key for persisting last-used frequency per band
const STORAGE_KEY_PREFIX = 'zeus.band.';

function getLastFrequency(bandName: string, defaultHz: number): number {
  const stored = localStorage.getItem(STORAGE_KEY_PREFIX + bandName);
  if (!stored) return defaultHz;
  const parsed = Number(stored);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : defaultHz;
}

function saveLastFrequency(bandName: string, hz: number): void {
  localStorage.setItem(STORAGE_KEY_PREFIX + bandName, String(hz));
}

export function BandButtons() {
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const applyState = useConnectionStore((s) => s.applyState);

  // Track current band for active state
  const [currentBand, setCurrentBand] = useState<string>(() => bandOf(vfoHz));

  // Update current band whenever VFO changes
  useEffect(() => {
    const band = bandOf(vfoHz);
    setCurrentBand(band);
    // Save the current frequency for this band
    if (band !== '—') {
      saveLastFrequency(band, vfoHz);
    }
  }, [vfoHz]);

  const selectBand = useCallback(
    (band: BandEntry) => {
      const targetHz = getLastFrequency(band.name, band.centerHz);
      useConnectionStore.setState({ vfoHz: targetHz });
      setVfo(targetHz)
        .then(applyState)
        .catch(() => {
          /* next state poll will reconcile */
        });
    },
    [applyState],
  );

  return (
    <>
      {/* Desktop: horizontal row of buttons */}
      <div className="ctrl-group hide-mobile">
        <div className="label-xs ctrl-lbl">BAND</div>
        <div className="btn-row wrap" style={{ width: 'auto', maxWidth: 480 }}>
          {HF_BANDS.map((band) => (
            <button
              key={band.name}
              type="button"
              onClick={() => selectBand(band)}
              className={`btn sm ${currentBand === band.name ? 'active' : ''}`}
            >
              {band.name}
            </button>
          ))}
        </div>
      </div>

      {/* Mobile: dropdown */}
      <div className="ctrl-group show-mobile" style={{ display: 'none' }}>
        <div className="label-xs ctrl-lbl">BAND</div>
        <select
          value={currentBand}
          onChange={(e) => {
            const band = HF_BANDS.find((b) => b.name === e.target.value);
            if (band) selectBand(band);
          }}
          className="band-select"
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
          {HF_BANDS.map((band) => (
            <option key={band.name} value={band.name}>
              {band.name}
            </option>
          ))}
        </select>
      </div>
    </>
  );
}
