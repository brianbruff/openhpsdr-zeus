import { useEffect } from 'react';
import { startMicUplink, type MicUplinkHandle } from './mic-uplink';
import { sendMicPcm } from '../realtime/ws-client';
import { useTxStore } from '../state/tx-store';
import { warnOnce } from '../util/logger';

// Silence floor for the mic meter — below this we clamp so the UI doesn't
// render -∞ and so the bar snaps to fully-empty on a quiet mic.
const MIC_DBFS_FLOOR = -100;

/**
 * Opens the mic AudioWorklet on mount and keeps it running while the app
 * is live. Peak dBFS of every 20 ms block is pushed to tx-store so the
 * MicMeter renders even on RX — the operator needs to know the mic is
 * being picked up *before* keying. Uplink samples are only forwarded to
 * the server when MOX is on; during RX the worklet still runs but the
 * wire path is a no-op.
 *
 * getUserMedia requires a user gesture on first grant, but Chrome remembers
 * the grant per-origin for the session, so the capture starts silently on
 * subsequent page loads once the operator has allowed it once.
 */
export function useMicUplink(): void {
  useEffect(() => {
    let handle: MicUplinkHandle | null = null;
    let disposed = false;

    startMicUplink((samples, peak) => {
      // Level: always pushed so MicMeter animates on RX.
      const dbfs = peak > 0
        ? Math.max(MIC_DBFS_FLOOR, 20 * Math.log10(peak))
        : MIC_DBFS_FLOOR;
      useTxStore.getState().setMicDbfs(dbfs);

      // Samples: only forwarded to the server while keyed. Capturing always +
      // gating here avoids a ~300 ms getUserMedia cold-start on every MOX.
      if (useTxStore.getState().moxOn) sendMicPcm(samples);
    })
      .then((h) => {
        if (disposed) { void h.stop(); return; }
        handle = h;
        useTxStore.getState().setMicError(null);
      })
      .catch((err: unknown) => {
        const msg = err instanceof Error ? err.message : String(err);
        warnOnce('mic-uplink-failed', `mic capture unavailable: ${msg}`);
        useTxStore.getState().setMicError(msg);
      });

    return () => {
      disposed = true;
      const h = handle;
      handle = null;
      if (h) void h.stop();
    };
  }, []);
}
