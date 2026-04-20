import { SMeter } from './SMeter';
import { useTxStore } from '../state/tx-store';

// Replaces SMeterDemo's animated harness with real tx-store telemetry. The
// SMeter component itself is unchanged (discriminated-union presentation
// component from PR #1). TX mode renders forward watts; RX mode renders the
// live rxDbm value pushed from DspPipelineService's 5 Hz RxMeterFrame.
//
// SWR and mic dBfs are surfaced alongside the meter only while MOX is on —
// they're TX-only telemetry and would be misleading under RX.

export function SMeterLive() {
  const moxOn = useTxStore((s) => s.moxOn);
  const tunOn = useTxStore((s) => s.tunOn);
  const fwdWatts = useTxStore((s) => s.fwdWatts);
  const swr = useTxStore((s) => s.swr);
  const micDbfs = useTxStore((s) => s.micDbfs);
  const rxDbm = useTxStore((s) => s.rxDbm);
  const transmitting = moxOn || tunOn;

  const swrColor = swr >= 3 ? 'var(--tx)' : swr >= 2 ? 'var(--power)' : 'var(--fg-0)';

  return (
    <div style={{ padding: 10, display: 'flex', flexDirection: 'column', gap: 8 }}>
      <div>
        {transmitting ? (
          <SMeter mode="tx" watts={fwdWatts} maxWatts={100} />
        ) : (
          <SMeter mode="rx" dbm={rxDbm} />
        )}
      </div>
      {transmitting && (
        <div style={{ display: 'flex', gap: 10, justifyContent: 'flex-end' }}>
          <span className="chip mono">
            <span className="k">SWR</span>
            <span className="v" style={{ color: swrColor }}>
              {swr.toFixed(2)}
            </span>
          </span>
          <span className="chip mono">
            <span className="k">MIC</span>
            <span className="v">{micDbfs.toFixed(0)} dBfs</span>
          </span>
        </div>
      )}
    </div>
  );
}
