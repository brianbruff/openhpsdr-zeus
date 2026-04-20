import { useEffect, useRef, useState } from 'react';

type SelectProps<T extends string> = {
  value: T;
  options: readonly T[];
  onChange: (v: T) => void;
};

export function Select<T extends string>({ value, options, onChange }: SelectProps<T>) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const onDoc = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('click', onDoc);
    return () => document.removeEventListener('click', onDoc);
  }, []);

  return (
    <div className="select" ref={ref}>
      <button type="button" className="btn select-trigger" onClick={() => setOpen((o) => !o)}>
        <span>{value}</span>
        <span className="select-caret">▾</span>
      </button>
      {open && (
        <div className="select-menu">
          {options.map((o) => (
            <button
              type="button"
              key={o}
              className={`select-opt ${o === value ? 'active' : ''}`}
              onClick={() => {
                onChange(o);
                setOpen(false);
              }}
            >
              {o}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
