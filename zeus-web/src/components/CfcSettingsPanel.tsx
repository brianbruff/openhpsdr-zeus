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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

import { useCallback } from 'react';

import type { CfcBandDto, CfcConfigDto } from '../api/client';
import { setCfcConfig } from '../api/client';
import { useTxStore } from '../state/tx-store';

/**
 * 10-band Continuous Frequency Compressor — issue #123. UI mirrors
 * pihpsdr's classic CFC menu (cfc_menu.c): Master section for the
 * frequency-independent toggles + scalar gains, then a per-band table.
 *
 * Visual aesthetics borrowed wholesale from PsSettingsPanel — same
 * Section/Row/NumberInput primitives — and the per-band table layout
 * mirrors PaSettingsPanel rows 191-260. No new tokens; no new
 * components; no amber accents (those are reserved for the panadapter
 * trace per CLAUDE.md).
 *
 * Optimistic-update pattern: each control writes the local store first,
 * then POSTs to the backend. POST failure rolls the entire prior config
 * back so the UI never lies about server state. CFC defaults to OFF —
 * see feedback_user_audio_philosophy.md.
 */

// pihpsdr cfc_menu.c bounds. Mirror them so the UI doesn't accept values
// the engine will silently clamp. WDSP also clamps internally at
// SetTXACFCOMPprofile time so these are just the operator-visible cap.
const FREQ_MIN = 10;
const FREQ_MAX = 9990;
const COMP_MIN = 0;
const COMP_MAX = 20;
const POST_MIN = -20;
const POST_MAX = 20;
const PRECOMP_MIN = -50;
const PRECOMP_MAX = 16;
const PREPEQ_MIN = -50;
const PREPEQ_MAX = 16;

function clamp(v: number, lo: number, hi: number): number {
  return Math.max(lo, Math.min(hi, v));
}

export function CfcSettingsPanel() {
  const cfc = useTxStore((s) => s.cfcConfig);
  const setLocal = useTxStore((s) => s.setCfcConfig);

  // Push the operator's edit through the optimistic-update gate. Local set
  // first so the slider/textbox stays responsive; POST kicks in parallel
  // and rolls back on failure so the radio + UI never drift.
  const push = useCallback(
    (next: CfcConfigDto) => {
      const prev = cfc;
      setLocal(next);
      setCfcConfig(next).catch(() => setLocal(prev));
    },
    [cfc, setLocal],
  );

  const setMaster = useCallback(
    (overrides: Partial<Omit<CfcConfigDto, 'bands'>>) => {
      push({ ...cfc, ...overrides });
    },
    [cfc, push],
  );

  const setBand = useCallback(
    (idx: number, overrides: Partial<CfcBandDto>) => {
      const bands = cfc.bands.map((b, i) => (i === idx ? { ...b, ...overrides } : b));
      push({ ...cfc, bands });
    },
    [cfc, push],
  );

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
      <div
        style={{
          padding: 10,
          border: '1px solid var(--panel-border)',
          borderRadius: 6,
          background: 'var(--bg-1)',
          color: 'var(--fg-2)',
          fontSize: 11,
        }}
      >
        Continuous Frequency Compressor — multi-band frequency-domain
        compressor mirroring pihpsdr's classic 10-band design. Defaults to
        OFF; enabling with neutral settings (compression 0, post-gain 0)
        is audibly transparent. Operators with an external analog rack
        will typically leave this disabled.
      </div>

      <Section title="Master">
        <Row label="Enabled">
          <input
            type="checkbox"
            checked={cfc.enabled}
            onChange={(e) => setMaster({ enabled: e.target.checked })}
          />
        </Row>
        <Row label="Post-EQ">
          <input
            type="checkbox"
            checked={cfc.postEqEnabled}
            onChange={(e) => setMaster({ postEqEnabled: e.target.checked })}
          />
        </Row>
        <Row label="Pre-comp (dB)">
          <NumberInput
            value={cfc.preCompDb}
            min={PRECOMP_MIN}
            max={PRECOMP_MAX}
            step={0.5}
            onChange={(v) => setMaster({ preCompDb: clamp(v, PRECOMP_MIN, PRECOMP_MAX) })}
          />
        </Row>
        <Row label="Pre-peq (dB)">
          <NumberInput
            value={cfc.prePeqDb}
            min={PREPEQ_MIN}
            max={PREPEQ_MAX}
            step={0.5}
            onChange={(v) => setMaster({ prePeqDb: clamp(v, PREPEQ_MIN, PREPEQ_MAX) })}
          />
        </Row>
      </Section>

      <Section title="Bands">
        <div style={{ overflowX: 'auto' }}>
          <table
            style={{
              width: '100%',
              borderCollapse: 'collapse',
              fontSize: 11,
            }}
          >
            <thead>
              <tr style={{ color: 'var(--fg-2)', textAlign: 'left' }}>
                <th style={th}>Band</th>
                <th style={th}>Frequency (Hz)</th>
                <th style={th}>Compression (dB)</th>
                <th style={th}>Post Gain (dB)</th>
              </tr>
            </thead>
            <tbody>
              {cfc.bands.map((b, i) => (
                <tr
                  key={i}
                  style={{
                    borderTop: '1px solid var(--panel-border)',
                    color: 'var(--fg-1)',
                  }}
                >
                  <td style={td}>{i + 1}</td>
                  <td style={td}>
                    <NumberInput
                      value={b.freqHz}
                      min={FREQ_MIN}
                      max={FREQ_MAX}
                      step={10}
                      onChange={(v) => setBand(i, { freqHz: clamp(Math.round(v), FREQ_MIN, FREQ_MAX) })}
                    />
                  </td>
                  <td style={td}>
                    <NumberInput
                      value={b.compLevelDb}
                      min={COMP_MIN}
                      max={COMP_MAX}
                      step={0.5}
                      onChange={(v) => setBand(i, { compLevelDb: clamp(v, COMP_MIN, COMP_MAX) })}
                    />
                  </td>
                  <td style={td}>
                    <NumberInput
                      value={b.postGainDb}
                      min={POST_MIN}
                      max={POST_MAX}
                      step={0.5}
                      onChange={(v) => setBand(i, { postGainDb: clamp(v, POST_MIN, POST_MAX) })}
                    />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Section>
    </div>
  );
}

const th: React.CSSProperties = {
  padding: '6px 8px',
  fontSize: 10,
  fontWeight: 700,
  letterSpacing: '0.08em',
  textTransform: 'uppercase',
};

const td: React.CSSProperties = {
  padding: '4px 8px',
};

// Visual primitives copied verbatim from PsSettingsPanel so this panel
// reads identically. Kept private to the file — not exported — so they
// don't accidentally become a "shared" pattern that drifts from
// PsSettingsPanel's source. If a real shared layout primitive is needed
// later, lift both copies into a single module then.

function Section({
  title,
  children,
}: {
  title: string;
  children: React.ReactNode;
}) {
  return (
    <section
      style={{
        border: '1px solid var(--panel-border)',
        borderRadius: 6,
        padding: '10px 12px',
        background: 'var(--bg-1)',
      }}
    >
      <h3
        style={{
          margin: 0,
          marginBottom: 8,
          fontSize: 11,
          fontWeight: 700,
          letterSpacing: '0.12em',
          textTransform: 'uppercase',
          color: 'var(--fg-1)',
        }}
      >
        {title}
      </h3>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        {children}
      </div>
    </section>
  );
}

function Row({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: 10,
        fontSize: 12,
      }}
    >
      <span style={{ minWidth: 110, color: 'var(--fg-2)' }}>{label}</span>
      <span style={{ display: 'flex', alignItems: 'center', flex: 1, gap: 6 }}>
        {children}
      </span>
    </div>
  );
}

function NumberInput({
  value,
  min,
  max,
  step,
  onChange,
  disabled,
}: {
  value: number;
  min: number;
  max: number;
  step: number;
  onChange: (v: number) => void;
  disabled?: boolean;
}) {
  return (
    <input
      type="number"
      value={value}
      min={min}
      max={max}
      step={step}
      disabled={disabled}
      onChange={(e) => {
        const v = Number(e.target.value);
        if (Number.isFinite(v)) onChange(v);
      }}
      style={{
        width: 100,
        padding: '3px 6px',
        background: 'var(--bg-0)',
        color: 'var(--fg-1)',
        border: '1px solid var(--panel-border)',
        borderRadius: 3,
        fontSize: 12,
      }}
    />
  );
}
