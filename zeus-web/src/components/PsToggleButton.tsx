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

import { useCallback } from 'react';
import { setPs } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useTxStore } from '../state/tx-store';

/**
 * PureSignal master arm. Optimistic update with rollback on server refusal —
 * same pattern as MoxButton. Now enabled for both P1 (Hermes Lite) and P2
 * (ANAN G2 / Orion MkII) radios. Protocol1 PureSignal support added with
 * register 0x0a[22] enable bit and predistortion configuration via 0x2b.
 */
export function PsToggleButton() {
  const connected = useConnectionStore((s) => s.status === 'Connected');
  const psEnabled = useTxStore((s) => s.psEnabled);
  const psAuto = useTxStore((s) => s.psAuto);
  const psSingle = useTxStore((s) => s.psSingle);
  const setPsEnabled = useTxStore((s) => s.setPsEnabled);

  const disabled = !connected;
  const tooltip = psEnabled
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
