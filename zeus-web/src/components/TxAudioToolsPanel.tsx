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

import { CfcSettingsPanel } from './CfcSettingsPanel';

/**
 * "TX Audio Tools" settings tab — host for digital-domain TX shaping
 * controls. Issue #123 lands the 10-band CFC; TX EQ has a placeholder
 * section that will fill in once the VST host integration (issue #106)
 * settles. Keeping EQ visible-but-disabled gives operators a clear sense
 * of the roadmap without committing scope before the architecture decision.
 */
export function TxAudioToolsPanel() {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
      <CfcSettingsPanel />

      {/* TX EQ placeholder — deferred per issue #123 / #106. Greyed-out so
          the tab's intent is visible but no controls invite a change yet. */}
      <section
        style={{
          border: '1px dashed var(--panel-border)',
          borderRadius: 6,
          padding: '12px 14px',
          background: 'var(--bg-1)',
          opacity: 0.55,
        }}
      >
        <h3
          style={{
            margin: 0,
            marginBottom: 6,
            fontSize: 11,
            fontWeight: 700,
            letterSpacing: '0.12em',
            textTransform: 'uppercase',
            color: 'var(--fg-2)',
          }}
        >
          TX EQ
        </h3>
        <p style={{ margin: 0, fontSize: 11, color: 'var(--fg-2)' }}>
          Coming with VST host integration (issue #106). The in-process EQ
          stage is paused until the out-of-process sidecar architecture is
          confirmed — see project notes.
        </p>
      </section>
    </div>
  );
}
