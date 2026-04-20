import {
  Fragment,
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import { fetchState, setVfo } from '../api/client';
import { useConnectionStore } from '../state/connection-store';

const MAX_HZ = 60_000_000;
const STATE_POLL_MS = 2000;

type DigitPlace = {
  decade: number;
  separatorAfter?: '.' | null;
};

const DIGIT_PLACES: readonly DigitPlace[] = [
  { decade: 10_000_000 },
  { decade: 1_000_000, separatorAfter: '.' },
  { decade: 100_000 },
  { decade: 10_000 },
  { decade: 1_000, separatorAfter: '.' },
  { decade: 100 },
  { decade: 10 },
  { decade: 1 },
];

function clampHz(hz: number): number {
  if (!Number.isFinite(hz)) return 0;
  return Math.min(MAX_HZ, Math.max(0, Math.trunc(hz)));
}

function digitAt(hz: number, decade: number): number {
  return Math.floor((hz / decade) % 10);
}

// User types kHz. Accept plain "14200", decimal "14200.5", leading/trailing
// whitespace, comma as decimal for EU keyboards. Reject anything else.
function parseKhzInput(raw: string): number | null {
  const cleaned = raw.trim().replace(',', '.');
  if (!cleaned) return null;
  if (!/^\d+(\.\d+)?$/.test(cleaned)) return null;
  const khz = Number(cleaned);
  if (!Number.isFinite(khz)) return null;
  return clampHz(Math.round(khz * 1000));
}

function formatKhz(hz: number): string {
  return (hz / 1000).toFixed(3);
}

export function VfoDisplay() {
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const applyState = useConnectionStore((s) => s.applyState);

  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState('');
  const inputRef = useRef<HTMLInputElement | null>(null);

  useEffect(() => {
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | null = null;
    const tick = async () => {
      if (!cancelled && !editing) {
        try {
          const next = await fetchState();
          if (!cancelled && !editing) applyState(next);
        } catch {
          /* swallow — retry next tick */
        }
      }
      if (!cancelled) timer = setTimeout(tick, STATE_POLL_MS);
    };
    tick();
    return () => {
      cancelled = true;
      if (timer != null) clearTimeout(timer);
    };
  }, [applyState, editing]);

  const beginEdit = useCallback(() => {
    setDraft(formatKhz(vfoHz));
    setEditing(true);
  }, [vfoHz]);

  const cancelEdit = useCallback(() => {
    setEditing(false);
    setDraft('');
  }, []);

  const commitEdit = useCallback(() => {
    const next = parseKhzInput(draft);
    setEditing(false);
    setDraft('');
    if (next == null || next === vfoHz) return;
    useConnectionStore.setState({ vfoHz: next });
    setVfo(next)
      .then(applyState)
      .catch(() => {
        /* next poll will reconcile */
      });
  }, [draft, vfoHz, applyState]);

  useLayoutEffect(() => {
    if (editing && inputRef.current) {
      inputRef.current.focus();
      inputRef.current.select();
    }
  }, [editing]);

  const onKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLInputElement>) => {
      if (e.key === 'Enter') {
        e.preventDefault();
        commitEdit();
      } else if (e.key === 'Escape') {
        e.preventDefault();
        cancelEdit();
      }
    },
    [commitEdit, cancelEdit],
  );

  const digits = useMemo(() => DIGIT_PLACES, []);

  return (
    <div className="freq-display">
      {editing ? (
        <div className="freq-digits mono" style={{ gap: 6 }}>
          <input
            ref={inputRef}
            type="text"
            inputMode="decimal"
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={onKeyDown}
            onBlur={cancelEdit}
            aria-label="Frequency in kHz"
            style={{
              width: 220,
              background: 'transparent',
              border: 'none',
              borderBottom: '1px solid var(--accent)',
              outline: 'none',
              color: 'var(--fg-0)',
              fontFamily: 'inherit',
              fontSize: 'inherit',
              fontWeight: 700,
            }}
            placeholder="kHz"
          />
          <span className="label-xs" style={{ alignSelf: 'center' }}>
            kHz
          </span>
        </div>
      ) : (
        <button
          type="button"
          onClick={beginEdit}
          aria-label="Edit frequency"
          title="Click to enter frequency in kHz"
          className="freq-digits mono"
          style={{ background: 'none', border: 'none', cursor: 'text', width: '100%' }}
        >
          {digits.map((place) => {
            const d = digitAt(vfoHz, place.decade);
            const isLeading = vfoHz < place.decade;
            return (
              <Fragment key={place.decade}>
                <span className={`digit ${isLeading ? 'leading' : ''}`}>{d}</span>
                {place.separatorAfter && (
                  <span aria-hidden className="sep">
                    {place.separatorAfter}
                  </span>
                )}
              </Fragment>
            );
          })}
        </button>
      )}
      <div className="freq-bot" style={{ justifyContent: 'flex-end', gap: 6, marginTop: 4 }}>
        <span className="label-xs">MHz · click to enter kHz</span>
      </div>
    </div>
  );
}
