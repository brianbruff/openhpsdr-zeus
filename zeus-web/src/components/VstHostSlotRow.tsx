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
// One row of the VST host chain. Empty slot shows a "Load" button; a
// loaded slot shows the plugin name + Bypass / Edit / Unload controls and
// a caret to reveal the parameter list. The native plugin editor opens as
// a real OS window — Zeus only brokers show/hide.

import { useState } from 'react';

import { useVstHostStore } from '../state/vst-host-store';
import { VstHostSlotParameters } from './VstHostSlotParameters';

type Props = {
  index: number;
  // Disabled when the master toggle is off — controls become read-only
  // hints. Load and parameter edits would just fail at the seam anyway.
  disabled: boolean;
  // Opens the plugin browser scoped to "load into this slot N".
  onRequestLoad: (index: number) => void;
};

export function VstHostSlotRow({ index, disabled, onRequestLoad }: Props) {
  const slot = useVstHostStore((s) => s.master.slots[index]);
  const editor = useVstHostStore((s) => s.editors.get(index));
  const error = useVstHostStore((s) => s.slotErrors.get(index) ?? null);
  const setSlotBypass = useVstHostStore((s) => s.setSlotBypass);
  const unloadSlot = useVstHostStore((s) => s.unloadSlot);
  const showEditor = useVstHostStore((s) => s.showEditor);
  const hideEditor = useVstHostStore((s) => s.hideEditor);
  const clearSlotError = useVstHostStore((s) => s.clearSlotError);

  const [expanded, setExpanded] = useState(false);

  // The slots array is always length 8 after parseVstHostState pads it,
  // but TS can't see that — guard for the noUncheckedIndexedAccess case.
  if (!slot) return null;

  const loaded = slot.plugin !== null;
  const editorOpen = editor?.open === true;
  // VST3, VST2 (.so), and CLAP (.clap) editors all flow through the same
  // sidecar IPlugView path; show EDIT for any loaded slot. The sidecar
  // returns "no editor" status if the plugin doesn't expose one, which
  // surfaces in the editor error field rather than as a popup.
  const path = slot.plugin?.path ?? '';
  const lowerPath = path.toLowerCase();
  const hasNativeEditor =
    lowerPath.endsWith('.vst3') ||
    lowerPath.endsWith('.so') ||
    lowerPath.endsWith('.clap');

  const rowStyle: React.CSSProperties = {
    display: 'flex',
    flexDirection: 'column',
    gap: 6,
    padding: '8px 10px',
    border: '1px solid var(--panel-border)',
    borderRadius: 4,
    background: 'var(--bg-1)',
    opacity: disabled ? 0.55 : 1,
  };

  return (
    <div style={rowStyle} aria-label={`VST slot ${index + 1}`}>
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 10,
          fontSize: 12,
        }}
      >
        <span
          style={{
            minWidth: 26,
            textAlign: 'center',
            color: 'var(--fg-2)',
            fontWeight: 700,
            fontVariantNumeric: 'tabular-nums',
          }}
          aria-label="Slot number"
        >
          {index + 1}
        </span>

        {loaded && slot.plugin ? (
          <button
            type="button"
            aria-label={expanded ? 'Collapse parameters' : 'Expand parameters'}
            onClick={() => setExpanded((v) => !v)}
            disabled={disabled || slot.parameterCount === 0}
            style={{
              width: 20,
              height: 20,
              padding: 0,
              color:
                slot.parameterCount === 0 ? 'var(--fg-3)' : 'var(--fg-2)',
              cursor:
                slot.parameterCount === 0 || disabled ? 'default' : 'pointer',
              fontSize: 11,
              lineHeight: 1,
            }}
            title={
              slot.parameterCount === 0
                ? 'No parameters exposed'
                : expanded
                  ? 'Collapse parameters'
                  : 'Expand parameters'
            }
          >
            {expanded ? 'v' : '>'}
          </button>
        ) : (
          <span style={{ width: 20 }} />
        )}

        <div
          style={{
            flex: 1,
            minWidth: 0,
            display: 'flex',
            flexDirection: 'column',
          }}
        >
          {loaded && slot.plugin ? (
            <>
              <span
                style={{
                  color: 'var(--fg-0)',
                  overflow: 'hidden',
                  textOverflow: 'ellipsis',
                  whiteSpace: 'nowrap',
                }}
                title={slot.plugin.path || slot.plugin.name}
              >
                {slot.plugin.name}
              </span>
              <span
                style={{
                  fontSize: 10,
                  color: 'var(--fg-3)',
                  letterSpacing: '0.04em',
                }}
              >
                {slot.plugin.vendor || '—'}
                {slot.plugin.version ? ` · ${slot.plugin.version}` : ''}
              </span>
            </>
          ) : (
            <span style={{ color: 'var(--fg-3)', fontStyle: 'italic' }}>
              Empty
            </span>
          )}
        </div>

        {loaded ? (
          <>
            <label
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: 4,
                color: 'var(--fg-2)',
                fontSize: 11,
              }}
            >
              <input
                type="checkbox"
                checked={slot.bypass}
                disabled={disabled}
                onChange={(e) => void setSlotBypass(index, e.target.checked)}
              />
              Bypass
            </label>
            {hasNativeEditor ? (
              <button
                type="button"
                className="btn sm"
                disabled={disabled}
                onClick={() =>
                  editorOpen ? void hideEditor(index) : void showEditor(index)
                }
                title={
                  editorOpen
                    ? `Editor open${
                        editor && editor.width > 0
                          ? ` (${editor.width}x${editor.height})`
                          : ''
                      } — click to close`
                    : 'Open the plugin’s native editor window'
                }
              >
                {editorOpen ? 'CLOSE' : 'EDIT'}
              </button>
            ) : null}
            <button
              type="button"
              className="btn sm"
              disabled={disabled}
              onClick={() => void unloadSlot(index)}
            >
              UNLOAD
            </button>
          </>
        ) : (
          <button
            type="button"
            className="btn sm"
            disabled={disabled}
            onClick={() => onRequestLoad(index)}
          >
            LOAD
          </button>
        )}
      </div>

      {editorOpen && editor && editor.width > 0 ? (
        <div style={{ fontSize: 10, color: 'var(--fg-3)' }}>
          Editor open ({editor.width}x{editor.height}) — native window on
          your desktop.
        </div>
      ) : null}

      {error ? (
        <div
          style={{
            fontSize: 11,
            color: 'var(--tx)',
            display: 'flex',
            alignItems: 'center',
            gap: 6,
          }}
        >
          <span>{error}</span>
          <button
            type="button"
            onClick={() => clearSlotError(index)}
            style={{
              fontSize: 10,
              color: 'var(--fg-2)',
              padding: '0 4px',
            }}
            aria-label="Dismiss error"
          >
            ×
          </button>
        </div>
      ) : null}

      {expanded && loaded ? (
        <div
          style={{
            marginTop: 4,
            padding: '6px 4px',
            borderTop: '1px solid var(--panel-border)',
          }}
        >
          <VstHostSlotParameters slotIndex={index} />
        </div>
      ) : null}
    </div>
  );
}
