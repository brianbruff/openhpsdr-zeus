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

import { useCallback, useEffect, useRef } from 'react';
import { setDrive } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useTxStore } from '../state/tx-store';

// PRD FR-4 drive range: 0..100 percent. Per-pixel POSTs would flood the
// server during a drag — trailing-edge debounce keeps the wire quiet while
// still giving a responsive thumb because the store updates optimistically.
const MIN = 0;
const MAX = 100;
const DEBOUNCE_MS = 100;

export function DriveSlider() {
  const connected = useConnectionStore((s) => s.status === 'Connected');
  const drivePercent = useTxStore((s) => s.drivePercent);
  const setDrivePercent = useTxStore((s) => s.setDrivePercent);

  const inflightAbort = useRef<AbortController | null>(null);
  const debounceTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const lastSent = useRef<number>(drivePercent);
  const previousOnError = useRef<number>(drivePercent);

  const sendDebounced = useCallback((v: number) => {
    if (debounceTimer.current != null) clearTimeout(debounceTimer.current);
    debounceTimer.current = setTimeout(() => {
      if (v === lastSent.current) return;
      inflightAbort.current?.abort();
      const ac = new AbortController();
      inflightAbort.current = ac;
      const prevValue = lastSent.current;
      lastSent.current = v;
      previousOnError.current = prevValue;
      setDrive(v, ac.signal)
        .then((r) => {
          if (ac.signal.aborted) return;
          if (r.drivePercent !== v) setDrivePercent(r.drivePercent);
        })
        .catch((err) => {
          if (ac.signal.aborted) return;
          if (err instanceof DOMException && err.name === 'AbortError') return;
          // Roll back the optimistic update so the user sees the real state.
          setDrivePercent(previousOnError.current);
          lastSent.current = previousOnError.current;
        });
    }, DEBOUNCE_MS);
  }, [setDrivePercent]);

  useEffect(() => () => {
    inflightAbort.current?.abort();
    if (debounceTimer.current != null) clearTimeout(debounceTimer.current);
  }, []);

  const onChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const v = Number(e.currentTarget.value);
    setDrivePercent(v);
    sendDebounced(v);
  };

  return (
    <label className="knob-group">
      <span className="label-xs">DRV</span>
      <input
        type="range"
        min={MIN}
        max={MAX}
        step={1}
        value={drivePercent}
        disabled={!connected}
        onChange={onChange}
        style={{ flex: 1, cursor: 'pointer', accentColor: 'var(--accent)' }}
      />
      <span className="mono" style={{ width: 40, textAlign: 'right', color: 'var(--power)', fontSize: 11, fontWeight: 700 }}>
        {drivePercent}%
      </span>
    </label>
  );
}
