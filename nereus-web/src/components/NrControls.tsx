import { useCallback, useEffect, useRef } from 'react';
import {
  setNr,
  type NbMode,
  type NrConfigDto,
  type NrMode,
} from '../api/client';
import { useConnectionStore } from '../state/connection-store';

// NR-button cycle mirrors Thetis: Off → NR1 (ANR, time-domain LMS) → NR2
// (EMNR, Ephraim–Malah spectral). ANR and EMNR are mutually exclusive in
// WDSP so both ride the one enum.
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

const ACTIVE_BTN = 'btn sm active';
const IDLE_BTN = 'btn sm';
const DISABLED = '';

export function NrControls() {
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
    <div className="btn-row">
      <button
        type="button"
        disabled={!connected}
        onClick={cycleNb}
        className={`${nbActive ? ACTIVE_BTN : IDLE_BTN} ${DISABLED}`}
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
      <button
        type="button"
        disabled={!connected}
        onClick={cycleNr}
        className={`${nrActive ? ACTIVE_BTN : IDLE_BTN} ${DISABLED}`}
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
        className={`${nr.anfEnabled ? ACTIVE_BTN : IDLE_BTN} ${DISABLED}`}
        title="ANF — adaptive auto-notch (time domain)"
      >
        ANF
      </button>
      <button
        type="button"
        disabled={!connected}
        onClick={toggleSnb}
        className={`${nr.snbEnabled ? ACTIVE_BTN : IDLE_BTN} ${DISABLED}`}
        title="SNB — spectral noise blanker"
      >
        SNB
      </button>
      <button
        type="button"
        disabled={!connected}
        onClick={toggleNbp}
        className={`${nr.nbpNotchesEnabled ? ACTIVE_BTN : IDLE_BTN} ${DISABLED}`}
        title="NBP — notch-filter auto-notch (RXA)"
      >
        NBP
      </button>
    </div>
  );
}
