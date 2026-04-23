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
import { setMicGain } from '../api/client';
import { useTxStore } from '../state/tx-store';

// PRD FR-3 mic-gain range: 0..+20 dB. Server applies via WDSP
// SetTXAPanelGain1(TXA, 10^(db/20)) — same linear dB curve Thetis uses in
// audio.cs:218-224. Debounce matches DriveSlider so a drag doesn't flood
// the endpoint; optimistic store update keeps the thumb responsive.
//
// Always enabled: the TXA panel gain persists across MOX off/on, so the
// operator can dial in level against the live mic meter before keying.
const MIN = 0;
const MAX = 20;
const DEBOUNCE_MS = 100;

export function MicGainSlider() {
  const micGainDb = useTxStore((s) => s.micGainDb);
  const setMicGainDb = useTxStore((s) => s.setMicGainDb);

  const inflightAbort = useRef<AbortController | null>(null);
  const debounceTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const lastSent = useRef<number>(micGainDb);
  const previousOnError = useRef<number>(micGainDb);

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
      setMicGain(v, ac.signal)
        .then((r) => {
          if (ac.signal.aborted) return;
          if (r.micGainDb !== v) setMicGainDb(r.micGainDb);
        })
        .catch((err) => {
          if (ac.signal.aborted) return;
          if (err instanceof DOMException && err.name === 'AbortError') return;
          setMicGainDb(previousOnError.current);
          lastSent.current = previousOnError.current;
        });
    }, DEBOUNCE_MS);
  }, [setMicGainDb]);

  useEffect(() => () => {
    inflightAbort.current?.abort();
    if (debounceTimer.current != null) clearTimeout(debounceTimer.current);
  }, []);

  // Rounded on send / display so the wire contract stays integer dB, but the
  // slider itself uses 0.5-step so micro-drags cross a step boundary on the
  // ~128px-wide input. At step=1 on a 20-dB range, each step is ~6px — drags
  // under that threshold didn't move the thumb and the user had to click to
  // commit, which looked like "drag doesn't work". Fractional store value is
  // fine; the round happens at render + wire time.
  const onInput = (e: React.FormEvent<HTMLInputElement>) => {
    const v = Number(e.currentTarget.value);
    setMicGainDb(v);
    sendDebounced(Math.round(v));
  };

  return (
    <label className="knob-group">
      <span className="label-xs">MIC</span>
      <input
        type="range"
        min={MIN}
        max={MAX}
        step={0.5}
        value={micGainDb}
        onInput={onInput}
        onChange={onInput}
        style={{ flex: 1, cursor: 'pointer', accentColor: 'var(--accent)' }}
      />
      <span className="mono" style={{ width: 52, textAlign: 'right', color: 'var(--fg-1)', fontSize: 11 }}>
        +{Math.round(micGainDb)} dB
      </span>
    </label>
  );
}
