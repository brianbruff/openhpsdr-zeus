import { useCallback, useEffect, useRef } from 'react';
import { setMicGain } from '../api/client';
import { useTxStore } from '../state/tx-store';

// PRD FR-3 mic-gain range: 0..+20 dB. Server applies via WDSP
// SetTXAPanelGain1(TXA, 10^(db/20)) — same linear dB curve Thetis uses in
// audio.cs:218-224. Debounce matches DriveSlider so a drag doesn't flood
// the endpoint; optimistic store update keeps the thumb responsive.
//
// Always enabled: the TXA panel gain persists across MOX off/on, so the
// operator can dial in level against the live mic meter before keying.
const MIN = 0;
const MAX = 20;
const DEBOUNCE_MS = 100;

export function MicGainSlider() {
  const micGainDb = useTxStore((s) => s.micGainDb);
  const setMicGainDb = useTxStore((s) => s.setMicGainDb);

  const inflightAbort = useRef<AbortController | null>(null);
  const debounceTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const lastSent = useRef<number>(micGainDb);
  const previousOnError = useRef<number>(micGainDb);

  const sendDebounced = useCallback((v: number) => {
    if (debounceTimer.current != null) clearTimeout(debounceTimer.current);
    debounceTimer.current = setTimeout(() => {
      if (v === lastSent.current) return;
      inflightAbort.current?.abort();
      const ac = new AbortController();
      inflightAbort.current = ac;
      const prevValue = lastSent.current;
      lastSent.current = v;
      previousOnError.current = prevValue;
      setMicGain(v, ac.signal)
        .then((r) => {
          if (ac.signal.aborted) return;
          if (r.micGainDb !== v) setMicGainDb(r.micGainDb);
        })
        .catch((err) => {
          if (ac.signal.aborted) return;
          if (err instanceof DOMException && err.name === 'AbortError') return;
          setMicGainDb(previousOnError.current);
          lastSent.current = previousOnError.current;
        });
    }, DEBOUNCE_MS);
  }, [setMicGainDb]);

  useEffect(() => () => {
    inflightAbort.current?.abort();
    if (debounceTimer.current != null) clearTimeout(debounceTimer.current);
  }, []);

  // Rounded on send / display so the wire contract stays integer dB, but the
  // slider itself uses 0.5-step so micro-drags cross a step boundary on the
  // ~128px-wide input. At step=1 on a 20-dB range, each step is ~6px — drags
  // under that threshold didn't move the thumb and the user had to click to
  // commit, which looked like "drag doesn't work". Fractional store value is
  // fine; the round happens at render + wire time.
  const onInput = (e: React.FormEvent<HTMLInputElement>) => {
    const v = Number(e.currentTarget.value);
    setMicGainDb(v);
    sendDebounced(Math.round(v));
  };

  return (
    <label className="knob-group">
      <span className="label-xs">MIC</span>
      <input
        type="range"
        min={MIN}
        max={MAX}
        step={0.5}
        value={micGainDb}
        onInput={onInput}
        onChange={onInput}
        style={{ flex: 1, cursor: 'pointer', accentColor: 'var(--accent)' }}
      />
      <span className="mono" style={{ width: 52, textAlign: 'right', color: 'var(--fg-1)', fontSize: 11 }}>
        +{Math.round(micGainDb)} dB
      </span>
    </label>
  );
}
