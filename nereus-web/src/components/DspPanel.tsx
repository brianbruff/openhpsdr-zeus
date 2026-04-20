import { useCallback, useEffect, useRef } from 'react';
import {
  setNr,
  type NbMode,
  type NrConfigDto,
  type NrMode,
} from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { Slider } from './design/Slider';

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

  const inflightAbort = useRef<AbortController | null>(null);
  useEffect(() => () => inflightAbort.current?.abort(), []);

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
    </div>
  );
}
