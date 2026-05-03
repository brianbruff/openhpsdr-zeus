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
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

import { useCallback } from 'react';
import {
  setPs,
  setPsAdvanced,
  setPsFeedbackSource,
  setPsMonitor,
  resetPs,
} from '../api/client';
import { useRadioStore } from '../state/radio-store';
import { useTxStore } from '../state/tx-store';

// HermesLite2 has no internal feedback coupler — the operator-side PS
// Monitor (post-PA loopback display) and the Internal/External feedback
// source selector both reduce to a single working choice on HL2:
// "External coupler". We hide the Internal/External selector on HL2 (the
// operator can't pick anything else) and the PS-Monitor toggle on HL2
// (no internal loopback to display). ANAN-class boards (and anything
// else that shows up here) retain both controls. See issues #121 and #172.
const HL2_BOARD_ID = 'HermesLite2';

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
  // The PS-Monitor source switch only makes sense when there's a real PS
  // feedback receiver. On HL2 we hide the toggle entirely. We key on the
  // CONNECTED board (not preferred) so a user who explicitly selects G2
  // while no radio is attached still sees the control as a preview, but
  // a live HL2 connection cleanly drops it. The Internal-vs-External
  // feedback source selector remains visible on every board (including
  // HL2) — even though HL2 currently routes only through the external
  // coupler path, an operator running HL2 + future amp setup may want
  // the option exposed.
  const connectedBoard = useRadioStore((s) => s.selection.connected);
  const psMonitorSupported = connectedBoard !== HL2_BOARD_ID;
  const feedbackSourceSelectorSupported = true;

  const psEnabled = useTxStore((s) => s.psEnabled);
  const psMonitorEnabled = useTxStore((s) => s.psMonitorEnabled);
  const setPsMonitorLocal = useTxStore((s) => s.setPsMonitorEnabled);
  const psAuto = useTxStore((s) => s.psAuto);
  const psSingle = useTxStore((s) => s.psSingle);
  const psPtol = useTxStore((s) => s.psPtol);
  const psAutoAttenuate = useTxStore((s) => s.psAutoAttenuate);
  const psMoxDelaySec = useTxStore((s) => s.psMoxDelaySec);
  const psLoopDelaySec = useTxStore((s) => s.psLoopDelaySec);
  const psAmpDelayNs = useTxStore((s) => s.psAmpDelayNs);
  const psHwPeak = useTxStore((s) => s.psHwPeak);
  const psHwPeakDefault = useTxStore((s) => s.psHwPeakDefault);
  const psIntsSpiPreset = useTxStore((s) => s.psIntsSpiPreset);
  const psFeedbackSourceState = useTxStore((s) => s.psFeedbackSource);
  const psFeedbackLevel = useTxStore((s) => s.psFeedbackLevel);
  const psCalState = useTxStore((s) => s.psCalState);
  const psCorrecting = useTxStore((s) => s.psCorrecting);
  const psCorrectionDb = useTxStore((s) => s.psCorrectionDb);
  // Observed TX envelope peak — mi0bot PSForm.cs:624 reads
  // GetPSMaxTX(_txachannel, ptr) every timer tick and shows it in
  // txtGetPSpeak so the operator can compare against HW peak. Zeus already
  // pumps this from WdspDspEngine.GetPsStageMeters → PsMetersFrame; we just
  // render the live value next to the HW-peak input.
  const psMaxTxEnvelope = useTxStore((s) => s.psMaxTxEnvelope);
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

  // PS-Monitor — operator-facing display source switch (issue #121).
  // Optimistic local set + POST, rolls back on failure so the analyzer
  // routing the backend uses and the UI checkbox stay in sync.
  const onPsMonitorToggle = useCallback(
    (next: boolean) => {
      const prev = psMonitorEnabled;
      setPsMonitorLocal(next);
      setPsMonitor(next).catch(() => setPsMonitorLocal(prev));
    },
    [psMonitorEnabled, setPsMonitorLocal],
  );

  const calStateLabel = CAL_STATE_NAMES[psCalState] ?? `state ${psCalState}`;
  // Feedback level is 0..256 raw; UI shows 0..1.
  const feedbackBar = Math.max(0, Math.min(1, psFeedbackLevel / 256));

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
      {/* Calibration */}
      <Section title="Calibration">
        <Row label="Mode">
          <label style={{ marginRight: 12 }}>
            <input
              type="radio"
              name="ps-mode"
              checked={psAuto && !psSingle}
              onChange={() => setMode(true, false)}
            />{' '}
            Auto
          </label>
          <label>
            <input
              type="radio"
              name="ps-mode"
              checked={psSingle}
              onChange={() => setMode(false, true)}
            />{' '}
            Single
          </label>
        </Row>
        <Row label="">
          <button
            type="button"
            onClick={onReset}
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
          />
        </Row>
      </Section>

      {/* Hardware */}
      <Section title="Hardware">
        {feedbackSourceSelectorSupported ? (
          <Row label="Feedback source">
            {/* Two-way selector: Internal coupler (default) or External
                (Bypass). On G2/MkII this flips ALEX_RX_ANTENNA_BYPASS in
                alex0 during xmit + PS armed. WDSP cal/iqc are unaffected;
                the HW-peak slider below stays shared across sources to
                match pihpsdr/Thetis.

                Hidden on HL2: HL2 has no internal coupler, so the only
                workable feedback path is external (issue #172). The
                backend default already routes the wire bit appropriately
                for HL2 — there's no operator choice to expose. */}
            <label
              style={{ display: 'inline-flex', alignItems: 'center', marginRight: 12 }}
            >
              <input
                type="radio"
                name="psFeedbackSource"
                value="internal"
                checked={psFeedbackSourceState === 'internal'}
                onChange={() => onFeedbackSourceChange('internal')}
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
                style={{ marginRight: 4 }}
              />
              <span style={{ fontSize: 11, color: 'var(--fg-1)' }}>External (Bypass)</span>
            </label>
          </Row>
        ) : null}
        <Row label="HW peak">
          {/* mi0bot ref: PSForm.cs PSpeak_TextChanged fires on every text
              change regardless of whether the value differs from the prior
              value, so re-typing the same number re-pushes it to WDSP and
              resets calcc state. React's controlled <input> only fires
              onChange when the rendered value actually changes, so a stale
              focus-and-blur with no edit is silently dropped. Mirror the
              mi0bot semantic by firing setPsAdvanced unconditionally on
              blur / Enter via onCommit, in addition to the live onChange
              path used by the other advanced fields. */}
          <NumberInput
            value={psHwPeak}
            min={0.01}
            max={2.0}
            step={0.001}
            onChange={(v) => {
              setPsHwPeak(v);
              pushAdvanced({ hwPeak: v });
            }}
            onCommit={(v) => {
              setPsHwPeak(v);
              pushAdvanced({ hwPeak: v });
            }}
          />
          {/* mi0bot ref: PSForm.cs:830
              `pbWarningSetPk.Visible = _PShwpeak != HardwareSpecific.PSDefaultPeak;`
              + clsHardwareSpecific.cs:303-328 PSDefaultPeak per-board switch.
              Show a small accent glyph after the input when the operator has
              dialed PsHwPeak away from the per-board factory default. Title
              attribute exposes the resolved default for the operator so they
              know what value would clear the indicator. */}
          {psHwPeak !== psHwPeakDefault ? (
            <span
              aria-label="HW peak differs from per-board default"
              title={`Differs from default ${psHwPeakDefault.toFixed(4)}`}
              style={{
                color: 'var(--accent)',
                fontSize: 12,
                fontWeight: 700,
                marginLeft: 4,
                userSelect: 'none',
              }}
            >
              *
            </span>
          ) : null}
          {/* mi0bot ref: PSForm.cs:991-994 btnDefaultPeaks_Click →
              SetDefaultPeaks → psdefpeak (PSForm.cs:371-381) writes
              HardwareSpecific.PSDefaultPeak (clsHardwareSpecific.cs:303-328)
              into txtPSpeak. One-click reset to the per-board factory default
              for operators who've drifted off and want to start over.
              Disabled when already at the default — no point re-pushing the
              same value through this button (the field's onCommit covers
              the deliberate re-push case). */}
          <button
            type="button"
            className="btn sm"
            disabled={psHwPeak === psHwPeakDefault}
            title={`Reset HW peak to per-board default ${psHwPeakDefault.toFixed(4)}`}
            onClick={() => {
              setPsHwPeak(psHwPeakDefault);
              pushAdvanced({ hwPeak: psHwPeakDefault });
            }}
          >
            Default
          </button>
        </Row>
        {/* mi0bot ref: PSForm.cs:624 GetPSMaxTX → PSForm.designer.cs
            txtGetPSpeak readout. Read-only; the operator dials HW peak
            above to match the observed envelope max during calibration. */}
        <Row label="Observed peak">
          <span style={{ fontSize: 11, color: 'var(--fg-1)' }}>
            {psMaxTxEnvelope.toFixed(4)}
          </span>
        </Row>
        <Row label="Ints / Spi">
          <select
            value={psIntsSpiPreset}
            onChange={(e) => {
              const v = e.target.value;
              setPsIntsSpiPreset(v);
              pushAdvanced({ intsSpiPreset: v });
            }}
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
          />
        </Row>
      </Section>

      {/* Display — issue #121. Hidden on HL2 (no PS feedback Rx). The
          control is operator opt-in; default off preserves the Thetis-
          style predistorted-IQ panadapter view. The backend gates the
          actual analyzer swap on PsEnabled && PsCorrecting in addition
          to this flag, so flipping it on while PS is off is harmless. */}
      {psMonitorSupported ? (
        <Section title="Display">
          <Row label="Monitor PA output">
            <input
              type="checkbox"
              checked={psMonitorEnabled}
              onChange={(e) => onPsMonitorToggle(e.target.checked)}
              title="Show post-correction signal in TX panadapter"
            />
            <span style={{ fontSize: 11, color: 'var(--fg-2)', marginLeft: 8 }}>
              Show post-correction signal in TX panadapter
            </span>
          </Row>
        </Section>
      ) : null}

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
  onCommit,
  disabled,
}: {
  value: number;
  min: number;
  max: number;
  step: number;
  onChange: (v: number) => void;
  // mi0bot ref: PSForm.cs PSpeak_TextChanged — onCommit fires on blur and
  // Enter unconditionally so re-entering the same value still re-pushes,
  // mirroring WinForms TextChanged-on-every-keystroke semantics that React's
  // controlled-input dedup otherwise hides on focus/blur with no edit.
  onCommit?: (v: number) => void;
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
      onBlur={(e) => {
        if (!onCommit) return;
        const v = Number(e.target.value);
        if (!Number.isFinite(v)) return;
        // React-controlled-input cosmetic ("00.18" → "0.18", ".5" → "0.5",
        // trailing zeros trimmed); mi0bot WinForms NumericUpDown handles via
        // .Value setter. State is already the parsed number, but a
        // controlled <input type="number"> does not re-render when the
        // parsed value matches state, so the raw text sticks until
        // something else changes — write the canonical form back to the DOM.
        const normalized = String(v);
        if (normalized !== e.target.value) e.target.value = normalized;
        onCommit(v);
      }}
      onKeyDown={(e) => {
        if (!onCommit || e.key !== 'Enter') return;
        const v = Number((e.target as HTMLInputElement).value);
        if (Number.isFinite(v)) onCommit(v);
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
