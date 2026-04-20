import type { PointerEvent as ReactPointerEvent } from 'react';

type SliderProps = {
  label?: string;
  value: number;
  onChange: (v: number) => void;
  disabled?: boolean;
  min?: number;
  max?: number;
  formatValue?: (v: number) => string;
};

export function Slider({
  label,
  value,
  onChange,
  disabled = false,
  min = 0,
  max = 100,
  formatValue,
}: SliderProps) {
  const pct = ((value - min) / (max - min)) * 100;

  const onPointerDown = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (disabled) return;
    const rect = e.currentTarget.getBoundingClientRect();
    const setFromEvent = (clientX: number) => {
      const t = (clientX - rect.left) / rect.width;
      onChange(Math.max(min, Math.min(max, min + t * (max - min))));
    };
    setFromEvent(e.clientX);
    const move = (ev: PointerEvent) => setFromEvent(ev.clientX);
    const up = () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up);
  };

  return (
    <div className={`slider ${disabled ? 'disabled' : ''}`}>
      {label != null && (
        <div className="slider-head">
          <span className="label-xs">{label}</span>
          <span className="mono slider-val">{formatValue ? formatValue(value) : Math.round(value)}</span>
        </div>
      )}
      <div className="slider-track" onPointerDown={onPointerDown}>
        <div className="slider-fill" style={{ width: `${pct}%` }} />
        <div className="slider-thumb" style={{ left: `${pct}%` }} />
      </div>
    </div>
  );
}
