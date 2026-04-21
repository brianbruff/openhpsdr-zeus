import { useEffect } from 'react';
import { useLoggerStore } from '../../state/logger-store';

export function LogbookLive() {
  const entries = useLoggerStore((s) => s.entries);
  const totalCount = useLoggerStore((s) => s.totalCount);
  const loading = useLoggerStore((s) => s.loading);
  const lastPublishResult = useLoggerStore((s) => s.lastPublishResult);
  const publishError = useLoggerStore((s) => s.publishError);
  const clearPublishResult = useLoggerStore((s) => s.clearPublishResult);
  const selectedIds = useLoggerStore((s) => s.selectedIds);
  const toggleSelected = useLoggerStore((s) => s.toggleSelected);

  useEffect(() => {
    // Self-clear publish feedback (shown in the Logbook header) after a few seconds.
    if (lastPublishResult || publishError) {
      const timer = setTimeout(() => {
        clearPublishResult();
      }, 4000);
      return () => clearTimeout(timer);
    }
  }, [lastPublishResult, publishError, clearPublishResult]);

  const formatTime = (isoString: string) => {
    const date = new Date(isoString);
    return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false });
  };

  const formatDate = (isoString: string) => {
    const date = new Date(isoString);
    return date.toLocaleDateString([], { month: 'short', day: 'numeric' });
  };

  if (loading && entries.length === 0) {
    return (
      <div className="logbook">
        <div className="log-rows" style={{ padding: '2rem', textAlign: 'center', opacity: 0.5 }}>
          Loading log entries...
        </div>
      </div>
    );
  }

  if (entries.length === 0) {
    return (
      <div className="logbook">
        <div className="log-rows" style={{ padding: '2rem', textAlign: 'center', opacity: 0.5 }}>
          No log entries yet. Log a QSO from the QRZ panel to get started.
        </div>
      </div>
    );
  }

  return (
    <div className="logbook">
      <div className="log-head mono">
        <span style={{ width: '2rem' }}>✓</span>
        <span>Date</span>
        <span>Time</span>
        <span>Call</span>
        <span>Freq</span>
        <span>Mode</span>
        <span>RST</span>
        <span>Name</span>
      </div>
      <div className="log-rows">
        {entries.map((entry) => (
          <button
            key={entry.id}
            type="button"
            className={`log-row mono ${selectedIds.has(entry.id) ? 'selected' : ''}`}
            onClick={() => toggleSelected(entry.id)}
          >
            <span style={{ width: '2rem' }}>
              <input
                type="checkbox"
                checked={selectedIds.has(entry.id)}
                readOnly
                tabIndex={-1}
                style={{ cursor: 'pointer', pointerEvents: 'none' }}
              />
            </span>
            <span className="t-date">{formatDate(entry.qsoDateTimeUtc)}</span>
            <span className="t-time">{formatTime(entry.qsoDateTimeUtc)}</span>
            <span className="t-call">{entry.callsign}</span>
            <span>{entry.frequencyMhz.toFixed(3)}</span>
            <span className="t-mode">{entry.mode}</span>
            <span>{entry.rstSent}/{entry.rstRcvd}</span>
            <span className="t-name">{entry.name ?? '—'}</span>
            {entry.qrzLogId && (
              <span style={{ marginLeft: '0.5rem', color: 'var(--accent)', fontSize: '0.7em' }}>
                ✓ QRZ
              </span>
            )}
          </button>
        ))}
      </div>
      <div className="log-foot">
        <span style={{ flex: 1 }} />
        <span className="label-xs">{entries.length} of {totalCount}</span>
      </div>
    </div>
  );
}
