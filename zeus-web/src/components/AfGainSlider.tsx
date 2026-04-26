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

import { useCallback, useEffect, useRef, useState } from 'react';
import { setRxAfGain } from '../api/client';
import { useConnectionStore } from '../state/connection-store';

// Master RX AF gain in dB. Drives WDSP's SetRXAPanelGain1(linear) server-side
// after a dB→linear conversion; the browser audio GainNode stays at 1.0 so
// the full operator range is realised in the DSP chain, not the soundcard.
// -50..+20 mirrors Thetis's ptbAF range (console.cs:4312-4313). 0 dB is the
// engine's fresh-open default — slider at centre on first connect is
// audibly identical to pre-issue-#77 builds.
const MIN = -50;
const MAX = 20;

export function AfGainSlider() {
  const serverAf = useConnectionStore((s) => s.rxAfGainDb);
  const connected = useConnectionStore((s) => s.status === 'Connected');
  const applyState = useConnectionStore((s) => s.applyState);

  const [dragValue, setDragValue] = useState<number | null>(null);
  const value = dragValue ?? serverAf;

  const inflightAbort = useRef<AbortController | null>(null);
  const latestSent = useRef<number>(serverAf);

  const sendValue = useCallback(
    (v: number) => {
      if (v === latestSent.current) return;
      latestSent.current = v;
      inflightAbort.current?.abort();
      const ac = new AbortController();
      inflightAbort.current = ac;
      setRxAfGain(v, ac.signal)
        .then((next) => {
          if (!ac.signal.aborted) applyState(next);
        })
        .catch(() => {
          /* next poll will reconcile; don't noisily log on abort */
        });
    },
    [applyState],
  );

  useEffect(() => () => inflightAbort.current?.abort(), []);

  return (
    <label className="knob-group" style={{ minWidth: 170 }}>
      <span className="label-xs" style={{ whiteSpace: 'nowrap' }}>AF</span>
      <input
        type="range"
        min={MIN}
        max={MAX}
        step={1}
        value={value}
        disabled={!connected}
        onChange={(e) => setDragValue(Number(e.currentTarget.value))}
        onMouseUp={() => {
          if (dragValue !== null) sendValue(dragValue);
          setDragValue(null);
        }}
        onTouchEnd={() => {
          if (dragValue !== null) sendValue(dragValue);
          setDragValue(null);
        }}
        onKeyUp={() => {
          if (dragValue !== null) sendValue(dragValue);
          setDragValue(null);
        }}
        style={{ flex: 1, cursor: 'pointer', accentColor: 'var(--accent)' }}
      />
      <span className="mono" style={{ width: 48, textAlign: 'right', color: 'var(--fg-1)', fontSize: 11 }}>
        {value} dB
      </span>
    </label>
  );
}
