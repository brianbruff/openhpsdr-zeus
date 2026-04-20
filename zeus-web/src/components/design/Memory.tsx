import type { MemoryChannel } from './data';
import { MEMS } from './data';

type MemoryProps = {
  current: number;
  onPick?: (m: MemoryChannel) => void;
};

export function Memory({ current, onPick }: MemoryProps) {
  return (
    <div className="memgrid">
      {MEMS.map((m) => (
        <button
          type="button"
          key={m.n}
          className={`mem ${current === m.n ? 'active' : ''}`}
          onClick={() => onPick?.(m)}
        >
          <div className="mem-top">
            <span className="mono mem-n">M{String(m.n).padStart(2, '0')}</span>
            <span className="label-xs mem-mode">{m.m}</span>
          </div>
          <div className="mono mem-f">{m.f.toFixed(3)}</div>
          <div className="label-xs mem-name">{m.name}</div>
        </button>
      ))}
    </div>
  );
}
