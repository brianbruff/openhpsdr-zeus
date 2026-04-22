import { useTxStore } from '../state/tx-store';

// HL2 PA temperature chip for the transport bar.
//
// Source: MsgType 0x17 (PaTempFrame) at 2 Hz, already clamped server-side to
// [-40, 125] °C. The HL2 Q6 sensor drives the gateware auto-shutdown at 55 °C,
// so the operator needs a quick at-a-glance read with headroom warning:
//   < 50 °C         — normal      (default chip color)
//   50 ≤ t < 55 °C  — warning     (amber — var(--orange))
//   ≥ 55 °C         — danger      (red   — var(--tx))
//
// Format matches the task spec: one decimal below 50 °C (precision matters
// at low temps), integer at/above 50 °C (visual clarity in the warning band).
// Null reading (no sample yet / disconnected) renders em-dash.

const WARN_C = 50;
const DANGER_C = 55;

function formatTempC(c: number): string {
  return c >= WARN_C ? `${Math.round(c)}` : c.toFixed(1);
}

export function PaTempChip() {
  const paTempC = useTxStore((s) => s.paTempC);
  const hasReading = paTempC !== null && Number.isFinite(paTempC);
  const danger = hasReading && paTempC >= DANGER_C;
  const warn = hasReading && !danger && paTempC >= WARN_C;
  const valueColor = danger
    ? 'var(--tx)'
    : warn
      ? 'var(--orange)'
      : 'var(--fg-0)';
  const display = hasReading ? `${formatTempC(paTempC)} °C` : '— °C';
  return (
    <div
      className="chip hide-mobile"
      title="HL2 PA temperature (Q6 sensor). 55 °C auto-shutdown threshold."
      aria-label={hasReading ? `PA temperature ${display}` : 'PA temperature no reading'}
      style={
        danger
          ? {
              // Subtle red glow on shutdown-imminent; no new hue introduced,
              // just the existing --tx-soft token used elsewhere for TX chrome.
              boxShadow: '0 0 6px var(--tx-soft)',
            }
          : undefined
      }
    >
      <span className="k">PA TEMP</span>
      <span className="v mono" style={{ color: valueColor }}>
        {display}
      </span>
    </div>
  );
}
