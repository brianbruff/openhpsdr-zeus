import { useCallback } from 'react';
import { setMox } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useTxStore } from '../state/tx-store';

/**
 * Click to toggle MOX. Optimistic update with rollback on server refusal
 * (e.g., 409 "not connected"). Spacebar PTT lives in use-keyboard-shortcuts
 * and shares the same tx-store flag so the button visually tracks the key.
 */
export function MoxButton() {
  const connected = useConnectionStore((s) => s.status === 'Connected');
  const moxOn = useTxStore((s) => s.moxOn);
  const setMoxOn = useTxStore((s) => s.setMoxOn);

  const click = useCallback(() => {
    const next = !moxOn;
    setMoxOn(next);
    setMox(next).catch(() => {
      setMoxOn(!next);
    });
  }, [moxOn, setMoxOn]);

  return (
    <button
      type="button"
      disabled={!connected}
      onClick={click}
      className={`btn lg tx-btn ${moxOn ? 'tx' : ''}`}
      title={moxOn ? 'MOX on — transmitting' : 'MOX off (hold Space to key)'}
    >
      <span className={`led ${moxOn ? 'tx' : ''}`} style={{ marginRight: 8 }} />
      {moxOn ? 'TX' : 'MOX'}
    </button>
  );
}
