import { useTxStore } from '../state/tx-store';

// PRD FR-6: server-generated amber banner shown until the operator dismisses.
// Single-hue amber per the project color convention — no rose/red even for a
// protection event. The trip itself is already visible (MOX drops, meters
// zero); the banner's job is to explain why.
export function AlertBanner() {
  const alert = useTxStore((s) => s.alert);
  const setAlert = useTxStore((s) => s.setAlert);
  // Always render a container so the parent grid keeps a fixed number of
  // child rows. Returning null collapsed the 1fr panadapter track because
  // the 8-row template outran the 7 DOM children, leaving the panadapter
  // container pinned to an `auto` row with 0 intrinsic height.
  // Always render something so the parent grid keeps a stable row count —
  // returning null collapses the grid track below (see App.tsx grid-template).
  if (alert == null) return <div aria-hidden style={{ height: 0 }} />;
  return (
    <div
      role="alert"
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: 10,
        padding: '6px 10px',
        margin: '0 6px',
        background: 'linear-gradient(180deg, rgba(255,160,40,0.18), rgba(180,84,16,0.18))',
        border: '1px solid rgba(255,160,40,0.45)',
        borderRadius: 'var(--r-md)',
        color: 'var(--power)',
        fontSize: 12,
      }}
    >
      <span className="label-xs" style={{ color: 'var(--power)' }}>
        ALERT
      </span>
      <span className="mono" style={{ flex: 1, color: 'var(--fg-0)' }}>
        {alert.message}
      </span>
      <button type="button" onClick={() => setAlert(null)} className="btn sm">
        Dismiss
      </button>
    </div>
  );
}
