import { useCallback, useEffect, useRef, useState } from 'react';
import {
  fetchBandMemory,
  saveBandMemory,
  setMode,
  setVfo,
  type BandMemoryEntry,
  type RxMode,
} from '../api/client';
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

// Debounce the "save current (hz, mode) for the current band" write so tuning
// the VFO doesn't hammer the server on every pixel of knob travel.
const SAVE_DEBOUNCE_MS = 500;

export function BandButtons() {
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const mode = useConnectionStore((s) => s.mode);
  const applyState = useConnectionStore((s) => s.applyState);

  const [currentBand, setCurrentBand] = useState<string>(() => bandOf(vfoHz));

  // In-memory mirror of the server's band memory. Populated from the
  // /api/bands/memory GET on mount and kept in sync with our own PUTs so a
  // band click can apply the saved (hz, mode) without an extra round-trip.
  const memoryRef = useRef<Map<string, BandMemoryEntry>>(new Map());
  const saveTimerRef = useRef<number | null>(null);

  // Initial load of server-persisted band memory
  useEffect(() => {
    const ac = new AbortController();
    fetchBandMemory(ac.signal)
      .then((entries) => {
        const m = new Map<string, BandMemoryEntry>();
        for (const e of entries) m.set(e.band, e);
        memoryRef.current = m;
      })
      .catch(() => {
        /* offline / older server — band click will just use center defaults */
      });
    return () => ac.abort();
  }, []);

  // Track current band + debounced save of (hz, mode) for that band
  useEffect(() => {
    const band = bandOf(vfoHz);
    setCurrentBand(band);
    if (band === '—') return;

    if (saveTimerRef.current !== null) {
      window.clearTimeout(saveTimerRef.current);
    }
    saveTimerRef.current = window.setTimeout(() => {
      saveTimerRef.current = null;
      memoryRef.current.set(band, { band, hz: vfoHz, mode });
      saveBandMemory(band, vfoHz, mode).catch(() => {
        /* best-effort — next tune will retry */
      });
    }, SAVE_DEBOUNCE_MS);

    return () => {
      if (saveTimerRef.current !== null) {
        window.clearTimeout(saveTimerRef.current);
        saveTimerRef.current = null;
      }
    };
  }, [vfoHz, mode]);

  const selectBand = useCallback(
    (band: BandEntry) => {
      const stored = memoryRef.current.get(band.name);
      const targetHz = stored?.hz ?? band.centerHz;
      const targetMode: RxMode | null = stored?.mode ?? null;

      useConnectionStore.setState({ vfoHz: targetHz });
      setVfo(targetHz)
        .then(applyState)
        .catch(() => {
          /* next state poll will reconcile */
        });

      if (targetMode && targetMode !== mode) {
        useConnectionStore.setState({ mode: targetMode });
        setMode(targetMode)
          .then(applyState)
          .catch(() => {
            /* next state poll will reconcile */
          });
      }
    },
    [applyState, mode],
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
