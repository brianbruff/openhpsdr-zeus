import type { LogEntry } from './data';
import { SAMPLE_LOG } from './data';

type LogbookProps = {
  onPick?: (entry: LogEntry) => void;
};

export function Logbook({ onPick }: LogbookProps) {
  return (
    <div className="logbook">
      <div className="log-head mono">
        <span>Time</span>
        <span>Call</span>
        <span>Freq</span>
        <span>Mode</span>
        <span>RST</span>
        <span>Name</span>
      </div>
      <div className="log-rows">
        {SAMPLE_LOG.map((r, i) => (
          <button
            key={i}
            type="button"
            className="log-row mono"
            onClick={() => onPick?.(r)}
          >
            <span className="t-time">{r.time}</span>
            <span className="t-call">{r.call}</span>
            <span>{r.freq}</span>
            <span className="t-mode">{r.mode}</span>
            <span>{r.rst}</span>
            <span className="t-name">{r.name}</span>
          </button>
        ))}
      </div>
      <div className="log-foot">
        <button type="button" className="btn sm">+ Add QSO</button>
        <span style={{ flex: 1 }} />
        <span className="label-xs">{SAMPLE_LOG.length} of 1,247</span>
      </div>
    </div>
  );
}
