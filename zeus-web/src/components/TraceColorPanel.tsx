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

import { useDisplaySettingsStore } from '../state/display-settings-store';

const SWATCHES: ReadonlyArray<{ id: string; label: string; color: string }> = [
  { id: 'amber',   label: 'Amber',   color: '#FFA028' },
  { id: 'orange',  label: 'Orange',  color: '#FF7A2E' },
  { id: 'yellow',  label: 'Yellow',  color: '#FFD93A' },
  { id: 'red',     label: 'Red',     color: '#FF4A5B' },
  { id: 'lime',    label: 'Lime',    color: '#A8E63A' },
  { id: 'green',   label: 'Green',   color: '#33D17A' },
  { id: 'mint',    label: 'Mint',    color: '#3AE6C5' },
  { id: 'cyan',    label: 'Cyan',    color: '#5BE5FF' },
  { id: 'sky',     label: 'Sky',     color: '#4A9EFF' },
  { id: 'purple',  label: 'Purple',  color: '#A66BFF' },
  { id: 'magenta', label: 'Magenta', color: '#FF5BC8' },
  { id: 'white',   label: 'White',   color: '#E8ECF4' },
];

export function TraceColorPanel() {
  const rxTraceColor = useDisplaySettingsStore((s) => s.rxTraceColor);
  const setRxTraceColor = useDisplaySettingsStore((s) => s.setRxTraceColor);
  const norm = rxTraceColor.toUpperCase();

  return (
    <section>
      <h3 style={sectionH3}>RX Trace Color</h3>
      <p style={sectionP}>
        Color of the RX panadapter trace and its fill. Affects only the
        spectrum graph — meters, S-meter, and passband overlays keep their
        own colors.
      </p>
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(6, 1fr)', gap: 8 }}>
        {SWATCHES.map((s) => {
          const active = s.color.toUpperCase() === norm;
          return (
            <button
              key={s.id}
              type="button"
              title={s.label}
              aria-label={s.label}
              aria-pressed={active}
              onClick={() => setRxTraceColor(s.color)}
              style={swatchStyle(s.color, active)}
            />
          );
        })}
      </div>
      <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginTop: 14 }}>
        <label
          htmlFor="rx-trace-color-custom"
          style={{
            fontSize: 11,
            fontWeight: 600,
            color: 'var(--fg-1)',
            textTransform: 'uppercase',
            letterSpacing: '0.08em',
          }}
        >
          Custom
        </label>
        <input
          id="rx-trace-color-custom"
          type="color"
          value={norm}
          onChange={(e) => setRxTraceColor(e.target.value.toUpperCase())}
          style={{
            width: 44,
            height: 28,
            padding: 0,
            border: '1px solid var(--line)',
            borderRadius: 'var(--r-xs)',
            background: 'transparent',
            cursor: 'pointer',
          }}
        />
        <span
          style={{
            fontSize: 11,
            color: 'var(--fg-2)',
            fontFamily: 'var(--font-mono, ui-monospace, monospace)',
            letterSpacing: '0.04em',
          }}
        >
          {norm}
        </span>
      </div>
    </section>
  );
}

const sectionH3: React.CSSProperties = {
  margin: '0 0 10px 0',
  fontSize: 11,
  fontWeight: 700,
  letterSpacing: '0.12em',
  textTransform: 'uppercase',
  color: 'var(--fg-0)',
};
const sectionP: React.CSSProperties = {
  margin: '0 0 12px 0',
  fontSize: 12,
  lineHeight: 1.5,
  color: 'var(--fg-2)',
};
function swatchStyle(color: string, active: boolean): React.CSSProperties {
  return {
    width: '100%',
    aspectRatio: '1.6 / 1',
    padding: 0,
    borderRadius: 'var(--r-sm)',
    background: color,
    border: '2px solid',
    borderColor: active ? 'var(--accent)' : 'var(--line)',
    cursor: 'pointer',
    transition: 'border-color var(--dur-fast)',
  };
}
