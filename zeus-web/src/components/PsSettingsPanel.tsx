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
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF), with contributions from (DH1KLM); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

import { useCallback } from 'react';
import {
  setPs,
  setPsAdvanced,
  setPsFeedbackSource,
  resetPs,
  setTwoTone,
} from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useTxStore } from '../state/tx-store';

const CAL_STATE_NAMES = [
  'RESET',
  'WAIT',
  'MOXDELAY',
  'SETUP',
  'COLLECT',
  'MOXCHECK',
  'CALC',
  'DELAY',
  'STAYON',
  'TURNON',
];

/**
 * PureSignal + Two-tone control surface. Lives inside the Settings modal
 * (SettingsMenu) and as a standalone dockable panel (PsFlexPanel).
 *
 * Uses the same neutral fg/accent tokens as the other settings tabs;
 * amber is reserved for the panadapter trace per CLAUDE.md.
 */
export function PsSettingsPanel() {
  const protocol = useConnectionStore((s) => s.connectedProtocol);
  const p1Disabled = protocol === 'P1';

  const psEnabled = useTxStore((s) => s.psEnabled);
  const psAuto = useTxStore((s) => s.psAuto);
  const psSingle = useTxStore((s) => s.psSingle);
  const psPtol = useTxStore((s) => s.psPtol);
  const psAutoAttenuate = useTxStore((s) => s.psAutoAttenuate);
  const psMoxDelaySec = useTxStore((s) => s.psMoxDelaySec);
  const psLoopDelaySec = useTxStore((s) => s.psLoopDelaySec);
  const psAmpDelayNs = useTxStore((s) => s.psAmpDelayNs);
  const psHwPeak = useTxStore((s) => s.psHwPeak);
  const psIntsSpiPreset = useTxStore((s) => s.psIntsSpiPreset);
  const psFeedbackSourceState = useTxStore((s) => s.psFeedbackSource);
  const psFeedbackLevel = useTxStore((s) => s.psFeedbackLevel);
  const psCalState = useTxStore((s) => s.psCalState);
  const psCorrecting = useTxStore((s) => s.psCorrecting);
  const psCorrectionDb = useTxStore((s) => s.psCorrectionDb);
  const setPsAuto = useTxStore((s) => s.setPsAuto);
  const setPsSingle = useTxStore((s) => s.setPsSingle);
  const setPsPtol = useTxStore((s) => s.setPsPtol);
  const setPsAutoAttenuate = useTxStore((s) => s.setPsAutoAttenuate);
  const setPsMoxDelaySec = useTxStore((s) => s.setPsMoxDelaySec);
  const setPsLoopDelaySec = useTxStore((s) => s.setPsLoopDelaySec);
  const setPsAmpDelayNs = useTxStore((s) => s.setPsAmpDelayNs);
  const setPsHwPeak = useTxStore((s) => s.setPsHwPeak);
  const setPsIntsSpiPreset = useTxStore((s) => s.setPsIntsSpiPreset);
  const setPsFeedbackSourceLocal = useTxStore((s) => s.setPsFeedbackSource);

  const twoToneOn = useTxStore((s) => s.twoToneOn);
  const twoToneFreq1 = useTxStore((s) => s.twoToneFreq1);
  const twoToneFreq2 = useTxStore((s) => s.twoToneFreq2);
  const twoToneMag = useTxStore((s) => s.twoToneMag);
  const setTwoToneOn = useTxStore((s) => s.setTwoToneOn);
  const setTwoToneFreq1 = useTxStore((s) => s.setTwoToneFreq1);
  const setTwoToneFreq2 = useTxStore((s) => s.setTwoToneFreq2);
  const setTwoToneMag = useTxStore((s) => s.setTwoToneMag);

  // Cal-mode radio buttons send the new combination on every change.
  const setMode = useCallback(
    (auto: boolean, single: boolean) => {
      setPsAuto(auto);
      setPsSingle(single);
      setPs({ enabled: psEnabled, auto, single }).catch(() => {});
    },
    [psEnabled, setPsAuto, setPsSingle],
  );

  const pushAdvanced = useCallback(
    (overrides: Partial<{
      ptol: boolean;
      autoAttenuate: boolean;
      moxDelaySec: number;
      loopDelaySec: number;
      ampDelayNs: number;
      hwPeak: number;
      intsSpiPreset: string;
    }>) => {
      setPsAdvanced(overrides).catch(() => {});
    },
    [],
  );

  const onReset = useCallback(() => {
    resetPs().catch(() => {});
  }, []);

  // Feedback antenna source — Internal coupler vs External (Bypass).
  // Optimistic local set + POST. Rolls back on POST failure so the radio's
  // alex bit and the UI stay in sync.
  const onFeedbackSourceChange = useCallback(
    (next: 'internal' | 'external') => {
      const prev = psFeedbackSourceState;
      setPsFeedbackSourceLocal(next);
      setPsFeedbackSource(next).catch(() => setPsFeedbackSourceLocal(prev));
    },
    [psFeedbackSourceState, setPsFeedbackSourceLocal],
  );

  const onTwoToneToggle = useCallback(() => {
    const next = !twoToneOn;
    setTwoToneOn(next);
    setTwoTone({
      enabled: next,
      freq1: twoToneFreq1,
      freq2: twoToneFreq2,
      mag: twoToneMag,
    }).catch(() => setTwoToneOn(!next));
  }, [twoToneOn, twoToneFreq1, twoToneFreq2, twoToneMag, setTwoToneOn]);

  // Two-tone freq/mag POSTs always go to the server, even when twoToneOn is
  // false. The server persists freq1/freq2/mag via PsSettingsStore so an
  // operator who dials in tones first ("set up the test, then arm") sees the
  // values stick across restarts. Server SetTwoTone accepts partial fields
  // and only flips the master arm if `enabled` changes — passing the current
  // twoToneOn state keeps the radio's TwoTone arm state untouched.
  const onTwoToneFreq1Change = useCallback(
    (hz: number) => {
      const v = Math.max(50, Math.min(5000, Math.round(hz)));
      setTwoToneFreq1(v);
      setTwoTone({ enabled: twoToneOn, freq1: v }).catch(() => {});
    },
    [twoToneOn, setTwoToneFreq1],
  );

  const onTwoToneFreq2Change = useCallback(
    (hz: number) => {
      const v = Math.max(50, Math.min(5000, Math.round(hz)));
      setTwoToneFreq2(v);
      setTwoTone({ enabled: twoToneOn, freq2: v }).catch(() => {});
    },
    [twoToneOn, setTwoToneFreq2],
  );

  const onTwoToneMagChange = useCallback(
    (mag: number) => {
      const v = Math.max(0, Math.min(1, mag));
      setTwoToneMag(v);
      setTwoTone({ enabled: twoToneOn, mag: v }).catch(() => {});
    },
    [twoToneOn, setTwoToneMag],
  );

  const calStateLabel = CAL_STATE_NAMES[psCalState] ?? `state ${psCalState}`;
  // Feedback level is 0..256 raw; UI shows 0..1.
  const feedbackBar = Math.max(0, Math.min(1, psFeedbackLevel / 256));

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
      {p1Disabled ? (
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
          PureSignal predistortion for Hermes / Protocol 1 is coming in a
          follow-up. Two-tone test generator below works on both protocols.
        </div>
      ) : null}

      {/* Calibration */}
      <Section title="Calibration">
        <Row label="Mode">
          <label style={{ marginRight: 12 }}>
            <input
              type="radio"
              name="ps-mode"
              checked={psAuto && !psSingle}
              onChange={() => setMode(true, false)}
              disabled={p1Disabled}
            />{' '}
            Auto
          </label>
          <label>
            <input
              type="radio"
              name="ps-mode"
              checked={psSingle}
              onChange={() => setMode(false, true)}
              disabled={p1Disabled}
            />{' '}
            Single
          </label>
        </Row>
        <Row label="">
          <button
            type="button"
            onClick={onReset}
            disabled={p1Disabled}
            className="btn sm"
          >
            Reset
          </button>
        </Row>
        <Row label="Auto-Attenuate">
          <input
            type="checkbox"
            checked={psAutoAttenuate}
            onChange={(e) => {
              const v = e.target.checked;
              setPsAutoAttenuate(v);
              pushAdvanced({ autoAttenuate: v });
            }}
            disabled={p1Disabled}
          />
        </Row>
      </Section>

      {/* Timing */}
      <Section title="Timing">
        <Row label="MOX delay (s)">
          <NumberInput
            value={psMoxDelaySec}
            min={0.0}
            max={10.0}
            step={0.1}
            onChange={(v) => {
              setPsMoxDelaySec(v);
              pushAdvanced({ moxDelaySec: v });
            }}
            disabled={p1Disabled}
          />
        </Row>
        <Row label="Cal delay (s)">
          <NumberInput
            value={psLoopDelaySec}
            min={0.0}
            max={100.0}
            step={0.5}
            onChange={(v) => {
              setPsLoopDelaySec(v);
              pushAdvanced({ loopDelaySec: v });
            }}
            disabled={p1Disabled}
          />
        </Row>
        <Row label="Amp delay (ns)">
          <NumberInput
            value={psAmpDelayNs}
            min={0}
            max={25_000_000}
            step={50}
            onChange={(v) => {
              setPsAmpDelayNs(v);
              pushAdvanced({ ampDelayNs: v });
            }}
            disabled={p1Disabled}
          />
        </Row>
      </Section>

      {/* Hardware */}
      <Section title="Hardware">
        <Row label="Feedback source">
          {/* Two-way selector: Internal coupler (default) or External
              (Bypass). On G2/MkII this flips ALEX_RX_ANTENNA_BYPASS in
              alex0 during xmit + PS armed. WDSP cal/iqc are unaffected;
              the HW-peak slider below stays shared across sources to
              match pihpsdr/Thetis. Disabled on P1 because P1 PS isn't
              wired through yet. */}
          <label
            style={{ display: 'inline-flex', alignItems: 'center', marginRight: 12 }}
          >
            <input
              type="radio"
              name="psFeedbackSource"
              value="internal"
              checked={psFeedbackSourceState === 'internal'}
              onChange={() => onFeedbackSourceChange('internal')}
              disabled={p1Disabled}
              style={{ marginRight: 4 }}
            />
            <span style={{ fontSize: 11, color: 'var(--fg-1)' }}>Internal coupler</span>
          </label>
          <label style={{ display: 'inline-flex', alignItems: 'center' }}>
            <input
              type="radio"
              name="psFeedbackSource"
              value="external"
              checked={psFeedbackSourceState === 'external'}
              onChange={() => onFeedbackSourceChange('external')}
              disabled={p1Disabled}
              style={{ marginRight: 4 }}
            />
            <span style={{ fontSize: 11, color: 'var(--fg-1)' }}>External (Bypass)</span>
          </label>
        </Row>
        <Row label="HW peak">
          <NumberInput
            value={psHwPeak}
            min={0.01}
            max={2.0}
            step={0.001}
            onChange={(v) => {
              setPsHwPeak(v);
              pushAdvanced({ hwPeak: v });
            }}
            disabled={p1Disabled}
          />
        </Row>
        <Row label="Ints / Spi">
          <select
            value={psIntsSpiPreset}
            onChange={(e) => {
              const v = e.target.value;
              setPsIntsSpiPreset(v);
              pushAdvanced({ intsSpiPreset: v });
            }}
            disabled={p1Disabled}
          >
            <option value="16/256">16 / 256</option>
            <option value="8/512">8 / 512</option>
            <option value="4/1024">4 / 1024</option>
          </select>
        </Row>
        <Row label="Relax phase tol">
          <input
            type="checkbox"
            checked={psPtol}
            onChange={(e) => {
              const v = e.target.checked;
              setPsPtol(v);
              pushAdvanced({ ptol: v });
            }}
            disabled={p1Disabled}
          />
        </Row>
      </Section>

      {/* Read-out */}
      <Section title="Read-out">
        <Row label="Feedback">
          <Bar value={feedbackBar} />
          <span style={{ fontSize: 11, color: 'var(--fg-2)', marginLeft: 8 }}>
            {psFeedbackLevel.toFixed(0)} / 256
          </span>
        </Row>
        <Row label="Cal state">
          <span style={{ fontSize: 11, color: 'var(--fg-1)' }}>
            {calStateLabel}
            {psCorrecting ? ' · correcting' : ''}
          </span>
        </Row>
        <Row label="Correction">
          <span style={{ fontSize: 11, color: 'var(--fg-1)' }}>
            {psCorrecting ? `${psCorrectionDb.toFixed(1)} dB` : '—'}
          </span>
        </Row>
      </Section>

      {/* Two-tone */}
      <Section title="Two-tone test signal">
        <Row label="">
          <button
            type="button"
            onClick={onTwoToneToggle}
            className={`btn sm ${twoToneOn ? 'active' : ''}`}
            title="Standard PureSignal calibration excitation"
          >
            {twoToneOn ? '2-Tone ON' : '2-Tone OFF'}
          </button>
        </Row>
        <Row label="Freq 1 (Hz)">
          <NumberInput
            value={twoToneFreq1}
            min={50}
            max={5000}
            step={10}
            onChange={onTwoToneFreq1Change}
          />
        </Row>
        <Row label="Freq 2 (Hz)">
          <NumberInput
            value={twoToneFreq2}
            min={50}
            max={5000}
            step={10}
            onChange={onTwoToneFreq2Change}
          />
        </Row>
        <Row label="Magnitude">
          <NumberInput
            value={twoToneMag}
            min={0}
            max={1}
            step={0.01}
            onChange={onTwoToneMagChange}
          />
        </Row>
      </Section>
    </div>
  );
}

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

function Bar({ value }: { value: number }) {
  const pct = Math.max(0, Math.min(1, value)) * 100;
  return (
    <div
      style={{
        width: 140,
        height: 8,
        background: 'var(--accent-soft)',
        borderRadius: 2,
        overflow: 'hidden',
      }}
    >
      <div
        style={{
          width: `${pct}%`,
          height: '100%',
          background: 'var(--accent)',
          transition: 'width 80ms linear',
        }}
      />
    </div>
  );
}
