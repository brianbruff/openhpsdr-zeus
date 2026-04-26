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
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF), with contributions from (DH1KLM); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { useCallback } from 'react';
import { setMode, type RxMode } from '../api/client';
import { useConnectionStore } from '../state/connection-store';

type ModeEntry = { value: RxMode; label: string };

const MODES: readonly ModeEntry[] = [
  { value: 'LSB', label: 'LSB' },
  { value: 'USB', label: 'USB' },
  { value: 'CWL', label: 'CWL' },
  { value: 'CWU', label: 'CWU' },
  { value: 'AM', label: 'AM' },
  { value: 'SAM', label: 'SAM' },
  { value: 'DSB', label: 'DSB' },
  { value: 'FM', label: 'FM' },
  { value: 'DIGL', label: 'DIGL' },
  { value: 'DIGU', label: 'DIGU' },
];

export function ModeBandwidth() {
  const mode = useConnectionStore((s) => s.mode);
  const applyState = useConnectionStore((s) => s.applyState);

  const selectMode = useCallback(
    (m: RxMode) => {
      if (m === mode) return;
      useConnectionStore.setState({ mode: m });
      setMode(m)
        .then(applyState)
        .catch(() => {
          /* next state poll will reconcile */
        });
    },
    [mode, applyState],
  );

  return (
    <>
      {/* Desktop: horizontal row of mode buttons */}
      <div className="ctrl-group hide-mobile">
        <div className="label-xs ctrl-lbl">MODE</div>
        <div className="btn-row wrap" style={{ width: 236 }}>
          {MODES.map((m) => (
            <button
              key={m.value}
              type="button"
              onClick={() => selectMode(m.value)}
              className={`btn sm ${mode === m.value ? 'active' : ''}`}
            >
              {m.label}
            </button>
          ))}
        </div>
      </div>

      {/* Mobile: dropdown for mode selection */}
      <div className="ctrl-group show-mobile" style={{ display: 'none' }}>
        <div className="label-xs ctrl-lbl">MODE</div>
        <select
          value={mode}
          onChange={(e) => selectMode(e.target.value as RxMode)}
          className="mode-select"
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
          {MODES.map((m) => (
            <option key={m.value} value={m.value}>
              {m.label}
            </option>
          ))}
        </select>
      </div>
    </>
  );
}
