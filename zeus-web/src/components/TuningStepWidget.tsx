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

import { useState } from 'react';

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

export function TuningStepWidget() {
  const [stepHz, setStepHz] = useState<number>(DEFAULT_STEP_HZ);

  return (
    <>
      {/* Desktop: single-line grid of equal-width step buttons. Cells use
          minmax(0, 1fr) so the row shrinks gracefully with the panel. */}
      <div className="ctrl-group hide-mobile" style={{ width: '100%' }}>
        <div className="label-xs ctrl-lbl">TUNE STEP</div>
        <div className="step-grid">
          {TUNING_STEPS.map((step) => (
            <button
              key={step.hz}
              type="button"
              onClick={() => setStepHz(step.hz)}
              className={`btn sm ${stepHz === step.hz ? 'active' : ''}`}
              title={`Wheel-on-digit step: ${step.label}`}
            >
              {step.label}
            </button>
          ))}
        </div>
      </div>

      {/* Mobile: dropdown */}
      <div className="ctrl-group show-mobile" style={{ display: 'none' }}>
        <div className="label-xs ctrl-lbl">TUNE STEP</div>
        <select
          value={stepHz}
          onChange={(e) => setStepHz(Number(e.target.value))}
          className="step-select"
          style={{
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
      </div>
    </>
  );
}
