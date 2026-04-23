// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { useRef } from 'react';
import { useTxStore } from '../state/tx-store';

// Overdrive detector — fires on ANY of three independent HL2 community
// signatures (W1AEX, softerhardware wiki), each sustained >= 200 ms:
//   1. mic clipping         micPk  >= -1 dBFS  (bad regardless of downstream)
//   2. ALC limiting hard    alcGr  > 10 dB
//   3. CFC limiting hard    cfcGr  > 10 dB     (silent until CFC is enabled)
// Earlier AND-of-mic+ALC spec never fired in live test because the Leveler
// (enabled by default in P1.1) absorbs mic dynamics before ALC — so mic
// could peg at 0 dBFS while alcGr stayed near 0. OR of independent
// signatures captures each failure mode on its own. Each signature gets
// its own sustain timer so a short transient on one doesn't reset the others.
const OVERDRIVE_MIC_PK_DBFS = -1;
const OVERDRIVE_ALC_GR_DB = 10;
const OVERDRIVE_CFC_GR_DB = 10;
const OVERDRIVE_SUSTAIN_MS = 200;

type OverdriveState = {
  tripped: boolean;
  mic: boolean;
  alc: boolean;
  cfc: boolean;
};

function useOverdrive(): OverdriveState {
  const micPk = useTxStore((s) => s.wdspMicPk);
  const alcGr = useTxStore((s) => s.alcGr);
  const cfcGr = useTxStore((s) => s.cfcGr);
  const moxOn = useTxStore((s) => s.moxOn);
  const tunOn = useTxStore((s) => s.tunOn);
  // Per-signature last-NOT-met timestamps; sustain = now - lastNotMet.
  const now0 =
    typeof performance !== 'undefined' ? performance.now() : 0;
  const micNotMetRef = useRef<number>(now0);
  const alcNotMetRef = useRef<number>(now0);
  const cfcNotMetRef = useRef<number>(now0);

  const transmitting = moxOn || tunOn;
  const now =
    typeof performance !== 'undefined' ? performance.now() : Date.now();

  const micRaw =
    transmitting &&
    isFinite(micPk) &&
    !isBypassed(micPk) &&
    micPk >= OVERDRIVE_MIC_PK_DBFS;
  const alcRaw =
    transmitting &&
    isFinite(alcGr) &&
    !isBypassed(alcGr) &&
    alcGr > OVERDRIVE_ALC_GR_DB;
  // CFC stays bypassed by default; isBypassed check keeps the −400 sentinel
  // from ever tripping this signature until the operator enables CFC.
  const cfcRaw =
    transmitting &&
    isFinite(cfcGr) &&
    !isBypassed(cfcGr) &&
    cfcGr > OVERDRIVE_CFC_GR_DB;

  if (!micRaw) micNotMetRef.current = now;
  if (!alcRaw) alcNotMetRef.current = now;
  if (!cfcRaw) cfcNotMetRef.current = now;

  const mic = micRaw && now - micNotMetRef.current >= OVERDRIVE_SUSTAIN_MS;
  const alc = alcRaw && now - alcNotMetRef.current >= OVERDRIVE_SUSTAIN_MS;
  const cfc = cfcRaw && now - cfcNotMetRef.current >= OVERDRIVE_SUSTAIN_MS;

  return { tripped: mic || alc || cfc, mic, alc, cfc };
}

function overdriveTooltip(s: OverdriveState): string {
  const triggers: string[] = [];
  if (s.mic) triggers.push('mic clipping (≥ -1 dBFS)');
  if (s.alc) triggers.push('ALC limiting hard (> 10 dB GR)');
  if (s.cfc) triggers.push('CFC limiting hard (> 10 dB GR)');
  if (s.tripped) {
    return (
      `Overdrive: ${triggers.join(' + ')}. ` +
      'Reduce mic gain or drive.'
    );
  }
  return (
    'Overdrive detector — fires on any of: mic peak ≥ -1 dBFS, ALC GR > 10 dB, ' +
    'or CFC GR > 10 dB, sustained 200 ms.'
  );
}

export function OverdriveIndicator() {
  const state = useOverdrive();
  const tripped = state.tripped;
  return (
    <span
      aria-live="polite"
      aria-label={tripped ? 'Overdrive detected' : 'Overdrive clear'}
      title={overdriveTooltip(state)}
      style={{
        display: 'inline-flex',
        alignItems: 'center',
        gap: 4,
        padding: '2px 6px',
        borderRadius: 3,
        fontSize: 9,
        fontWeight: 700,
        letterSpacing: '0.08em',
        textTransform: 'uppercase',
        background: tripped ? 'var(--tx)' : 'transparent',
        color: tripped ? '#fff' : 'var(--fg-3)',
        border: `1px solid ${tripped ? 'var(--tx)' : 'var(--panel-border)'}`,
        opacity: tripped ? 1 : 0.35,
        transition: 'background 120ms, opacity 120ms, color 120ms',
        boxShadow: tripped ? '0 0 6px var(--tx-soft)' : undefined,
      }}
    >
      <span
        aria-hidden="true"
        style={{
          width: 6,
          height: 6,
          borderRadius: '50%',
          background: tripped ? '#fff' : 'var(--fg-3)',
        }}
      />
      Overdrive
    </span>
  );
}

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
    <div
      className="meter"
      title={rowTitle}
      style={bypassed ? { opacity: 0.55 } : undefined}
    >
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

// W1AEX / softerhardware-wiki community guidance: operators must read ALC
// peak and ALC gain-reduction side-by-side to know how much the limiter is
// acting. Zones on the GR bar follow the task spec:
//   0..3  dB — "quiet"    (Leveler barely engaging)
//   3..10 dB — "healthy"  (SSB compression sweet spot)
//   10+   dB — "overdrive" (input is consistently over-driving the limiter)
// Zone colors track the existing .meter-fill green→amber→red gradient so
// the surrounding Zeus amber chrome stays intact; zones are rendered as
// low-alpha background stripes so the bar itself still reads as amber.
const ALC_ZONE_QUIET_END_DB = 3;
const ALC_ZONE_HEALTHY_END_DB = 10;

const ALC_TOOLTIP =
  'ALC peak (left) + gain reduction (right). You must read both to know ' +
  'how much ALC is acting — the ALC meter tops out at 0 dB and the Comp ' +
  'meter bottoms at 0 dB (W1AEX). Healthy SSB compression sits in the ' +
  '3–10 dB GR band; sustained >10 dB means the input is over-driving.';

// Faint background stripes behind the GR fill that cue the zones without
// overwhelming the amber palette. Alpha is kept low (0.10–0.14) so the
// primary readout is still the filled bar.
function grZoneBackground(): string {
  const q = (ALC_ZONE_QUIET_END_DB / GR_MAX_DB) * 100;
  const h = (ALC_ZONE_HEALTHY_END_DB / GR_MAX_DB) * 100;
  return (
    `linear-gradient(90deg,` +
    ` rgba(242, 133, 36, 0.10) 0%,` + //   amber (quiet)
    ` rgba(242, 133, 36, 0.10) ${q}%,` +
    ` rgba(0, 200, 83, 0.14) ${q}%,` + //  green (healthy)
    ` rgba(0, 200, 83, 0.14) ${h}%,` +
    ` rgba(230, 58, 43, 0.14) ${h}%,` + // red (overdrive)
    ` rgba(230, 58, 43, 0.14) 100%)`
  );
}

function AlcPairRow({ alcPk, alcGr }: { alcPk: number; alcGr: number }) {
  // PK side re-uses the same -30..+12 dB axis as the per-stage strip so the
  // prominent summary agrees with the strip below.
  const pkBypassed = isBypassed(alcPk);
  const pkAxis = dbfsToAxis(alcPk);
  const pkHeld = usePeakHold(alcPk);
  const pkHeldAxis = dbfsToAxis(pkHeld);
  const pkHeldVisible =
    isFinite(pkHeld) && !isBypassed(pkHeld) && pkHeldAxis > pkAxis;
  const pkDisplay =
    !isFinite(alcPk) || pkBypassed ? '—' : alcPk.toFixed(0);

  // GR side clamps negative noise to 0 (see P1.8); bypass sentinel still wins.
  const grBypassed = isBypassed(alcGr);
  const grNormalized = grBypassed ? alcGr : Math.max(0, alcGr);
  const grClamped = grBypassed ? 0 : Math.min(GR_MAX_DB, grNormalized);
  const grOverdrive = grClamped >= ALC_ZONE_HEALTHY_END_DB;
  const grHeld = usePeakHold(grNormalized, GR_MAX_DB / 2);
  const grHeldClamped = Math.max(0, Math.min(GR_MAX_DB, grHeld));
  const grHeldVisible =
    isFinite(grHeld) && !isBypassed(grHeld) && grHeldClamped > grClamped;
  const grDisplay =
    !isFinite(grNormalized) || grBypassed
      ? '—'
      : grNormalized === 0
        ? '0'
        : grNormalized.toFixed(1);

  return (
    <div
      className="meter"
      title={ALC_TOOLTIP}
      aria-label="ALC peak and gain reduction pair"
      style={{
        borderTop: '1px solid var(--panel-border)',
        borderBottom: '1px solid var(--panel-border)',
        background: 'rgba(255, 160, 40, 0.04)',
      }}
    >
      <div className="meter-head">
        <span className="label-xs" style={{ fontWeight: 700 }}>
          ALC
        </span>
        <span className="meter-val mono" style={{ fontSize: 12 }}>
          PK {pkDisplay}
          <span className="unit"> dBFS</span>
          <span style={{ color: 'var(--fg-3)', margin: '0 6px' }}>·</span>
          GR {grDisplay}
          <span className="unit"> dB</span>
        </span>
      </div>
      <div
        style={{
          display: 'grid',
          gridTemplateColumns: '1fr 1fr',
          gap: 8,
          alignItems: 'stretch',
        }}
      >
        {/* PK bar (-30..+12) */}
        <div className="meter-bar">
          <div
            className="meter-fill"
            style={{
              width: `${(pkAxis / LEVEL_RANGE_DB) * 100}%`,
              filter:
                pkAxis / LEVEL_RANGE_DB > LEVEL_DANGER_POS
                  ? 'hue-rotate(-20deg) saturate(1.4)'
                  : undefined,
            }}
          />
          {pkHeldVisible && (
            <div
              aria-hidden="true"
              style={{
                position: 'absolute',
                left: `calc(${(pkHeldAxis / LEVEL_RANGE_DB) * 100}% - 1px)`,
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
              <div
                key={t}
                className="meter-tick"
                style={{ left: `${t * 100}%` }}
              />
            ))}
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

        {/* GR bar (0..25) with zone bands */}
        <div className="meter-bar">
          {/* Zone background stripes sit behind the fill. */}
          <div
            aria-hidden="true"
            style={{
              position: 'absolute',
              inset: 0,
              background: grZoneBackground(),
              pointerEvents: 'none',
            }}
          />
          <div
            className="meter-fill"
            style={{
              width: `${(grClamped / GR_MAX_DB) * 100}%`,
              filter: grOverdrive
                ? 'hue-rotate(-20deg) saturate(1.4)'
                : undefined,
            }}
          />
          {grHeldVisible && (
            <div
              aria-hidden="true"
              style={{
                position: 'absolute',
                left: `calc(${(grHeldClamped / GR_MAX_DB) * 100}% - 1px)`,
                top: 0,
                bottom: 0,
                width: 2,
                background: 'rgba(255, 160, 40, 0.4)',
                pointerEvents: 'none',
              }}
            />
          )}
          <div className="meter-ticks">
            {/* Zone dividers at the boundaries and the overdrive danger tick. */}
            <div
              className="meter-tick"
              style={{
                left: `${(ALC_ZONE_QUIET_END_DB / GR_MAX_DB) * 100}%`,
              }}
            />
            <div
              className="meter-tick danger"
              style={{
                left: `${(ALC_ZONE_HEALTHY_END_DB / GR_MAX_DB) * 100}%`,
              }}
            />
          </div>
        </div>
      </div>
    </div>
  );
}

function GrRow({ db, hint }: { db: number; hint: string }) {
  // GR is "dB of gain reduction" by convention: 0 = no reduction, +N = N dB
  // cut. WDSP's ALC_GAIN meter drifts slightly above unity when ALC isn't
  // limiting, which lands as small negative values after the backend's
  // negation — meaningless for the operator. Clamp the raw reading to ≥ 0
  // *before* anything else so text, bar, and peak-hold all agree.
  // Bypass (−400 dBFS sentinel) wins over the clamp so bypassed stages
  // still render as em-dash rather than "0 dB".
  const bypassed = isBypassed(db);
  const normalized = bypassed ? db : Math.max(0, db);
  const clamped = bypassed ? 0 : Math.min(GR_MAX_DB, normalized);
  // GR axis is GR_MAX_DB wide; scale decay so full-range takes ~2 s.
  const held = usePeakHold(normalized, GR_MAX_DB / 2);
  const heldClamped = Math.max(0, Math.min(GR_MAX_DB, held));
  const heldVisible =
    isFinite(held) && !isBypassed(held) && heldClamped > clamped;
  const display =
    !isFinite(normalized) || bypassed
      ? '—'
      : normalized === 0
        ? '0'
        : normalized.toFixed(1);
  const rowTitle = bypassed ? `${hint} (stage bypassed)` : hint;
  return (
    <div
      className="meter"
      title={rowTitle}
      style={bypassed ? { opacity: 0.55 } : undefined}
    >
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

// Compact CFC row: PK level bar on the left, GR bar on the right — same
// visual language as AlcPairRow but in a single LevelRow-sized vertical
// slot. CFC is bypassed by default (WDSP SetTXACFCRun is not called), so
// both readings land on the −400 sentinel and the row dims to 55% via
// the `bypassed` style on the outer meter chassis.
function CfcPairRow({ pk, gr, hint }: { pk: number; gr: number; hint: string }) {
  const pkBypassed = isBypassed(pk);
  const grBypassed = isBypassed(gr);
  // Row-level "bypassed" = both streams silent (CFC off in WDSP).
  const bypassed = pkBypassed && grBypassed;

  const pkAxis = dbfsToAxis(pk);
  const pkHeld = usePeakHold(pk);
  const pkHeldAxis = dbfsToAxis(pkHeld);
  const pkHeldVisible =
    isFinite(pkHeld) && !isBypassed(pkHeld) && pkHeldAxis > pkAxis;
  const pkDisplay = !isFinite(pk) || pkBypassed ? '—' : pk.toFixed(0);

  const grNormalized = grBypassed ? gr : Math.max(0, gr);
  const grClamped = grBypassed ? 0 : Math.min(GR_MAX_DB, grNormalized);
  const grOverdrive = grClamped >= ALC_ZONE_HEALTHY_END_DB;
  const grHeld = usePeakHold(grNormalized, GR_MAX_DB / 2);
  const grHeldClamped = Math.max(0, Math.min(GR_MAX_DB, grHeld));
  const grHeldVisible =
    isFinite(grHeld) && !isBypassed(grHeld) && grHeldClamped > grClamped;
  const grDisplay =
    !isFinite(grNormalized) || grBypassed
      ? '—'
      : grNormalized === 0
        ? '0'
        : grNormalized.toFixed(1);

  const rowTitle = bypassed ? `${hint} (stage bypassed)` : hint;
  return (
    <div
      className="meter"
      title={rowTitle}
      style={bypassed ? { opacity: 0.55 } : undefined}
    >
      <div className="meter-head">
        <span className="label-xs">CFC</span>
        <span className="meter-val mono" style={{ fontSize: 12 }}>
          PK {pkDisplay}
          <span className="unit"> dBFS</span>
          <span style={{ color: 'var(--fg-3)', margin: '0 6px' }}>·</span>
          GR {grDisplay}
          <span className="unit"> dB</span>
        </span>
      </div>
      <div
        style={{
          display: 'grid',
          gridTemplateColumns: '1fr 1fr',
          gap: 6,
          alignItems: 'stretch',
        }}
      >
        {/* PK bar */}
        <div className="meter-bar">
          <div
            className="meter-fill"
            style={{
              width: `${(pkAxis / LEVEL_RANGE_DB) * 100}%`,
              filter:
                pkAxis / LEVEL_RANGE_DB > LEVEL_DANGER_POS
                  ? 'hue-rotate(-20deg) saturate(1.4)'
                  : undefined,
            }}
          />
          {pkHeldVisible && (
            <div
              aria-hidden="true"
              style={{
                position: 'absolute',
                left: `calc(${(pkHeldAxis / LEVEL_RANGE_DB) * 100}% - 1px)`,
                top: 0,
                bottom: 0,
                width: 2,
                background: 'rgba(255, 160, 40, 0.4)',
                pointerEvents: 'none',
              }}
            />
          )}
          <div className="meter-ticks">
            <div
              className="meter-tick danger"
              style={{ left: `${LEVEL_DANGER_POS * 100}%` }}
            />
          </div>
        </div>
        {/* GR bar */}
        <div className="meter-bar">
          <div
            className="meter-fill"
            style={{
              width: `${(grClamped / GR_MAX_DB) * 100}%`,
              filter: grOverdrive
                ? 'hue-rotate(-20deg) saturate(1.4)'
                : undefined,
            }}
          />
          {grHeldVisible && (
            <div
              aria-hidden="true"
              style={{
                position: 'absolute',
                left: `calc(${(grHeldClamped / GR_MAX_DB) * 100}% - 1px)`,
                top: 0,
                bottom: 0,
                width: 2,
                background: 'rgba(255, 160, 40, 0.4)',
                pointerEvents: 'none',
              }}
            />
          )}
          <div className="meter-ticks">
            <div
              className="meter-tick danger"
              style={{
                left: `${(ALC_ZONE_HEALTHY_END_DB / GR_MAX_DB) * 100}%`,
              }}
            />
          </div>
        </div>
      </div>
    </div>
  );
}

export function TxStageMeters() {
  const wdspMicPk = useTxStore((s) => s.wdspMicPk);
  const eqPk = useTxStore((s) => s.eqPk);
  const lvlrPk = useTxStore((s) => s.lvlrPk);
  const cfcPk = useTxStore((s) => s.cfcPk);
  const cfcGr = useTxStore((s) => s.cfcGr);
  const compPk = useTxStore((s) => s.compPk);
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
        // The bottom-row slot is fixed at 200 px (see layout.css .workspace
        // grid-template-rows) and .panel-body hides overflow, so without an
        // inner scroller rows 4-6 (ALC / ALC GR / OUT) are clipped off the
        // bottom of the Dockable. Use a thin scrollbar per the .side-stack
        // convention rather than changing row density (deferred to Phase 2).
        height: '100%',
        overflowY: 'auto',
        scrollbarWidth: 'thin',
      }}
      aria-label="TX stage meters"
    >
      {/*
        ALC prominent summary — peak + gain-reduction side-by-side. The
        community-critical operator diagnostic (W1AEX, softerhardware wiki).
        Rendered above the per-stage strip so it's always in view even
        before the user scrolls.
      */}
      <AlcPairRow alcPk={alcPk} alcGr={alcGr} />

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
      <CfcPairRow
        pk={cfcPk}
        gr={cfcGr}
        hint="CFC (continuous-frequency compressor) peak + gain reduction"
      />
      <LevelRow
        label="COMP"
        dbfs={compPk}
        hint="Post-compressor peak — bypassed by default"
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
