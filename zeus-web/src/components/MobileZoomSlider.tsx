import { useCallback, useEffect, useRef, useState } from 'react';
import { setZoom, ZOOM_MAX, ZOOM_MIN, type ZoomLevel } from '../api/client';
import { useConnectionStore } from '../state/connection-store';

/**
 * Vertical zoom slider pinned to the right edge of the panadapter on mobile.
 * Allows zooming without finger-dragging on the spectrum (which tunes frequency).
 * Hidden on desktop via CSS.
 */
export function MobileZoomSlider() {
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
    <div
      className="mobile-zoom-slider"
      style={{
        position: 'absolute',
        right: 0,
        top: 0,
        bottom: 0,
        width: 32,
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        zIndex: 10,
        pointerEvents: connected ? 'auto' : 'none',
        background: 'linear-gradient(90deg, transparent, rgba(255, 160, 40, 0.08))',
        borderLeft: '1px solid rgba(255, 160, 40, 0.15)',
      }}
    >
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
        style={{
          writingMode: 'vertical-lr',
          direction: 'rtl',
          height: '80%',
          cursor: 'pointer',
          accentColor: 'var(--accent)',
          opacity: connected ? 1 : 0.3,
        }}
        aria-label="Zoom level"
      />
      <span
        style={{
          position: 'absolute',
          bottom: 8,
          left: '50%',
          transform: 'translateX(-50%)',
          fontSize: 9,
          fontWeight: 700,
          color: 'var(--accent)',
          pointerEvents: 'none',
          textShadow: '0 0 4px rgba(255, 160, 40, 0.6)',
        }}
      >
        {value}×
      </span>
    </div>
  );
}
