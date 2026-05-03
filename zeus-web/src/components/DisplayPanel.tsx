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

import { useLayoutPreferenceStore, type LayoutMode } from '../state/layout-preference-store';
import { useLayoutStore } from '../state/layout-store';
import { BackgroundSettingsPanel } from './BackgroundSettingsPanel';
import { TraceColorPanel } from './TraceColorPanel';

type LayoutOption = {
  id: LayoutMode;
  label: string;
  help: string;
  icon: React.ReactNode;
};

const iconSvg: React.CSSProperties = {
  width: 13,
  height: 13,
  stroke: 'currentColor',
  fill: 'none',
  strokeWidth: 1.6,
  strokeLinecap: 'round',
  strokeLinejoin: 'round',
};

const LAYOUT_OPTIONS: ReadonlyArray<LayoutOption> = [
  {
    id: 'default',
    label: 'Default Layout',
    help: 'Fixed panel positions, consistent spacing.',
    icon: (
      <svg viewBox="0 0 16 16" style={iconSvg}>
        <rect x="2" y="2" width="12" height="12" rx="1.5" />
        <path d="M2 6h12M6 6v8" />
      </svg>
    ),
  },
  {
    id: 'flex',
    label: 'Flex Layout',
    help: 'Dockable panels, customizable arrangement.',
    icon: (
      <svg viewBox="0 0 16 16" style={iconSvg}>
        <rect x="2" y="2" width="5" height="5" rx="1" />
        <rect x="9" y="2" width="5" height="5" rx="1" />
        <rect x="2" y="9" width="5" height="5" rx="1" />
        <rect x="9" y="9" width="5" height="5" rx="1" />
      </svg>
    ),
  },
];

export function DisplayPanel() {
  const layoutMode = useLayoutPreferenceStore((s) => s.layoutMode);
  const setLayoutMode = useLayoutPreferenceStore((s) => s.setLayoutMode);
  const resetLayout = useLayoutStore((s) => s.resetLayout);

  const handleLayoutChange = (mode: LayoutMode) => {
    if (mode === layoutMode) return;
    setLayoutMode(mode);
    window.location.reload();
  };

  const handleResetFlexLayout = () => {
    if (confirm('Reset the flex layout to its default state? This will discard any panel arrangements you have saved.')) {
      resetLayout();
      window.location.reload();
    }
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
      <BackgroundSettingsPanel />

      <TraceColorPanel />

      <section>
        <div style={sectionHead}>
          <h3 style={sectionH3}>Layout Mode</h3>
          <p style={sectionP}>Changes require a page reload.</p>
        </div>

        <div role="radiogroup" aria-label="Layout mode" style={segmentedGrid(2)}>
          {LAYOUT_OPTIONS.map((opt) => {
            const active = layoutMode === opt.id;
            return (
              <button
                key={opt.id}
                type="button"
                role="radio"
                aria-checked={active}
                onClick={() => handleLayoutChange(opt.id)}
                style={segCardStyle(active, true)}
              >
                <span style={checkDotStyle(active)} aria-hidden />
                <div style={segTopRow}>
                  <span style={segIconBoxStyle(active)} aria-hidden>{opt.icon}</span>
                  <span style={segTitle}>{opt.label}</span>
                </div>
                <span style={segHelp}>{opt.help}</span>
              </button>
            );
          })}
        </div>

        {layoutMode === 'flex' && (
          <div style={{ marginTop: 12 }}>
            <button
              type="button"
              className="btn sm"
              onClick={handleResetFlexLayout}
            >
              RESET FLEX LAYOUT
            </button>
          </div>
        )}
      </section>
    </div>
  );
}

const sectionHead: React.CSSProperties = {
  display: 'flex',
  alignItems: 'baseline',
  flexWrap: 'wrap',
  gap: 10,
  marginBottom: 10,
};
const sectionH3: React.CSSProperties = {
  margin: 0,
  fontSize: 11,
  fontWeight: 700,
  letterSpacing: '0.18em',
  textTransform: 'uppercase',
  color: 'var(--fg-0)',
};
const sectionP: React.CSSProperties = {
  margin: 0,
  fontSize: 12,
  lineHeight: 1.5,
  color: 'var(--fg-2)',
};

function segmentedGrid(cols: number): React.CSSProperties {
  return {
    display: 'grid',
    gridTemplateColumns: `repeat(${cols}, 1fr)`,
    gap: 8,
  };
}

function segCardStyle(active: boolean, compact: boolean): React.CSSProperties {
  return {
    position: 'relative',
    display: 'flex',
    flexDirection: 'column',
    gap: 6,
    minHeight: compact ? undefined : 76,
    padding: compact ? '10px 12px' : '12px 12px 11px',
    textAlign: 'left',
    border: '1px solid',
    borderColor: active ? 'var(--accent)' : 'var(--line)',
    background: active ? 'var(--accent-soft)' : 'var(--bg-1)',
    boxShadow: active ? 'inset 0 0 0 1px var(--accent)' : 'none',
    borderRadius: 'var(--r-md)',
    cursor: 'pointer',
    color: 'var(--fg-1)',
    transition: 'background var(--dur-fast), border-color var(--dur-fast)',
  };
}

function segIconBoxStyle(active: boolean): React.CSSProperties {
  return {
    width: 22,
    height: 22,
    display: 'inline-grid',
    placeItems: 'center',
    borderRadius: 'var(--r-sm)',
    background: active ? 'var(--accent)' : 'var(--bg-3)',
    border: active ? '1px solid var(--accent)' : '1px solid var(--line)',
    color: active ? '#0b1220' : 'var(--fg-1)',
    flexShrink: 0,
  };
}

function checkDotStyle(active: boolean): React.CSSProperties {
  return {
    position: 'absolute',
    top: 9,
    right: 9,
    width: 14,
    height: 14,
    borderRadius: '50%',
    border: `1.5px solid ${active ? 'var(--accent)' : 'var(--line)'}`,
    background: active
      ? 'radial-gradient(circle at center, var(--accent) 0 4px, transparent 4.5px)'
      : 'transparent',
    transition: 'border-color var(--dur-fast), background var(--dur-fast)',
  };
}

const segTopRow: React.CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: 8,
};
const segTitle: React.CSSProperties = {
  fontSize: 13,
  fontWeight: 700,
  color: 'var(--fg-0)',
  letterSpacing: '0.02em',
};
const segHelp: React.CSSProperties = {
  fontSize: 11.5,
  color: 'var(--fg-2)',
  lineHeight: 1.45,
};
