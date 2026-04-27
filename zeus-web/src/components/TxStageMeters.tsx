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
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { useRef } from 'react';
import { useTxStore } from '../state/tx-store';

// Overdrive detector — fires when the TX signal is actually clipping at
// digital full scale. Two signatures, both at the 0 dBFS hard limit:
//   1. mic ADC clipping     wdspMicPk >= 0 dBFS  (post-panel-gain, pre-DSP)
//   2. TX output clipping   outPk     >= 0 dBFS  (post-DSP, what the radio sees)
// "Clipping" is a hard fact (sample reached digital full scale ±1.0), not a
// soft "compressor working hard" signal. Compressor activity is normal and
// belongs on the GR meters, not on a warning indicator. Earlier versions
// also OR'd ALC GR > 10 dB and CFC GR > 10 dB; both produced false positives
// (CFC's −400 dBFS bypass sentinel becomes +400 after the WdspDspEngine
// negation and slips past isBypassed; ALC GR sustained at 10 dB during heavy
// compression isn't clipping, it's the limiter doing its job).
//
// Visual: hold the indicator lit for OVERDRIVE_HOLD_MS after the most recent
// clip event so a single transient produces a visible flash and sustained
// clipping keeps the light on.
const OVERDRIVE_CLIP_DBFS = 0;
const OVERDRIVE_HOLD_MS = 250;

type OverdriveState = {
  tripped: boolean;
  mic: boolean;
  out: boolean;
};

function useOverdrive(): OverdriveState {
  const micPk = useTxStore((s) => s.wdspMicPk);
  const outPk = useTxStore((s) => s.outPk);
  const moxOn = useTxStore((s) => s.moxOn);
  const tunOn = useTxStore((s) => s.tunOn);
  const lastMicClipRef = useRef<number>(0);
  const lastOutClipRef = useRef<number>(0);

  const transmitting = moxOn || tunOn;
  const now =
    typeof performance !== 'undefined' ? performance.now() : Date.now();

  const micClip =
    transmitting &&
    isFinite(micPk) &&
    !isBypassed(micPk) &&
    micPk >= OVERDRIVE_CLIP_DBFS;
  const outClip =
    transmitting &&
    isFinite(outPk) &&
    !isBypassed(outPk) &&
    outPk >= OVERDRIVE_CLIP_DBFS;

  if (micClip) lastMicClipRef.current = now;
  if (outClip) lastOutClipRef.current = now;

  const mic =
    transmitting && now - lastMicClipRef.current < OVERDRIVE_HOLD_MS;
  const out =
    transmitting && now - lastOutClipRef.current < OVERDRIVE_HOLD_MS;

  return { tripped: mic || out, mic, out };
}

function overdriveTooltip(s: OverdriveState): string {
  const triggers: string[] = [];
  if (s.mic) triggers.push('mic clipping (≥ 0 dBFS)');
  if (s.out) triggers.push('TX output clipping (≥ 0 dBFS)');
  if (s.tripped) {
    return `Overdrive: ${triggers.join(' + ')}. Reduce mic gain or drive.`;
  }
  return 'Overdrive detector — flashes when mic input or TX output reaches 0 dBFS (digital clip).';
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
  dbfs: number;     // peak (TXA_*_PK)
  dbfsAv: number;   // average (TXA_*_AV) — slow, sustained energy
  hint: string;
};

// Solid colours used for the PK/AV bar fill, picked by zone — matches the
// design tokens (--good is added to tokens.css alongside --tx and --power).
const COLOR_GOOD = '#2e7a2e';        // healthy speech band (≤ -6 dBFS)
const COLOR_WARN = 'var(--power)';   // approaching clip (-6..0 dBFS)
const COLOR_CLIP = 'var(--tx)';      // clipping (≥ 0 dBFS)

// Permanent zone bands painted on the chassis so "good vs bad" reads even
// when the bar is empty. Low alpha keeps the chassis legible; the live fill
// draws on top in a saturated colour. Boundaries match LEVEL_TARGET_POS
// (-6 dBFS) and LEVEL_DANGER_POS (0 dBFS).
const LEVEL_ZONE_BG =
  `linear-gradient(90deg,` +
  ` rgba(46,122,46,0.18) 0%,` +
  ` rgba(46,122,46,0.18) ${LEVEL_TARGET_POS * 100}%,` +
  ` rgba(255,201,58,0.20) ${LEVEL_TARGET_POS * 100}%,` +
  ` rgba(255,201,58,0.20) ${LEVEL_DANGER_POS * 100}%,` +
  ` rgba(230,58,43,0.24) ${LEVEL_DANGER_POS * 100}%,` +
  ` rgba(230,58,43,0.24) 100%)`;

function levelFillColor(dbfs: number): string {
  if (!isFinite(dbfs) || isBypassed(dbfs)) return COLOR_GOOD;
  if (dbfs >= 0) return COLOR_CLIP;
  if (dbfs >= -6) return COLOR_WARN;
  return COLOR_GOOD;
}

// GR fill colour by zone — 0..3 dB warns the operator the leveler isn't
// engaging (signal probably too quiet), 3..10 dB is the healthy SSB band,
// 10+ dB means the input is overdriving the limiter.
function grFillColor(db: number): string {
  if (!isFinite(db) || isBypassed(db)) return COLOR_WARN;
  const v = Math.max(0, db);
  if (v >= ALC_ZONE_HEALTHY_END_DB) return COLOR_CLIP;
  if (v >= ALC_ZONE_QUIET_END_DB) return COLOR_GOOD;
  return COLOR_WARN;
}

// Single solid-colour bar:
//   • Background = permanent green/yellow/red zone bands (chassis paint).
//   • AV fill    = solid colour driven by the AV zone — wide bar, slow
//                  transition. This is the "easy to read" sustained level.
//   • PK tick    = bright vertical line at the instantaneous peak position
//                  so operators can see headroom-to-clipping at a glance.
//
// PK and AV come straight off the wire (TXA_*_PK / TXA_*_AV); we don't
// fabricate either client-side. Stage-bypassed (≤ -200 dBFS sentinel) rows
// just render zone bands with no fill and an em-dash readout.
function ZoneLevelBar({ pkAxis, avAxis, pkValue, avValue, pkBypassed, avBypassed }: {
  pkAxis: number; avAxis: number;
  pkValue: number; avValue: number;
  pkBypassed: boolean; avBypassed: boolean;
}) {
  const fillColor = levelFillColor(avBypassed ? pkValue : avValue);
  const avPct = avBypassed ? 0 : (avAxis / LEVEL_RANGE_DB) * 100;
  const pkPct = pkBypassed ? null : (pkAxis / LEVEL_RANGE_DB) * 100;
  return (
    <>
      {/* Zone bands behind the fill */}
      <div
        aria-hidden="true"
        style={{
          position: 'absolute',
          inset: 0,
          background: LEVEL_ZONE_BG,
          pointerEvents: 'none',
        }}
      />
      {/* AV — solid colour fill */}
      <div
        aria-hidden="true"
        style={{
          position: 'absolute',
          left: 0,
          top: 0,
          bottom: 0,
          width: `${avPct}%`,
          background: fillColor,
          transition: 'width 220ms, background 100ms',
          pointerEvents: 'none',
        }}
      />
      {/* PK — bright tick at the instantaneous peak */}
      {pkPct !== null && (
        <div
          aria-hidden="true"
          style={{
            position: 'absolute',
            left: `calc(${pkPct}% - 1px)`,
            top: 0,
            bottom: 0,
            width: 2,
            background: 'rgba(255,255,255,0.92)',
            transition: 'left 80ms',
            pointerEvents: 'none',
          }}
        />
      )}
    </>
  );
}

function LevelRow({ label, dbfs, dbfsAv, hint }: LevelRowProps) {
  const pkBypassed = isBypassed(dbfs);
  const avBypassed = isBypassed(dbfsAv);
  const bypassed = pkBypassed && avBypassed;
  const pkAxis = dbfsToAxis(dbfs);
  const avAxis = dbfsToAxis(dbfsAv);
  const held = usePeakHold(dbfs);
  const heldAxis = dbfsToAxis(held);
  const heldVisible = isFinite(held) && !isBypassed(held) && heldAxis > pkAxis;
  const pkDisplay = !isFinite(dbfs) || pkBypassed ? '—' : dbfs.toFixed(0);
  const avDisplay = !isFinite(dbfsAv) || avBypassed ? '—' : dbfsAv.toFixed(0);
  const rowTitle = bypassed ? `${hint} (stage bypassed)` : hint;
  return (
    <div
      className="meter"
      title={rowTitle}
      style={bypassed ? { opacity: 0.55 } : undefined}
    >
      <div className="meter-head">
        <span className="label-xs">{label}</span>
        <span className="meter-val mono" style={{ fontSize: 12 }}>
          {pkDisplay}
          <span className="unit" style={{ margin: '0 4px' }}>/</span>
          {avDisplay}
          <span className="unit"> dBFS</span>
        </span>
      </div>
      <div className="meter-bar" style={{ height: 12 }}>
        <ZoneLevelBar
          pkAxis={pkAxis}
          avAxis={avAxis}
          pkValue={dbfs}
          avValue={dbfsAv}
          pkBypassed={pkBypassed}
          avBypassed={avBypassed}
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

function AlcPairRow({ alcPk, alcAv, alcGr }: { alcPk: number; alcAv: number; alcGr: number }) {
  // PK + AV side re-use the same -30..+12 dB axis as the per-stage strip so
  // the prominent summary agrees with the strip below.
  const pkBypassed = isBypassed(alcPk);
  const avBypassed = isBypassed(alcAv);
  const pkAxis = dbfsToAxis(alcPk);
  const avAxis = dbfsToAxis(alcAv);
  const pkHeld = usePeakHold(alcPk);
  const pkHeldAxis = dbfsToAxis(pkHeld);
  const pkHeldVisible =
    isFinite(pkHeld) && !isBypassed(pkHeld) && pkHeldAxis > pkAxis;
  const pkDisplay =
    !isFinite(alcPk) || pkBypassed ? '—' : alcPk.toFixed(0);
  const avDisplay =
    !isFinite(alcAv) || avBypassed ? '—' : alcAv.toFixed(0);

  // GR side clamps negative noise to 0 (see P1.8); bypass sentinel still wins.
  const grBypassed = isBypassed(alcGr);
  const grNormalized = grBypassed ? alcGr : Math.max(0, alcGr);
  const grClamped = grBypassed ? 0 : Math.min(GR_MAX_DB, grNormalized);
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
          PK {pkDisplay}<span className="unit" style={{ margin: '0 4px' }}>/</span>{avDisplay}
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
        {/* PK + AV bar (-30..+12) with green/yellow/red zones */}
        <div className="meter-bar" style={{ height: 12 }}>
          <ZoneLevelBar
            pkAxis={pkAxis}
            avAxis={avAxis}
            pkValue={alcPk}
            avValue={alcAv}
            pkBypassed={pkBypassed}
            avBypassed={avBypassed}
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

        {/* GR bar (0..25) with zone bands + solid-colour fill */}
        <div className="meter-bar" style={{ height: 12 }}>
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
            aria-hidden="true"
            style={{
              position: 'absolute',
              left: 0,
              top: 0,
              bottom: 0,
              width: `${(grClamped / GR_MAX_DB) * 100}%`,
              background: grFillColor(grNormalized),
              transition: 'width 200ms, background 100ms',
              pointerEvents: 'none',
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

// Compact CFC row: PK level bar on the left, GR bar on the right — same
// visual language as AlcPairRow but in a single LevelRow-sized vertical
// slot. CFC is bypassed by default (WDSP SetTXACFCRun is not called), so
// both readings land on the −400 sentinel and the row dims to 55% via
// the `bypassed` style on the outer meter chassis.
function CfcPairRow({ pk, av, gr, hint }: { pk: number; av: number; gr: number; hint: string }) {
  const pkBypassed = isBypassed(pk);
  const avBypassed = isBypassed(av);
  const grBypassed = isBypassed(gr);
  // Row-level "bypassed" = all three streams silent (CFC off in WDSP).
  const bypassed = pkBypassed && avBypassed && grBypassed;

  const pkAxis = dbfsToAxis(pk);
  const avAxis = dbfsToAxis(av);
  const pkHeld = usePeakHold(pk);
  const pkHeldAxis = dbfsToAxis(pkHeld);
  const pkHeldVisible =
    isFinite(pkHeld) && !isBypassed(pkHeld) && pkHeldAxis > pkAxis;
  const pkDisplay = !isFinite(pk) || pkBypassed ? '—' : pk.toFixed(0);
  const avDisplay = !isFinite(av) || avBypassed ? '—' : av.toFixed(0);

  const grNormalized = grBypassed ? gr : Math.max(0, gr);
  const grClamped = grBypassed ? 0 : Math.min(GR_MAX_DB, grNormalized);
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
          PK {pkDisplay}<span className="unit" style={{ margin: '0 4px' }}>/</span>{avDisplay}
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
        {/* PK + AV bar with green/yellow/red zones */}
        <div className="meter-bar" style={{ height: 12 }}>
          <ZoneLevelBar
            pkAxis={pkAxis}
            avAxis={avAxis}
            pkValue={pk}
            avValue={av}
            pkBypassed={pkBypassed}
            avBypassed={avBypassed}
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
        {/* GR bar — solid colour by zone, zone bands behind */}
        <div className="meter-bar" style={{ height: 12 }}>
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
            aria-hidden="true"
            style={{
              position: 'absolute',
              left: 0,
              top: 0,
              bottom: 0,
              width: `${(grClamped / GR_MAX_DB) * 100}%`,
              background: grFillColor(grNormalized),
              transition: 'width 200ms, background 100ms',
              pointerEvents: 'none',
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
  const micAv = useTxStore((s) => s.micAv);
  const eqPk = useTxStore((s) => s.eqPk);
  const eqAv = useTxStore((s) => s.eqAv);
  const lvlrPk = useTxStore((s) => s.lvlrPk);
  const lvlrAv = useTxStore((s) => s.lvlrAv);
  const cfcPk = useTxStore((s) => s.cfcPk);
  const cfcAv = useTxStore((s) => s.cfcAv);
  const cfcGr = useTxStore((s) => s.cfcGr);
  const compPk = useTxStore((s) => s.compPk);
  const compAv = useTxStore((s) => s.compAv);
  const alcPk = useTxStore((s) => s.alcPk);
  const alcAv = useTxStore((s) => s.alcAv);
  const alcGr = useTxStore((s) => s.alcGr);
  const outPk = useTxStore((s) => s.outPk);
  const outAv = useTxStore((s) => s.outAv);
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
      <AlcPairRow alcPk={alcPk} alcAv={alcAv} alcGr={alcGr} />

      <LevelRow
        label="MIC"
        dbfs={wdspMicPk}
        dbfsAv={micAv}
        hint="Post-panel-gain mic level entering WDSP TXA (TXA_MIC_PK / _AV)"
      />
      <LevelRow label="EQ" dbfs={eqPk} dbfsAv={eqAv} hint="Post-EQ peak / average" />
      <LevelRow
        label="LVLR"
        dbfs={lvlrPk}
        dbfsAv={lvlrAv}
        hint="Post-Leveler peak / average — same as EQ while Leveler is disabled"
      />
      <CfcPairRow
        pk={cfcPk}
        av={cfcAv}
        gr={cfcGr}
        hint="CFC (continuous-frequency compressor) peak + average + gain reduction"
      />
      <LevelRow
        label="COMP"
        dbfs={compPk}
        dbfsAv={compAv}
        hint="Post-compressor peak / average — bypassed by default"
      />
      {/* ALC and ALC GR are not duplicated as standalone rows — the
          prominent AlcPairRow at the top of the panel already shows both
          in side-by-side form. */}
      <LevelRow label="OUT" dbfs={outPk} dbfsAv={outAv} hint="Final TX peak / average" />
    </div>
  );
}
