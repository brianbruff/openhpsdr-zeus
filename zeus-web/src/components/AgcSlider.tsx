import { useCallback, useEffect, useRef, useState } from 'react';
import { setAgcTop } from '../api/client';
import { useConnectionStore } from '../state/connection-store';

// AGC top (max gain) in dB. 80 is the deskhpsdr AGC_MEDIUM default; the
// WDSP docs call this the upper gain limit before compression kicks in.
// 0-120 mirrors the range Thetis exposes on its AGC-T slider.
const MIN = 0;
const MAX = 120;

export function AgcSlider() {
  const serverAgc = useConnectionStore((s) => s.agcTopDb);
  const connected = useConnectionStore((s) => s.status === 'Connected');
  const applyState = useConnectionStore((s) => s.applyState);

  // Local drag state overrides the store while the user is actively moving
  // the slider so echoed state updates don't yank the thumb back.
  const [dragValue, setDragValue] = useState<number | null>(null);
  const value = dragValue ?? serverAgc;

  const inflightAbort = useRef<AbortController | null>(null);
  const latestSent = useRef<number>(serverAgc);

  const sendValue = useCallback(
    (v: number) => {
      if (v === latestSent.current) return;
      latestSent.current = v;
      inflightAbort.current?.abort();
      const ac = new AbortController();
      inflightAbort.current = ac;
      setAgcTop(v, ac.signal)
        .then((next) => {
          if (!ac.signal.aborted) applyState(next);
        })
        .catch(() => {
          /* next poll will reconcile; don't noisily log on abort */
        });
    },
    [applyState],
  );

  useEffect(() => () => inflightAbort.current?.abort(), []);

  return (
    <label className="knob-group" style={{ minWidth: 170 }}>
      <span className="label-xs" style={{ whiteSpace: 'nowrap' }}>AGC-T</span>
      <input
        type="range"
        min={MIN}
        max={MAX}
        step={1}
        value={value}
        disabled={!connected}
        onChange={(e) => setDragValue(Number(e.currentTarget.value))}
        onMouseUp={() => {
          if (dragValue !== null) sendValue(dragValue);
          setDragValue(null);
        }}
        onTouchEnd={() => {
          if (dragValue !== null) sendValue(dragValue);
          setDragValue(null);
        }}
        onKeyUp={() => {
          if (dragValue !== null) sendValue(dragValue);
          setDragValue(null);
        }}
        style={{ flex: 1, cursor: 'pointer', accentColor: 'var(--accent)' }}
      />
      <span className="mono" style={{ width: 48, textAlign: 'right', color: 'var(--fg-1)', fontSize: 11 }}>
        {value} dB
      </span>
    </label>
  );
}
