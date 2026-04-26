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

import { useEffect } from 'react';
import { useTciStore } from '../state/tci-store';

// Clicking the badge opens TCI settings via the hash-deeplink that App.tsx handles.
function openTciSettings() {
  window.location.hash = 'tci';
}

export function TciStatusBadge() {
  const settings = useTciStore((s) => s.settings);
  const load = useTciStore((s) => s.load);

  useEffect(() => {
    void load();
  }, [load]);

  if (!settings) return null;

  const { portBound, portError, enabled, restartRequired } = settings;

  // Only show the badge when TCI is enabled or has an error.
  if (!enabled && !portError) return null;

  let badgeStyle: React.CSSProperties;
  let label: string;

  if (portError) {
    badgeStyle = {
      background: 'rgba(230,58,43,0.15)',
      color: 'var(--tx)',
      border: '1px solid rgba(230,58,43,0.5)',
    };
    label = '📡 TCI ERR';
  } else if (portBound) {
    badgeStyle = {
      background: 'rgba(74,158,255,0.15)',
      color: 'var(--accent)',
      border: '1px solid rgba(74,158,255,0.4)',
    };
    label = '📡 TCI';
  } else if (restartRequired) {
    badgeStyle = {
      background: 'rgba(255,201,58,0.1)',
      color: 'var(--power)',
      border: '1px solid rgba(255,201,58,0.4)',
    };
    label = '📡 TCI ↺';
  } else {
    return null;
  }

  return (
    <button
      type="button"
      title={portError ?? (restartRequired ? 'TCI enabled — restart to activate' : 'TCI running')}
      onClick={openTciSettings}
      style={{
        ...badgeStyle,
        borderRadius: 4,
        padding: '1px 7px',
        fontSize: 11,
        fontWeight: 600,
        letterSpacing: '0.04em',
        cursor: 'pointer',
        lineHeight: '18px',
        whiteSpace: 'nowrap',
      }}
    >
      {label}
    </button>
  );
}
