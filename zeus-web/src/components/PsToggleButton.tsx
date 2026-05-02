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
import { setPs, setPsMonitor } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useRadioStore } from '../state/radio-store';
import { useTxStore } from '../state/tx-store';

// Connected board kinds that don't have a real PS feedback receiver. PS
// Monitor (post-PA loopback display source) is only meaningful where the
// board has a feedback path, so we don't auto-enable it for these.
const PS_MONITOR_UNSUPPORTED = new Set(['HermesLite2']);

/**
 * PureSignal master arm. Optimistic update with rollback on server refusal —
 * same pattern as MoxButton. Disabled until a P2 radio is connected:
 * Protocol1 PureSignal is deferred to a follow-up because we can only
 * rack-test against a G2 / OrionMkII today. TODO(ps-p1): drop the gate
 * once Protocol1Client gains the SetPuresignal hooks.
 */
export function PsToggleButton() {
  const connected = useConnectionStore((s) => s.status === 'Connected');
  const protocol = useConnectionStore((s) => s.connectedProtocol);
  const psEnabled = useTxStore((s) => s.psEnabled);
  const psAuto = useTxStore((s) => s.psAuto);
  const psSingle = useTxStore((s) => s.psSingle);
  const psMonitorEnabled = useTxStore((s) => s.psMonitorEnabled);
  const setPsEnabled = useTxStore((s) => s.setPsEnabled);
  const setPsMonitorLocal = useTxStore((s) => s.setPsMonitorEnabled);
  const connectedBoard = useRadioStore((s) => s.selection.connected);

  // P1 gate — backend forwards SetPsEnabled to the engine on either protocol
  // but the wire-side feedback path is P2-only in v1.
  const p1Disabled = protocol === 'P1';
  const disabled = !connected || p1Disabled;
  const tooltip = p1Disabled
    ? 'PureSignal for Hermes coming in a follow-up'
    : psEnabled
      ? 'PureSignal armed — predistortion active'
      : 'Arm PureSignal predistortion';

  const click = useCallback(() => {
    if (disabled) return;
    const next = !psEnabled;
    setPsEnabled(next);
    setPs({ enabled: next, auto: psAuto, single: psSingle }).catch(() => {
      setPsEnabled(!next);
    });
    // When arming PS, also turn on PS Monitor by default — operators almost
    // always want to see the post-PA loopback while PS is correcting, and
    // having it default off forced an extra trip to Settings every session.
    // Only auto-toggles up; disarming PS doesn't force the monitor off so
    // the operator can keep watching the trace if they had it on
    // pre-arming. Skip on boards without a real feedback receiver (HL2).
    if (
      next
      && !psMonitorEnabled
      && !PS_MONITOR_UNSUPPORTED.has(connectedBoard)
    ) {
      setPsMonitorLocal(true);
      setPsMonitor(true).catch(() => setPsMonitorLocal(false));
    }
  }, [
    disabled,
    psEnabled,
    psAuto,
    psSingle,
    psMonitorEnabled,
    connectedBoard,
    setPsEnabled,
    setPsMonitorLocal,
  ]);

  return (
    <button
      type="button"
      disabled={disabled}
      onClick={click}
      className={`btn tx-btn ${psEnabled ? 'tx' : ''}`}
      title={tooltip}
    >
      <span className={`led ${psEnabled ? 'tx' : ''}`} style={{ marginRight: 8 }} />
      PS
    </button>
  );
}
