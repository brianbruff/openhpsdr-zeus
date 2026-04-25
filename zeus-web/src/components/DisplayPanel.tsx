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

import { useLayoutPreferenceStore, type LayoutMode } from '../state/layout-preference-store';
import { useLayoutStore } from '../state/layout-store';

export function DisplayPanel() {
  const layoutMode = useLayoutPreferenceStore((s) => s.layoutMode);
  const setLayoutMode = useLayoutPreferenceStore((s) => s.setLayoutMode);
  const resetLayout = useLayoutStore((s) => s.resetLayout);

  const handleLayoutChange = (mode: LayoutMode) => {
    setLayoutMode(mode);
    // Reload the page to apply the new layout mode
    window.location.reload();
  };

  const handleResetFlexLayout = () => {
    if (confirm('Reset the flex layout to its default state? This will discard any panel arrangements you have saved.')) {
      resetLayout();
      window.location.reload();
    }
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 20 }}>
      <section>
        <h3 style={{
          margin: '0 0 10px 0',
          fontSize: 11,
          fontWeight: 700,
          letterSpacing: '0.12em',
          textTransform: 'uppercase',
          color: 'var(--fg-0)',
        }}>
          Layout Mode
        </h3>
        <p style={{
          margin: '0 0 12px 0',
          fontSize: 12,
          lineHeight: 1.5,
          color: 'var(--fg-2)',
        }}>
          Choose between the default fixed layout or the flexible dockable layout.
          Changes require a page reload.
        </p>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
          <label
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: 8,
              padding: '8px 12px',
              borderRadius: 'var(--r-sm)',
              background: layoutMode === 'default' ? 'var(--bg-2)' : 'transparent',
              border: '1px solid',
              borderColor: layoutMode === 'default' ? 'var(--accent)' : 'var(--line)',
              cursor: 'pointer',
              transition: 'all var(--dur-fast)',
            }}
          >
            <input
              type="radio"
              name="layout-mode"
              value="default"
              checked={layoutMode === 'default'}
              onChange={() => handleLayoutChange('default')}
              style={{ cursor: 'pointer' }}
            />
            <div style={{ flex: 1 }}>
              <div style={{ fontSize: 12, fontWeight: 600, color: 'var(--fg-0)' }}>
                Default Layout
              </div>
              <div style={{ fontSize: 11, color: 'var(--fg-2)', marginTop: 2 }}>
                Fixed panel positions, consistent spacing
              </div>
            </div>
          </label>
          <label
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: 8,
              padding: '8px 12px',
              borderRadius: 'var(--r-sm)',
              background: layoutMode === 'flex' ? 'var(--bg-2)' : 'transparent',
              border: '1px solid',
              borderColor: layoutMode === 'flex' ? 'var(--accent)' : 'var(--line)',
              cursor: 'pointer',
              transition: 'all var(--dur-fast)',
            }}
          >
            <input
              type="radio"
              name="layout-mode"
              value="flex"
              checked={layoutMode === 'flex'}
              onChange={() => handleLayoutChange('flex')}
              style={{ cursor: 'pointer' }}
            />
            <div style={{ flex: 1 }}>
              <div style={{ fontSize: 12, fontWeight: 600, color: 'var(--fg-0)' }}>
                Flex Layout
              </div>
              <div style={{ fontSize: 11, color: 'var(--fg-2)', marginTop: 2 }}>
                Dockable panels, customizable arrangement
              </div>
            </div>
          </label>
        </div>
      </section>

      {layoutMode === 'flex' && (
        <section>
          <h3 style={{
            margin: '0 0 10px 0',
            fontSize: 11,
            fontWeight: 700,
            letterSpacing: '0.12em',
            textTransform: 'uppercase',
            color: 'var(--fg-0)',
          }}>
            Flex Layout Options
          </h3>
          <p style={{
            margin: '0 0 12px 0',
            fontSize: 12,
            lineHeight: 1.5,
            color: 'var(--fg-2)',
          }}>
            Reset the flex layout to restore the default panel arrangement.
          </p>
          <button
            type="button"
            className="btn sm"
            onClick={handleResetFlexLayout}
            style={{
              width: 'fit-content',
            }}
          >
            RESET FLEX LAYOUT
          </button>
        </section>
      )}
    </div>
  );
}
