import { useCallback, useEffect, useRef, useState } from 'react';
import { setAttenuator, setAutoAtt } from '../api/client';
import { useConnectionStore } from '../state/connection-store';

// HpsdrAtten range — the server clamps to [MIN, MAX], but pinning the UI
// to the same bounds avoids a round-trip that would visually snap the thumb.
const MIN = 0;
const MAX = 31;

export function AttenuatorSlider() {
  const userAtten = useConnectionStore((s) => s.attenDb);
  const offsetDb = useConnectionStore((s) => s.attOffsetDb);
  const autoEnabled = useConnectionStore((s) => s.autoAttEnabled);
  const overload = useConnectionStore((s) => s.adcOverloadWarning);
  const connected = useConnectionStore((s) => s.status === 'Connected');
  const applyState = useConnectionStore((s) => s.applyState);

  const [dragValue, setDragValue] = useState<number | null>(null);
  // Slider thumb edits the user baseline (attenDb); the displayed number shows
  // the effective atten on the hardware so the user can watch the auto ramp.
  const sliderValue = dragValue ?? userAtten;
  const effective = Math.min(MAX, sliderValue + offsetDb);

  const attenAbort = useRef<AbortController | null>(null);
  const latestSent = useRef<number>(userAtten);
  const autoAbort = useRef<AbortController | null>(null);

  const sendValue = useCallback(
    (v: number) => {
      if (v === latestSent.current) return;
      latestSent.current = v;
      attenAbort.current?.abort();
      const ac = new AbortController();
      attenAbort.current = ac;
      setAttenuator(v, ac.signal)
        .then((next) => {
          if (!ac.signal.aborted) applyState(next);
        })
        .catch(() => {
          /* next poll will reconcile; don't noisily log on abort */
        });
    },
    [applyState],
  );

  const toggleAuto = useCallback(() => {
    if (!connected) return;
    autoAbort.current?.abort();
    const ac = new AbortController();
    autoAbort.current = ac;
    setAutoAtt(!autoEnabled, ac.signal)
      .then((next) => {
        if (!ac.signal.aborted) applyState(next);
      })
      .catch(() => {
        /* state subscription will reconcile on next broadcast */
      });
  }, [autoEnabled, connected, applyState]);

  useEffect(
    () => () => {
      attenAbort.current?.abort();
      autoAbort.current?.abort();
    },
    [],
  );

  return (
    <label className="knob-group">
      <button
        type="button"
        onClick={toggleAuto}
        disabled={!connected}
        aria-pressed={autoEnabled}
        aria-label={autoEnabled ? 'Auto attenuator on' : 'Auto attenuator off'}
        title={
          autoEnabled
            ? 'Auto-ATT ON (click to disable)'
            : 'Auto-ATT OFF (click to enable)'
        }
        className={`btn sm ${autoEnabled ? 'active' : ''} ${overload ? 'overload' : ''}`}
      >
        {autoEnabled ? 'A-ATT' : 'S-ATT'}
      </button>
      <input
        type="range"
        min={MIN}
        max={MAX}
        step={1}
        value={sliderValue}
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
      <span className="mono" style={{ width: 48, textAlign: 'right', color: overload ? 'var(--power)' : 'var(--fg-1)', fontSize: 11 }}>
        {effective} dB
      </span>
    </label>
  );
}
