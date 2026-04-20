import { useCallback, useEffect, useRef } from 'react';
import { setDrive } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useTxStore } from '../state/tx-store';

// PRD FR-4 drive range: 0..100 percent. Per-pixel POSTs would flood the
// server during a drag — trailing-edge debounce keeps the wire quiet while
// still giving a responsive thumb because the store updates optimistically.
const MIN = 0;
const MAX = 100;
const DEBOUNCE_MS = 100;

export function DriveSlider() {
  const connected = useConnectionStore((s) => s.status === 'Connected');
  const drivePercent = useTxStore((s) => s.drivePercent);
  const setDrivePercent = useTxStore((s) => s.setDrivePercent);

  const inflightAbort = useRef<AbortController | null>(null);
  const debounceTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const lastSent = useRef<number>(drivePercent);
  const previousOnError = useRef<number>(drivePercent);

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
      setDrive(v, ac.signal)
        .then((r) => {
          if (ac.signal.aborted) return;
          if (r.drivePercent !== v) setDrivePercent(r.drivePercent);
        })
        .catch((err) => {
          if (ac.signal.aborted) return;
          if (err instanceof DOMException && err.name === 'AbortError') return;
          // Roll back the optimistic update so the user sees the real state.
          setDrivePercent(previousOnError.current);
          lastSent.current = previousOnError.current;
        });
    }, DEBOUNCE_MS);
  }, [setDrivePercent]);

  useEffect(() => () => {
    inflightAbort.current?.abort();
    if (debounceTimer.current != null) clearTimeout(debounceTimer.current);
  }, []);

  const onChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const v = Number(e.currentTarget.value);
    setDrivePercent(v);
    sendDebounced(v);
  };

  return (
    <label className="knob-group">
      <span className="label-xs">DRV</span>
      <input
        type="range"
        min={MIN}
        max={MAX}
        step={1}
        value={drivePercent}
        disabled={!connected}
        onChange={onChange}
        style={{ flex: 1, cursor: 'pointer', accentColor: 'var(--accent)' }}
      />
      <span className="mono" style={{ width: 40, textAlign: 'right', color: 'var(--power)', fontSize: 11, fontWeight: 700 }}>
        {drivePercent}%
      </span>
    </label>
  );
}
