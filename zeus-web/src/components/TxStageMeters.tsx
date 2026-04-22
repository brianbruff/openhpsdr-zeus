import { useTxStore } from '../state/tx-store';

// Per-stage TX meter panel. Replaces the Memory-channels placeholder in the
// bottom row for now — TX diagnostics are higher-priority while we chase
// the SSB audio-quality issue. Reads peak-dBFS readings published by
// WdspDspEngine.ProcessTxBlock (via TxMetersFrame) and renders them in
// the design's .meter chassis.
//
// Conventions:
//   - Levels shown on a -60..0 dBFS scale with the danger tick at -3 dBFS
//     (3 dB headroom, Thetis-equivalent). We negate the stored dBFS for
//     the Meter primitive's 0..max positive axis — i.e. a −20 dBFS peak
//     fills 40/60 of the bar.
//   - ALC gain reduction uses a 0..20 dB scale with the danger tick at
//     ~12 dB; sustained >12 dB on SSB means the input is consistently
//     over-driving the limiter.
//   - While MOX/TUN is off, TxMetersFrame carries −Infinity level / 0 GR;
//     we detect that with isFinite() and render em-dashes.

const LEVEL_RANGE_DB = 60; // -60..0 dBFS displayed
const LEVEL_DANGER_POS = (LEVEL_RANGE_DB - 3) / LEVEL_RANGE_DB; // -3 dBFS
const GR_MAX_DB = 20;
const GR_DANGER_POS = 12 / GR_MAX_DB;
// WDSP returns −400 dBFS when a stage is bypassed. Anything ≤ −200 is far
// below any real audio level, so we treat it as a bypassed sentinel rather
// than clamping to the axis floor (which would paint a misleading tiny bar
// and a confusing "-400 dBFS" readout).
const BYPASSED_DBFS_THRESHOLD = -200;

function isBypassed(dbfs: number): boolean {
  return dbfs <= BYPASSED_DBFS_THRESHOLD;
}

// Convert a dBFS reading (≤ 0) to the 0..max axis the Meter primitive wants.
// -60 dBFS → 0, 0 dBFS → 60.
function dbfsToAxis(dbfs: number): number {
  if (!isFinite(dbfs) || isBypassed(dbfs)) return 0;
  const clamped = Math.max(-LEVEL_RANGE_DB, Math.min(0, dbfs));
  return LEVEL_RANGE_DB + clamped;
}

type LevelRowProps = {
  label: string;
  dbfs: number;
  hint: string;
};

function LevelRow({ label, dbfs, hint }: LevelRowProps) {
  const bypassed = isBypassed(dbfs);
  const axis = dbfsToAxis(dbfs);
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
        <div className="meter-ticks">
          {[0.25, 0.5, 0.75].map((t) => (
            <div key={t} className="meter-tick" style={{ left: `${t * 100}%` }} />
          ))}
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
        hint="ALC gain reduction; sustained >12 dB means the input is over-driving the limiter"
      />
      <LevelRow label="OUT" dbfs={outPk} hint="Final TX peak" />
    </div>
  );
}
