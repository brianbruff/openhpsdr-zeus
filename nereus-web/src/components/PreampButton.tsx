import { useCallback } from 'react';
import { setPreamp } from '../api/client';
import { useConnectionStore } from '../state/connection-store';

// HermesLite2 has no hardware preamp — the RX gain path is firmware-controlled
// through the attenuator register (CC0=0x14), so a preamp toggle is a no-op
// that only confuses the user. Hide the button on HL2 entirely.
const HL2_BOARD_ID = 'HermesLite2';

export function PreampButton() {
  const boardId = useConnectionStore((s) => s.boardId);
  const preampOn = useConnectionStore((s) => s.preampOn);
  const setPreampOn = useConnectionStore((s) => s.setPreampOn);
  const connected = useConnectionStore((s) => s.status === 'Connected');

  const click = useCallback(() => {
    const next = !preampOn;
    setPreampOn(next);
    setPreamp(next).catch(() => {
      setPreampOn(!next);
    });
  }, [preampOn, setPreampOn]);

  if (boardId === HL2_BOARD_ID) return null;

  return (
    <button
      type="button"
      disabled={!connected}
      onClick={click}
      className={`btn sm ${preampOn ? 'active' : ''}`}
      title={preampOn ? 'Preamp on' : 'Preamp off'}
    >
      PRE
    </button>
  );
}
