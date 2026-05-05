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
import { useCapabilitiesStore } from '../state/capabilities-store';

// CFC is WDSP-driven and always available. The VST host submenu is gated
// on the zeus-plughost sidecar (today: Linux only) — when unavailable we
// hide the submenu rather than the whole tab, so CFC stays reachable.
export function TxAudioToolsPanel() {
  const vstHostAvailable = useCapabilitiesStore(
    (s) => s.capabilities?.features.vstHost.available ?? false,
  );
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
      <CfcSettingsPanel />

      {vstHostAvailable && <VstHostSubmenu />}
    </div>
  );
}
