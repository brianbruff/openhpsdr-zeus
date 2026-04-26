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
// Wüllen (DL1YCF), with contributions from (DH1KLM); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

import { useCallback } from 'react';
import { setPs } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useTxStore } from '../state/tx-store';

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
  const setPsEnabled = useTxStore((s) => s.setPsEnabled);

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
  }, [disabled, psEnabled, psAuto, psSingle, setPsEnabled]);

  return (
    <button
      type="button"
      disabled={disabled}
      onClick={click}
      className={`btn lg tx-btn ${psEnabled ? 'tx' : ''}`}
      title={tooltip}
    >
      <span className={`led ${psEnabled ? 'tx' : ''}`} style={{ marginRight: 8 }} />
      PS
    </button>
  );
}
