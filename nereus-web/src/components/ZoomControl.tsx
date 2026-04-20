import { useCallback, useEffect, useRef, useState } from 'react';
import { setZoom, ZOOM_MAX, ZOOM_MIN, type ZoomLevel } from '../api/client';
import { useConnectionStore } from '../state/connection-store';

export function ZoomControl() {
  const serverZoom = useConnectionStore((s) => s.zoomLevel);
  const setLocalZoom = useConnectionStore((s) => s.setZoomLevel);
  const applyState = useConnectionStore((s) => s.applyState);
  const connected = useConnectionStore((s) => s.status === 'Connected');

  const [dragValue, setDragValue] = useState<ZoomLevel | null>(null);
  const value = dragValue ?? serverZoom;

  const inflightAbort = useRef<AbortController | null>(null);
  const latestSent = useRef<ZoomLevel>(serverZoom);

  const sendValue = useCallback(
    (v: ZoomLevel) => {
      if (v === latestSent.current) return;
      latestSent.current = v;
      setLocalZoom(v);
      inflightAbort.current?.abort();
      const ac = new AbortController();
      inflightAbort.current = ac;
      setZoom(v, ac.signal)
        .then((next) => {
          if (!ac.signal.aborted) applyState(next);
        })
        .catch(() => {
          /* next state poll will reconcile */
        });
    },
    [applyState, setLocalZoom],
  );

  useEffect(() => () => inflightAbort.current?.abort(), []);

  const commit = () => {
    if (dragValue !== null) sendValue(dragValue);
    setDragValue(null);
  };

  return (
    <label className="knob-group">
      <span className="label-xs">ZOOM</span>
      <input
        type="range"
        min={ZOOM_MIN}
        max={ZOOM_MAX}
        step={1}
        value={value}
        disabled={!connected}
        onChange={(e) => setDragValue(Number(e.currentTarget.value) as ZoomLevel)}
        onMouseUp={commit}
        onTouchEnd={commit}
        onKeyUp={commit}
        style={{ flex: 1, cursor: 'pointer', accentColor: 'var(--accent)' }}
      />
      <span className="mono" style={{ width: 40, textAlign: 'right', color: 'var(--fg-1)', fontSize: 11 }}>
        {value}×
      </span>
    </label>
  );
}
