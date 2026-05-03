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
// Custom plugin search paths. Operator adds folders that the rescan
// pass walks in addition to the per-OS defaults (~/.vst3, /Library/...,
// %COMMONPROGRAMFILES%\VST3 etc.). Backend validates path existence on
// POST and returns 400 with `error` for invalid input — surfaced inline.

import { useState } from 'react';

import { useVstHostStore } from '../state/vst-host-store';

export function VstHostSearchPaths() {
  const paths = useVstHostStore((s) => s.master.customSearchPaths);
  const addSearchPath = useVstHostStore((s) => s.addSearchPath);
  const removeSearchPath = useVstHostStore((s) => s.removeSearchPath);
  const catalogError = useVstHostStore((s) => s.catalogError);

  const [draft, setDraft] = useState('');
  const trimmed = draft.trim();

  const onAdd = async () => {
    if (trimmed.length === 0) return;
    await addSearchPath(trimmed);
    setDraft('');
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      <h4
        style={{
          margin: 0,
          fontSize: 10,
          fontWeight: 700,
          letterSpacing: '0.12em',
          textTransform: 'uppercase',
          color: 'var(--fg-2)',
        }}
      >
        Custom search paths
      </h4>

      {paths.length === 0 ? (
        <div style={{ fontSize: 11, color: 'var(--fg-3)' }}>
          None — only the OS-standard paths are scanned.
        </div>
      ) : (
        <ul
          style={{
            listStyle: 'none',
            margin: 0,
            padding: 0,
            display: 'flex',
            flexDirection: 'column',
            gap: 2,
          }}
        >
          {paths.map((p) => (
            <li
              key={p}
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: 6,
                fontSize: 11,
              }}
            >
              <span
                title={p}
                style={{
                  flex: 1,
                  minWidth: 0,
                  color: 'var(--fg-1)',
                  overflow: 'hidden',
                  textOverflow: 'ellipsis',
                  whiteSpace: 'nowrap',
                }}
              >
                {p}
              </span>
              <button
                type="button"
                className="btn sm"
                onClick={() => void removeSearchPath(p)}
                aria-label={`Remove search path ${p}`}
              >
                REMOVE
              </button>
            </li>
          ))}
        </ul>
      )}

      <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
        <input
          type="text"
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          placeholder="/path/to/plugins"
          style={{
            flex: 1,
            padding: '4px 6px',
            background: 'var(--bg-0)',
            color: 'var(--fg-1)',
            border: '1px solid var(--panel-border)',
            borderRadius: 3,
            fontSize: 12,
          }}
        />
        <button
          type="button"
          className="btn sm"
          disabled={trimmed.length === 0}
          onClick={() => void onAdd()}
        >
          ADD
        </button>
      </div>

      {catalogError ? (
        <div style={{ fontSize: 11, color: 'var(--tx)' }}>{catalogError}</div>
      ) : null}
    </div>
  );
}
