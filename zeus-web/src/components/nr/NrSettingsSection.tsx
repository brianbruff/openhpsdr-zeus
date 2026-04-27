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
// Inline NR settings section (issue #79). Renders the per-mode tunables for
// NR2 (EMNR post2) and NR4 (SBNR) directly in the DSP layout — the floating
// right-click popover variant proved unreliable to surface on disabled
// buttons across browsers, so settings live as a normal inline panel
// matching Thetis's Setup-form pattern.

import { useState } from 'react';
import {
  NR2_POST2_DEFAULTS,
  setNr2Post2,
  type RadioStateDto,
} from '../../api/client';
import { useConnectionStore } from '../../state/connection-store';

export type NrSettingsMode = 'Anr' | 'Emnr' | 'Sbnr';

export type NrSettingsSectionProps = {
  mode: NrSettingsMode;
};

export function NrSettingsSection({ mode }: NrSettingsSectionProps) {
  // NR4 (Sbnr) panel is intentionally not rendered here — bundled libwdsp
  // doesn't export the SetRXASBNR* symbols (Phase 1 of issue #79), so any
  // adjustment is silently inert. Nr4Panel is preserved below; re-enable
  // its case in this switch once Phase 1 binaries land.
  if (mode === 'Sbnr') return null;
  return (
    <div className="nr-settings" role="region" aria-label={`NR ${mode} settings`}>
      <h3 className="nr-settings__title">
        {mode === 'Anr' && 'NR1 — ANR'}
        {mode === 'Emnr' && 'NR2 — EMNR post2'}
      </h3>
      {mode === 'Anr' && <AnrPanel />}
      {mode === 'Emnr' && <Nr2Panel />}
    </div>
  );
}

// ---------- NR1 (ANR) — no exposed tunables in this iteration. ----------

function AnrPanel() {
  return (
    <p className="nr-settings__hint">
      NR1 (time-domain LMS) has no operator-tunable knobs in Zeus today.
      Defaults match Thetis: 64 taps, 16-sample delay, gain 1e-4, leakage 0.1.
    </p>
  );
}

// ---------- NR2 (EMNR) post2 comfort-noise tunables. ----------

function Nr2Panel() {
  const nr = useConnectionStore((s) => s.nr);
  const applyState = useConnectionStore((s) => s.applyState);

  const [run, setRun] = useState<boolean>(nr.emnrPost2Run ?? NR2_POST2_DEFAULTS.run);
  const [factor, setFactor] = useState<number>(nr.emnrPost2Factor ?? NR2_POST2_DEFAULTS.factor);
  const [nlevel, setNlevel] = useState<number>(nr.emnrPost2Nlevel ?? NR2_POST2_DEFAULTS.nlevel);
  const [rate, setRate] = useState<number>(nr.emnrPost2Rate ?? NR2_POST2_DEFAULTS.rate);
  const [taper, setTaper] = useState<number>(nr.emnrPost2Taper ?? NR2_POST2_DEFAULTS.taper);

  function commit() {
    setNr2Post2({
      post2Run: run,
      post2Factor: factor,
      post2Nlevel: nlevel,
      post2Rate: rate,
      post2Taper: Math.round(taper),
    })
      .then((s: RadioStateDto) => applyState(s))
      .catch(() => {
        /* state poll will reconcile */
      });
  }

  return (
    <form
      onSubmit={(e) => {
        e.preventDefault();
        commit();
      }}
    >
      <div
        className="nr-settings__row"
        title="EMNR's post-stage comfort-noise injection (post2). Off = raw EMNR output. The NR cycle button is the master on/off; this is a sub-stage of NR2 only."
      >
        <label className="nr-settings__label" htmlFor="nr2-run">Post-Process</label>
        <input
          id="nr2-run"
          type="checkbox"
          className="nr-settings__checkbox"
          checked={run}
          onChange={(e) => setRun(e.target.checked)}
        />
      </div>

      <NumericRow id="nr2-factor" label="Factor" value={factor} step={0.01} min={0} max={1} onChange={setFactor} />
      <NumericRow id="nr2-nlevel" label="Nlevel" value={nlevel} step={0.01} min={0} max={1} onChange={setNlevel} />
      <NumericRow id="nr2-rate" label="Rate" value={rate} step={0.1} min={0} onChange={setRate} />
      <NumericRow id="nr2-taper" label="Taper (bins)" value={taper} step={1} min={0} onChange={setTaper} />

      <p className="nr-settings__hint">
        Comfort-noise injection masking residual EMNR warble. Defaults: factor 0.15,
        nlevel 0.15, rate 5.0, taper 12. See emnr.c:981–1056.
      </p>

      <div className="nr-settings__buttons">
        <button type="submit" className="nr-settings__button nr-settings__button--primary">
          Save
        </button>
      </div>
    </form>
  );
}

// ---------- Shared numeric input row. ----------

type NumericRowProps = {
  id: string;
  label: string;
  value: number;
  step: number;
  min?: number;
  max?: number;
  onChange: (v: number) => void;
};

function NumericRow({ id, label, value, step, min, max, onChange }: NumericRowProps) {
  return (
    <div className="nr-settings__row">
      <label className="nr-settings__label" htmlFor={id}>{label}</label>
      <input
        id={id}
        type="number"
        className="nr-settings__input"
        value={value}
        step={step}
        min={min}
        max={max}
        onChange={(e) => {
          const v = Number(e.target.value);
          if (!Number.isNaN(v)) onChange(v);
        }}
      />
    </div>
  );
}
