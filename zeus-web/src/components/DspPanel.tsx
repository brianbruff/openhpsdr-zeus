import { useCallback, useEffect, useRef } from 'react';
import {
  setLevelerMaxGain,
  setNr,
  type NbMode,
  type NrConfigDto,
  type NrMode,
} from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useTxStore } from '../state/tx-store';
import { Slider } from './design/Slider';

// Leveler max-gain slider bounds — matches backend clamp and the HL2
// community-recommended range. 0.5 dB steps give a useful resolution
// without flooding the POST endpoint.
const LVLR_MIN_DB = 0;
const LVLR_MAX_DB = 15;
const LVLR_STEP_DB = 0.5;
const LVLR_DEBOUNCE_MS = 100;

function quantizeLvlr(v: number): number {
  const snapped = Math.round(v / LVLR_STEP_DB) * LVLR_STEP_DB;
  // JS float artefacts: round to 1 decimal so "5.0" stays "5.0".
  return Math.round(snapped * 10) / 10;
}

// Mirrors NrControls.tsx — cycle order matches Thetis WDSP semantics. ANR
// and EMNR are mutually exclusive in WDSP so both ride the single nrMode.
const NR_CYCLE: readonly NrMode[] = ['Off', 'Anr', 'Emnr'];
const NR_LABEL: Record<NrMode, string> = {
  Off: 'NR',
  Anr: 'NR',
  Emnr: 'NR2',
};

const NB_CYCLE: readonly NbMode[] = ['Off', 'Nb1', 'Nb2'];
const NB_LABEL: Record<NbMode, string> = {
  Off: 'NB',
  Nb1: 'NB1',
  Nb2: 'NB2',
};

export function DspPanel() {
  const nr = useConnectionStore((s) => s.nr);
  const setLocalNr = useConnectionStore((s) => s.setNr);
  const applyState = useConnectionStore((s) => s.applyState);
  const connected = useConnectionStore((s) => s.status === 'Connected');

  const levelerMaxGainDb = useTxStore((s) => s.levelerMaxGainDb);
  const setLevelerMaxGainDb = useTxStore((s) => s.setLevelerMaxGainDb);

  const inflightAbort = useRef<AbortController | null>(null);
  const lvlrInflight = useRef<AbortController | null>(null);
  const lvlrDebounce = useRef<ReturnType<typeof setTimeout> | null>(null);
  const lvlrLastSent = useRef<number>(levelerMaxGainDb);
  const lvlrPrevOnError = useRef<number>(levelerMaxGainDb);
  useEffect(
    () => () => {
      inflightAbort.current?.abort();
      lvlrInflight.current?.abort();
      if (lvlrDebounce.current != null) clearTimeout(lvlrDebounce.current);
    },
    [],
  );

  const sendLvlrDebounced = useCallback(
    (v: number) => {
      if (lvlrDebounce.current != null) clearTimeout(lvlrDebounce.current);
      lvlrDebounce.current = setTimeout(() => {
        if (v === lvlrLastSent.current) return;
        lvlrInflight.current?.abort();
        const ac = new AbortController();
        lvlrInflight.current = ac;
        const prevValue = lvlrLastSent.current;
        lvlrLastSent.current = v;
        lvlrPrevOnError.current = prevValue;
        setLevelerMaxGain(v, ac.signal)
          .then((r) => {
            if (ac.signal.aborted) return;
            if (r.levelerMaxGainDb !== v) setLevelerMaxGainDb(r.levelerMaxGainDb);
          })
          .catch((err) => {
            if (ac.signal.aborted) return;
            if (err instanceof DOMException && err.name === 'AbortError') return;
            // Roll back the optimistic store update on non-abort failures.
            setLevelerMaxGainDb(lvlrPrevOnError.current);
            lvlrLastSent.current = lvlrPrevOnError.current;
          });
      }, LVLR_DEBOUNCE_MS);
    },
    [setLevelerMaxGainDb],
  );

  const onLvlrChange = useCallback(
    (v: number) => {
      const q = quantizeLvlr(v);
      setLevelerMaxGainDb(q);
      sendLvlrDebounced(q);
    },
    [setLevelerMaxGainDb, sendLvlrDebounced],
  );

  const send = useCallback(
    (next: NrConfigDto) => {
      setLocalNr(next);
      inflightAbort.current?.abort();
      const ac = new AbortController();
      inflightAbort.current = ac;
      setNr(next, ac.signal)
        .then((s) => {
          if (!ac.signal.aborted) applyState(s);
        })
        .catch(() => {
          /* next state poll will reconcile */
        });
    },
    [setLocalNr, applyState],
  );

  const cycleNr = useCallback(() => {
    const idx = NR_CYCLE.indexOf(nr.nrMode);
    const nextIdx = (idx < 0 ? 0 : idx + 1) % NR_CYCLE.length;
    send({ ...nr, nrMode: NR_CYCLE[nextIdx]! });
  }, [nr, send]);

  const cycleNb = useCallback(() => {
    const idx = NB_CYCLE.indexOf(nr.nbMode);
    const nextIdx = (idx < 0 ? 0 : idx + 1) % NB_CYCLE.length;
    send({ ...nr, nbMode: NB_CYCLE[nextIdx]! });
  }, [nr, send]);

  const setNbThreshold = useCallback(
    (v: number) => send({ ...nr, nbThreshold: v }),
    [nr, send],
  );

  const toggleAnf = useCallback(
    () => send({ ...nr, anfEnabled: !nr.anfEnabled }),
    [nr, send],
  );
  const toggleSnb = useCallback(
    () => send({ ...nr, snbEnabled: !nr.snbEnabled }),
    [nr, send],
  );
  const toggleNbp = useCallback(
    () => send({ ...nr, nbpNotchesEnabled: !nr.nbpNotchesEnabled }),
    [nr, send],
  );

  const nrActive = nr.nrMode !== 'Off';
  const nbActive = nr.nbMode !== 'Off';

  return (
    <div className="dsp-grid">
      <div className="dsp-row">
        <button
          type="button"
          disabled={!connected}
          onClick={cycleNb}
          className={`btn sm ${nbActive ? 'active' : ''}`}
          title={
            nr.nbMode === 'Off'
              ? 'Noise blanker off'
              : nr.nbMode === 'Nb1'
                ? 'NB1 (time-domain blanker, xanbEXT)'
                : 'NB2 (time-domain blanker, xnobEXT)'
          }
        >
          {NB_LABEL[nr.nbMode]}
        </button>
        <Slider
          label="Thresh"
          value={nr.nbThreshold}
          onChange={setNbThreshold}
          disabled={!connected || !nbActive}
        />
      </div>
      <div className="dsp-row">
        <button
          type="button"
          disabled={!connected}
          onClick={cycleNr}
          className={`btn sm ${nrActive ? 'active' : ''}`}
          title={
            nr.nrMode === 'Off'
              ? 'Noise reduction off'
              : nr.nrMode === 'Anr'
                ? 'NR1 (ANR, time-domain LMS)'
                : 'NR2 (EMNR, spectral)'
          }
        >
          {NR_LABEL[nr.nrMode]}
        </button>
        <button
          type="button"
          disabled={!connected}
          onClick={toggleAnf}
          className={`btn sm ${nr.anfEnabled ? 'active' : ''}`}
          title="ANF — adaptive auto-notch (time domain)"
        >
          ANF
        </button>
        <button
          type="button"
          disabled={!connected}
          onClick={toggleSnb}
          className={`btn sm ${nr.snbEnabled ? 'active' : ''}`}
          title="SNB — spectral noise blanker"
        >
          SNB
        </button>
        <button
          type="button"
          disabled={!connected}
          onClick={toggleNbp}
          className={`btn sm ${nr.nbpNotchesEnabled ? 'active' : ''}`}
          title="NBP — notch-filter auto-notch (RXA)"
        >
          NBP
        </button>
      </div>
      <div
        className="dsp-row"
        title="How much the Leveler can boost quiet speech. +5 dB is the community-recommended starting point. Higher = more aggressive voice leveling, but can push ALC into limiting."
      >
        <Slider
          label="Leveler Max Gain"
          value={levelerMaxGainDb}
          onChange={onLvlrChange}
          min={LVLR_MIN_DB}
          max={LVLR_MAX_DB}
          formatValue={(v) => `+${v.toFixed(1)} dB`}
          disabled={!connected}
        />
      </div>
    </div>
  );
}
