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
import {
  setNr,
  type NbMode,
  type NrConfigDto,
  type NrMode,
} from '../api/client';
import { useConnectionStore } from '../state/connection-store';

// NR-button cycle mirrors Thetis: Off → NR1 (ANR, time-domain LMS) → NR2
// (EMNR, Ephraim–Malah spectral). ANR and EMNR are mutually exclusive in
// WDSP so both ride the one enum.
const NR_CYCLE: readonly NrMode[] = ['Off', 'Anr', 'Emnr'];
const NR_LABEL: Record<NrMode, string> = {
  Off: 'NR',
  Anr: 'NR',
  Emnr: 'NR2',
};

const NB_CYCLE: readonly NbMode[] = ['Off', 'Nb1', 'Nb2'];
const NB_LABEL: Record<NbMode, string> = {
  Off: 'NB',
  Nb1: 'NB1',
  Nb2: 'NB2',
};

const ACTIVE_BTN = 'btn sm active';
const IDLE_BTN = 'btn sm';
const DISABLED = '';

export function NrControls() {
  const nr = useConnectionStore((s) => s.nr);
  const setLocalNr = useConnectionStore((s) => s.setNr);
  const applyState = useConnectionStore((s) => s.applyState);
  const connected = useConnectionStore((s) => s.status === 'Connected');

  const inflightAbort = useRef<AbortController | null>(null);
  useEffect(() => () => inflightAbort.current?.abort(), []);

  const send = useCallback(
    (next: NrConfigDto) => {
      setLocalNr(next);
      inflightAbort.current?.abort();
      const ac = new AbortController();
      inflightAbort.current = ac;
      setNr(next, ac.signal)
        .then((s) => {
          if (!ac.signal.aborted) applyState(s);
        })
        .catch(() => {
          /* next state poll will reconcile */
        });
    },
    [setLocalNr, applyState],
  );

  const cycleNr = useCallback(() => {
    const idx = NR_CYCLE.indexOf(nr.nrMode);
    const nextIdx = (idx < 0 ? 0 : idx + 1) % NR_CYCLE.length;
    send({ ...nr, nrMode: NR_CYCLE[nextIdx]! });
  }, [nr, send]);

  const cycleNb = useCallback(() => {
    const idx = NB_CYCLE.indexOf(nr.nbMode);
    const nextIdx = (idx < 0 ? 0 : idx + 1) % NB_CYCLE.length;
    send({ ...nr, nbMode: NB_CYCLE[nextIdx]! });
  }, [nr, send]);

  const toggleAnf = useCallback(
    () => send({ ...nr, anfEnabled: !nr.anfEnabled }),
    [nr, send],
  );
  const toggleSnb = useCallback(
    () => send({ ...nr, snbEnabled: !nr.snbEnabled }),
    [nr, send],
  );
  const toggleNbp = useCallback(
    () => send({ ...nr, nbpNotchesEnabled: !nr.nbpNotchesEnabled }),
    [nr, send],
  );

  const nrActive = nr.nrMode !== 'Off';
  const nbActive = nr.nbMode !== 'Off';

  return (
    <div className="btn-row">
      <button
        type="button"
        disabled={!connected}
        onClick={cycleNb}
        className={`${nbActive ? ACTIVE_BTN : IDLE_BTN} ${DISABLED}`}
        title={
          nr.nbMode === 'Off'
            ? 'Noise blanker off'
            : nr.nbMode === 'Nb1'
              ? 'NB1 (time-domain blanker, xanbEXT)'
              : 'NB2 (time-domain blanker, xnobEXT)'
        }
      >
        {NB_LABEL[nr.nbMode]}
      </button>
      <button
        type="button"
        disabled={!connected}
        onClick={cycleNr}
        className={`${nrActive ? ACTIVE_BTN : IDLE_BTN} ${DISABLED}`}
        title={
          nr.nrMode === 'Off'
            ? 'Noise reduction off'
            : nr.nrMode === 'Anr'
              ? 'NR1 (ANR, time-domain LMS)'
              : 'NR2 (EMNR, spectral)'
        }
      >
        {NR_LABEL[nr.nrMode]}
      </button>
      <button
        type="button"
        disabled={!connected}
        onClick={toggleAnf}
        className={`${nr.anfEnabled ? ACTIVE_BTN : IDLE_BTN} ${DISABLED}`}
        title="ANF — adaptive auto-notch (time domain)"
      >
        ANF
      </button>
      <button
        type="button"
        disabled={!connected}
        onClick={toggleSnb}
        className={`${nr.snbEnabled ? ACTIVE_BTN : IDLE_BTN} ${DISABLED}`}
        title="SNB — spectral noise blanker"
      >
        SNB
      </button>
      <button
        type="button"
        disabled={!connected}
        onClick={toggleNbp}
        className={`${nr.nbpNotchesEnabled ? ACTIVE_BTN : IDLE_BTN} ${DISABLED}`}
        title="NBP — notch-filter auto-notch (RXA)"
      >
        NBP
      </button>
    </div>
  );
}
