import { useRef } from 'react';
import { useTxStore } from '../state/tx-store';

// Per-stage TX meter panel. Replaces the Memory-channels placeholder in the
// bottom row for now — TX diagnostics are higher-priority while we chase
// the SSB audio-quality issue. Reads peak-dBFS readings published by
// WdspDspEngine.ProcessTxBlock (via TxMetersFrame) and renders them in
// the design's .meter chassis.
//
// Conventions (Thetis MeterManager.cs):
//   - Levels shown on a -30..+12 dB scale (42 dB span) with the danger tick
//     at 0 dBFS (clip point) and a secondary "target peak" tick at -6 dBFS.
//     The asymmetric scale concentrates resolution around the useful range
//     for SSB voice, where healthy peaks sit around -6..-3 dBFS.
//   - ALC gain reduction uses a 0..25 dB scale with the danger tick at
//     10 dB; sustained > 10 dB GR means the input is consistently
//     over-driving the limiter.
//   - While MOX/TUN is off, TxMetersFrame carries −Infinity level / 0 GR;
//     we detect that with isFinite() and render em-dashes.

const LEVEL_MIN_DB = -30;
const LEVEL_MAX_DB = 12;
const LEVEL_RANGE_DB = LEVEL_MAX_DB - LEVEL_MIN_DB; // 42 dB span
const LEVEL_DANGER_POS = (0 - LEVEL_MIN_DB) / LEVEL_RANGE_DB; // 0 dBFS = clip
const LEVEL_TARGET_POS = (-6 - LEVEL_MIN_DB) / LEVEL_RANGE_DB; // -6 dBFS target
const GR_MAX_DB = 25;
const GR_DANGER_POS = 10 / GR_MAX_DB; // >10 dB GR = over-driving the limiter
// WDSP returns −400 dBFS when a stage is bypassed. Anything ≤ −200 is far
// below any real audio level, so we treat it as a bypassed sentinel rather
// than clamping to the axis floor (which would paint a misleading tiny bar
// and a confusing "-400 dBFS" readout).
const BYPASSED_DBFS_THRESHOLD = -200;

function isBypassed(dbfs: number): boolean {
  return dbfs <= BYPASSED_DBFS_THRESHOLD;
}

// Thetis convention (MeterManager.cs: attack 0.8, decay 0.1, ~2 s visible
// history): the held peak decays at a rate that takes ~2 s to traverse the
// full axis. For the 42 dB level axis that's 21 dB/s; the GR axis uses
// GR_MAX_DB/2 via the decayDbPerSec override. The hook tracks the running
// max in a ref so decay stays continuous across renders, using wall-clock
// time for dt rather than frame count. Returns −Infinity while current is
// non-finite or ≤ the bypass sentinel.
const PEAK_DECAY_DB_PER_SEC = LEVEL_RANGE_DB / 2;

function usePeakHold(current: number, decayDbPerSec = PEAK_DECAY_DB_PER_SEC): number {
  const state = useRef<{ db: number; ts: number }>({ db: -Infinity, ts: 0 });
  if (!isFinite(current) || isBypassed(current)) {
    state.current = { db: -Infinity, ts: 0 };
    return -Infinity;
  }
  const now =
    typeof performance !== 'undefined' ? performance.now() : Date.now();
  const prev = state.current;
  const dt = prev.ts === 0 ? 0 : Math.max(0, (now - prev.ts) / 1000);
  const decayed = isFinite(prev.db) ? prev.db - decayDbPerSec * dt : -Infinity;
  const held = Math.max(current, decayed);
  state.current = { db: held, ts: now };
  return held;
}

// Convert a dBFS reading to the 0..LEVEL_RANGE_DB axis (0..42).
// -30 dBFS → 0, 0 dBFS → 30, +12 dBFS → 42.
function dbfsToAxis(dbfs: number): number {
  if (!isFinite(dbfs) || isBypassed(dbfs)) return 0;
  const clamped = Math.max(LEVEL_MIN_DB, Math.min(LEVEL_MAX_DB, dbfs));
  return clamped - LEVEL_MIN_DB;
}

type LevelRowProps = {
  label: string;
  dbfs: number;
  hint: string;
};

function LevelRow({ label, dbfs, hint }: LevelRowProps) {
  const bypassed = isBypassed(dbfs);
  const axis = dbfsToAxis(dbfs);
  const held = usePeakHold(dbfs);
  const heldAxis = dbfsToAxis(held);
  const heldVisible = isFinite(held) && !isBypassed(held) && heldAxis > axis;
  const display = !isFinite(dbfs) || bypassed ? '—' : dbfs.toFixed(0);
  const rowTitle = bypassed ? `${hint} (stage bypassed)` : hint;
  return (
    <div className="meter" title={rowTitle}>
      <div className="meter-head">
        <span className="label-xs">{label}</span>
        <span className="meter-val mono">
          {display}
          <span className="unit"> dBFS</span>
        </span>
      </div>
      <div className="meter-bar">
        <div
          className="meter-fill"
          style={{
            width: `${(axis / LEVEL_RANGE_DB) * 100}%`,
            filter:
              axis / LEVEL_RANGE_DB > LEVEL_DANGER_POS
                ? 'hue-rotate(-20deg) saturate(1.4)'
                : undefined,
          }}
        />
        {heldVisible && (
          // 2 px tick at the held peak — amber (#FFA028) @ 0.4 alpha, no new
          // hue introduced. Decays 30 dB/sec per Thetis convention.
          <div
            className="meter-peak-hold"
            aria-hidden="true"
            style={{
              position: 'absolute',
              left: `calc(${(heldAxis / LEVEL_RANGE_DB) * 100}% - 1px)`,
              top: 0,
              bottom: 0,
              width: 2,
              background: 'rgba(255, 160, 40, 0.4)',
              pointerEvents: 'none',
            }}
          />
        )}
        <div className="meter-ticks">
          {[0.25, 0.5, 0.75].map((t) => (
            <div key={t} className="meter-tick" style={{ left: `${t * 100}%` }} />
          ))}
          {/* Target-peak marker at -6 dBFS (amber, #FFA028 @ 0.55 alpha). */}
          <div
            className="meter-tick"
            style={{
              left: `${LEVEL_TARGET_POS * 100}%`,
              background: 'rgba(255, 160, 40, 0.55)',
            }}
          />
          <div
            className="meter-tick danger"
            style={{ left: `${LEVEL_DANGER_POS * 100}%` }}
          />
        </div>
      </div>
    </div>
  );
}

function GrRow({ db, hint }: { db: number; hint: string }) {
  // GR readings are always ≥ 0 dB; a large negative value is WDSP's bypass
  // sentinel (−400). Show em-dash rather than pinning the bar to 0 dB with
  // a meaningless readout.
  const bypassed = isBypassed(db);
  const clamped = bypassed ? 0 : Math.max(0, Math.min(GR_MAX_DB, db));
  // GR axis is 20 dB wide; scale decay so full-range takes ~2 s.
  const held = usePeakHold(db, GR_MAX_DB / 2);
  const heldClamped = Math.max(0, Math.min(GR_MAX_DB, held));
  const heldVisible =
    isFinite(held) && !isBypassed(held) && heldClamped > clamped;
  const display =
    !isFinite(db) || bypassed ? '—' : db === 0 ? '0' : db.toFixed(1);
  const rowTitle = bypassed ? `${hint} (stage bypassed)` : hint;
  return (
    <div className="meter" title={rowTitle}>
      <div className="meter-head">
        <span className="label-xs">ALC GR</span>
        <span className="meter-val mono">
          {display}
          <span className="unit"> dB</span>
        </span>
      </div>
      <div className="meter-bar">
        <div
          className="meter-fill"
          style={{
            width: `${(clamped / GR_MAX_DB) * 100}%`,
            filter:
              clamped / GR_MAX_DB > GR_DANGER_POS
                ? 'hue-rotate(-20deg) saturate(1.4)'
                : undefined,
          }}
        />
        {heldVisible && (
          <div
            className="meter-peak-hold"
            aria-hidden="true"
            style={{
              position: 'absolute',
              left: `calc(${(heldClamped / GR_MAX_DB) * 100}% - 1px)`,
              top: 0,
              bottom: 0,
              width: 2,
              background: 'rgba(255, 160, 40, 0.4)',
              pointerEvents: 'none',
            }}
          />
        )}
        <div className="meter-ticks">
          {[0.25, 0.5, 0.75].map((t) => (
            <div key={t} className="meter-tick" style={{ left: `${t * 100}%` }} />
          ))}
          <div
            className="meter-tick danger"
            style={{ left: `${GR_DANGER_POS * 100}%` }}
          />
        </div>
      </div>
    </div>
  );
}

export function TxStageMeters() {
  const wdspMicPk = useTxStore((s) => s.wdspMicPk);
  const eqPk = useTxStore((s) => s.eqPk);
  const lvlrPk = useTxStore((s) => s.lvlrPk);
  const alcPk = useTxStore((s) => s.alcPk);
  const alcGr = useTxStore((s) => s.alcGr);
  const outPk = useTxStore((s) => s.outPk);
  const moxOn = useTxStore((s) => s.moxOn);
  const tunOn = useTxStore((s) => s.tunOn);
  const transmitting = moxOn || tunOn;

  return (
    <div
      style={{
        display: 'flex',
        flexDirection: 'column',
        padding: '4px 0',
        opacity: transmitting ? 1 : 0.55,
        transition: 'opacity 120ms',
      }}
      aria-label="TX stage meters"
    >
      <LevelRow
        label="MIC"
        dbfs={wdspMicPk}
        hint="Post-panel-gain mic level entering WDSP TXA (TXA_MIC_PK)"
      />
      <LevelRow label="EQ" dbfs={eqPk} hint="Post-EQ peak" />
      <LevelRow
        label="LVLR"
        dbfs={lvlrPk}
        hint="Post-Leveler peak — same as EQ while Leveler is disabled"
      />
      <LevelRow
        label="ALC"
        dbfs={alcPk}
        hint="Post-ALC peak — the key clipping indicator for SSB distortion"
      />
      <GrRow
        db={alcGr}
        hint="ALC gain reduction; sustained >10 dB means the input is over-driving the limiter"
      />
      <LevelRow label="OUT" dbfs={outPk} hint="Final TX peak" />
    </div>
  );
}
