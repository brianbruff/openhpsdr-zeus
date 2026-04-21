import { useCallback, useRef } from 'react';
import { setMox } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useTxStore } from '../state/tx-store';

// Press-and-hold PTT for mobile. Pointer events mirror the spacebar-PTT
// pattern in use-keyboard-shortcuts — driveMox(true) on pointerdown,
// driveMox(false) on pointerup/cancel/leave so a drag off the button still
// releases the key. setPointerCapture keeps the up event owned by this
// element even if the finger strays outside the hit area.
export function MobilePttButton() {
  const connected = useConnectionStore((s) => s.status === 'Connected');
  const moxOn = useTxStore((s) => s.moxOn);
  const setMoxOn = useTxStore((s) => s.setMoxOn);
  const abortRef = useRef<AbortController | null>(null);

  const drive = useCallback(
    (on: boolean) => {
      if (useTxStore.getState().moxOn === on) return;
      setMoxOn(on);
      abortRef.current?.abort();
      const ctrl = new AbortController();
      abortRef.current = ctrl;
      setMox(on, ctrl.signal).catch(() => {
        if (!ctrl.signal.aborted) setMoxOn(!on);
      });
    },
    [setMoxOn],
  );

  const onDown = useCallback(
    (e: React.PointerEvent<HTMLButtonElement>) => {
      if (!connected) return;
      e.currentTarget.setPointerCapture(e.pointerId);
      drive(true);
    },
    [connected, drive],
  );

  const onUp = useCallback(
    (e: React.PointerEvent<HTMLButtonElement>) => {
      if (e.currentTarget.hasPointerCapture(e.pointerId)) {
        e.currentTarget.releasePointerCapture(e.pointerId);
      }
      drive(false);
    },
    [drive],
  );

  return (
    <button
      type="button"
      disabled={!connected}
      className={`mobile-ptt-btn ${moxOn ? 'tx' : ''}`}
      onPointerDown={onDown}
      onPointerUp={onUp}
      onPointerCancel={onUp}
      onContextMenu={(e) => e.preventDefault()}
    >
      <span className={`led ${moxOn ? 'tx' : 'on'}`} />
      <span className="mobile-ptt-label">{moxOn ? 'TX' : 'PTT'}</span>
      <span className="mobile-ptt-hint">{moxOn ? 'release to stop' : 'hold to transmit'}</span>
    </button>
  );
}
