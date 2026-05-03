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
import { VstHostSubmenu } from './VstHostSubmenu';

/**
 * "TX Audio Tools" settings tab — host for digital-domain TX shaping
 * controls. Issue #123 lands the 10-band CFC; issue #106 lands the VST
 * host submenu (out-of-process sidecar with an 8-slot chain). The
 * legacy in-process TX EQ remains paused — operators with EQ needs go
 * through a VST instead.
 */
export function TxAudioToolsPanel() {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
      <CfcSettingsPanel />

      <VstHostSubmenu />
    </div>
  );
}
