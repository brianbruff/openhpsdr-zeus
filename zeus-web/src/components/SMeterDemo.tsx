import { useEffect, useRef, useState } from 'react';
import { SMeter } from './SMeter';

// Presentational harness until real meter telemetry is wired up. Generates a
// plausible fluctuating RX level and lets the user toggle a TX state to
// preview the power-bar variant.
export function SMeterDemo() {
  const [isTx, setIsTx] = useState(false);
  const [dbm, setDbm] = useState(-100);
  const [watts, setWatts] = useState(0);
  const startRef = useRef<number>(performance.now());
  const rafRef = useRef<number | null>(null);

  useEffect(() => {
    const tick = () => {
      const t = (performance.now() - startRef.current) / 1000;
      if (isTx) {
        // Slight envelope around 25 W avg with voice-peak excursions.
        const env = 25 + 15 * Math.abs(Math.sin(t * 3.1)) + 8 * Math.sin(t * 11);
        setWatts(Math.max(0, env));
      } else {
        // Slow roaming S-level with bursts.
        const slow = -95 + 25 * Math.sin(t * 0.35);
        const fast = 6 * Math.sin(t * 2.4) + 3 * Math.sin(t * 7.2);
        setDbm(slow + fast);
      }
      rafRef.current = requestAnimationFrame(tick);
    };
    rafRef.current = requestAnimationFrame(tick);
    return () => {
      if (rafRef.current != null) cancelAnimationFrame(rafRef.current);
    };
  }, [isTx]);

  return (
    <section className="flex items-center gap-2 border-b border-neutral-800 bg-neutral-950 px-3 py-1 sm:px-4">
      <button
        type="button"
        onClick={() => setIsTx((v) => !v)}
        className={
          'rounded px-2 py-1 font-mono text-xs tracking-wide ' +
          (isTx
            ? 'bg-red-600/80 text-neutral-50 ring-1 ring-red-400/60'
            : 'bg-neutral-800 text-neutral-300 hover:bg-neutral-700')
        }
        aria-pressed={isTx}
        title="Toggle TX (demo)"
      >
        {isTx ? 'TX' : 'RX'}
      </button>
      <div className="flex-1">
        {isTx ? (
          <SMeter mode="tx" watts={watts} maxWatts={100} />
        ) : (
          <SMeter mode="rx" dbm={dbm} />
        )}
      </div>
    </section>
  );
}
