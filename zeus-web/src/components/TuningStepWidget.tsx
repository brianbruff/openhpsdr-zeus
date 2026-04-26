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

import { useCallback, useState } from 'react';
import { setVfo } from '../api/client';
import { useConnectionStore } from '../state/connection-store';

type TuningStep = {
  hz: number;
  label: string;
};

// Thetis-compatible tuning steps, from fine to coarse
const TUNING_STEPS: readonly TuningStep[] = [
  { hz: 1, label: '1 Hz' },
  { hz: 10, label: '10 Hz' },
  { hz: 50, label: '50 Hz' },
  { hz: 100, label: '100 Hz' },
  { hz: 250, label: '250 Hz' },
  { hz: 500, label: '500 Hz' },
  { hz: 1_000, label: '1 kHz' },
  { hz: 5_000, label: '5 kHz' },
  { hz: 9_000, label: '9 kHz' },
  { hz: 10_000, label: '10 kHz' },
  { hz: 100_000, label: '100 kHz' },
  { hz: 250_000, label: '250 kHz' },
  { hz: 1_000_000, label: '1 MHz' },
];

const DEFAULT_STEP_HZ = 500;

function clampHz(hz: number): number {
  const MAX_HZ = 60_000_000;
  if (!Number.isFinite(hz)) return 0;
  return Math.min(MAX_HZ, Math.max(0, Math.trunc(hz)));
}

export function TuningStepWidget() {
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const applyState = useConnectionStore((s) => s.applyState);
  const [stepHz, setStepHz] = useState(DEFAULT_STEP_HZ);

  const tune = useCallback(
    (direction: 1 | -1) => {
      const next = clampHz(vfoHz + direction * stepHz);
      if (next === vfoHz) return;
      useConnectionStore.setState({ vfoHz: next });
      setVfo(next)
        .then(applyState)
        .catch(() => {
          /* next state poll will reconcile */
        });
    },
    [vfoHz, stepHz, applyState],
  );

  const currentStep = (TUNING_STEPS.find((s) => s.hz === stepHz) || TUNING_STEPS[5]) as TuningStep;

  return (
    <div className="ctrl-group" style={{ minWidth: 200 }}>
      <div className="label-xs ctrl-lbl">TUNE STEP</div>
      <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
        <button
          type="button"
          onClick={() => tune(-1)}
          className="btn sm"
          style={{ width: 32 }}
          title={`Tune down by ${currentStep.label}`}
        >
          ◀
        </button>
        <select
          value={stepHz}
          onChange={(e) => setStepHz(Number(e.target.value))}
          className="step-select"
          style={{
            flex: 1,
            background: 'var(--btn-top)',
            color: 'var(--fg-0)',
            border: '1px solid var(--line)',
            borderRadius: 'var(--r-sm)',
            padding: '4px 8px',
            fontSize: '11px',
            fontWeight: 600,
            cursor: 'pointer',
          }}
        >
          {TUNING_STEPS.map((step) => (
            <option key={step.hz} value={step.hz}>
              {step.label}
            </option>
          ))}
        </select>
        <button
          type="button"
          onClick={() => tune(1)}
          className="btn sm"
          style={{ width: 32 }}
          title={`Tune up by ${currentStep.label}`}
        >
          ▶
        </button>
      </div>
    </div>
  );
}
