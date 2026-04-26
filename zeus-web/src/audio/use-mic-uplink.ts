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

import { useEffect } from 'react';
import { startMicUplink, type MicUplinkHandle } from './mic-uplink';
import { sendMicPcm } from '../realtime/ws-client';
import { useTxStore } from '../state/tx-store';
import { warnOnce } from '../util/logger';

// Silence floor for the mic meter — below this we clamp so the UI doesn't
// render -∞ and so the bar snaps to fully-empty on a quiet mic.
const MIC_DBFS_FLOOR = -100;

/**
 * Opens the mic AudioWorklet on mount and keeps it running while the app
 * is live. Peak dBFS of every 20 ms block is pushed to tx-store so the
 * MicMeter renders even on RX — the operator needs to know the mic is
 * being picked up *before* keying. Uplink samples are only forwarded to
 * the server when MOX is on; during RX the worklet still runs but the
 * wire path is a no-op.
 *
 * getUserMedia requires a user gesture on first grant, but Chrome remembers
 * the grant per-origin for the session, so the capture starts silently on
 * subsequent page loads once the operator has allowed it once.
 */
export function useMicUplink(): void {
  useEffect(() => {
    let handle: MicUplinkHandle | null = null;
    let disposed = false;

    startMicUplink((samples, peak) => {
      // Level: always pushed so MicMeter animates on RX.
      const dbfs = peak > 0
        ? Math.max(MIC_DBFS_FLOOR, 20 * Math.log10(peak))
        : MIC_DBFS_FLOOR;
      useTxStore.getState().setMicDbfs(dbfs);

      // Samples: only forwarded to the server while keyed. Capturing always +
      // gating here avoids a ~300 ms getUserMedia cold-start on every MOX.
      if (useTxStore.getState().moxOn) sendMicPcm(samples);
    })
      .then((h) => {
        if (disposed) { void h.stop(); return; }
        handle = h;
        useTxStore.getState().setMicError(null);
      })
      .catch((err: unknown) => {
        const msg = err instanceof Error ? err.message : String(err);
        warnOnce('mic-uplink-failed', `mic capture unavailable: ${msg}`);
        useTxStore.getState().setMicError(msg);
      });

    return () => {
      disposed = true;
      const h = handle;
      handle = null;
      if (h) void h.stop();
    };
  }, []);
}
