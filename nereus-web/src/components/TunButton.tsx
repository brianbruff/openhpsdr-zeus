import { useCallback } from 'react';
import { setTun } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useTxStore } from '../state/tx-store';

/**
 * PRD FR-7: TUN keys a single-tone carrier (WDSP SetTXAPostGen*) and is
 * mutually exclusive with MOX — the store setters enforce the exclusion so
 * the click handler stays dumb. Server clamps drive to min(drive, 25%) while
 * TUN is on; the DriveSlider reflects whatever StateDto reports back.
 */
export function TunButton() {
  const connected = useConnectionStore((s) => s.status === 'Connected');
  const tunOn = useTxStore((s) => s.tunOn);
  const setTunOn = useTxStore((s) => s.setTunOn);

  const click = useCallback(() => {
    const next = !tunOn;
    setTunOn(next);
    setTun(next).catch(() => {
      setTunOn(!next);
    });
  }, [tunOn, setTunOn]);

  return (
    <button
      type="button"
      disabled={!connected}
      onClick={click}
      className={`btn lg ${tunOn ? 'active' : ''}`}
      title={tunOn ? 'TUN on — single-tone carrier' : 'TUN off (single-tone carrier for tuning)'}
    >
      TUNE
    </button>
  );
}
