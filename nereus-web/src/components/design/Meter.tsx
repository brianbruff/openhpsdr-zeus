type MeterProps = {
  label: string;
  value: number;
  max: number;
  unit: string;
  /** 0..1 position of the "danger" tick; when set, the bar turns red past this fraction */
  danger?: number;
};

export function Meter({ label, value, max, unit, danger }: MeterProps) {
  const clamped = Math.max(0, Math.min(max, value));
  const pct = (clamped / max) * 100;
  const over = danger != null && clamped / max > danger;
  return (
    <div className="meter">
      <div className="meter-head">
        <span className="label-xs">{label}</span>
        <span className="meter-val mono">
          {clamped.toFixed(1)}
          <span className="unit"> {unit}</span>
        </span>
      </div>
      <div className="meter-bar">
        <div
          className="meter-fill"
          style={{ width: `${pct}%`, filter: over ? 'hue-rotate(-20deg) saturate(1.4)' : undefined }}
        />
        <div className="meter-ticks">
          {[0.25, 0.5, 0.75].map((t) => (
            <div key={t} className="meter-tick" style={{ left: `${t * 100}%` }} />
          ))}
          {danger != null && (
            <div className="meter-tick danger" style={{ left: `${danger * 100}%` }} />
          )}
        </div>
      </div>
    </div>
  );
}
