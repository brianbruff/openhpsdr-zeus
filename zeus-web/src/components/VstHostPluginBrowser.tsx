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
// VST host plugin browser. Lists the catalog and lets the operator load a
// plugin into a target slot (or browse in preview mode with no target).
// Custom search paths are managed inline so the operator can rescan and
// see the result without leaving the panel.

import { useEffect, useMemo, useState } from 'react';

import { useVstHostStore } from '../state/vst-host-store';
import type { VstHostCatalogEntry } from '../api/vst-host';
import { VstHostSearchPaths } from './VstHostSearchPaths';

type Props = {
  open: boolean;
  // null = preview mode (no slot pre-selected). Otherwise the catalog
  // shows a "LOAD INTO SLOT N" action on each row.
  targetSlot: number | null;
  onClose: () => void;
};

export function VstHostPluginBrowser({ open, targetSlot, onClose }: Props) {
  const catalog = useVstHostStore((s) => s.catalog);
  const catalogLoaded = useVstHostStore((s) => s.catalogLoaded);
  const catalogInflight = useVstHostStore((s) => s.catalogInflight);
  const catalogError = useVstHostStore((s) => s.catalogError);
  const refreshCatalog = useVstHostStore((s) => s.refreshCatalog);
  const loadSlot = useVstHostStore((s) => s.loadSlot);

  const [search, setSearch] = useState('');

  // Auto-load the catalog on first open. Subsequent opens keep the cached
  // result so flipping the panel doesn't redo a 5-second scan; operator
  // hits "RESCAN" explicitly to refresh.
  useEffect(() => {
    if (open && !catalogLoaded && !catalogInflight) {
      void refreshCatalog(false);
    }
  }, [open, catalogLoaded, catalogInflight, refreshCatalog]);

  const filtered = useMemo<VstHostCatalogEntry[]>(() => {
    const q = search.trim().toLowerCase();
    if (q.length === 0) return catalog;
    return catalog.filter(
      (p) =>
        p.displayName.toLowerCase().includes(q) ||
        p.format.toLowerCase().includes(q),
    );
  }, [catalog, search]);

  if (!open) return null;

  return (
    <div
      role="dialog"
      aria-label="VST plugin browser"
      style={{
        position: 'absolute',
        inset: 0,
        background: 'var(--bg-1)',
        display: 'flex',
        flexDirection: 'column',
        zIndex: 5,
        border: '1px solid var(--panel-border)',
        borderRadius: 4,
      }}
    >
      <header
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 10,
          padding: '8px 12px',
          background:
            'linear-gradient(180deg, var(--panel-head-top), var(--panel-head-bot))',
          borderBottom: '1px solid var(--panel-border)',
          color: 'var(--fg-0)',
        }}
      >
        <span
          style={{
            fontSize: 11,
            fontWeight: 700,
            letterSpacing: '0.12em',
            textTransform: 'uppercase',
          }}
        >
          Plugin browser
          {targetSlot !== null ? ` — load into slot ${targetSlot + 1}` : ''}
        </span>
        <span style={{ flex: 1 }} />
        <button
          type="button"
          className="btn sm"
          onClick={() => void refreshCatalog(true)}
          disabled={catalogInflight}
        >
          {catalogInflight ? 'SCANNING…' : 'RESCAN'}
        </button>
        <button
          type="button"
          className="btn sm"
          onClick={onClose}
          aria-label="Close plugin browser"
        >
          CLOSE
        </button>
      </header>

      <div style={{ padding: '8px 12px', borderBottom: '1px solid var(--panel-border)' }}>
        <input
          type="search"
          placeholder="Filter by name or format…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          style={{
            width: '100%',
            padding: '4px 8px',
            background: 'var(--bg-0)',
            color: 'var(--fg-1)',
            border: '1px solid var(--panel-border)',
            borderRadius: 3,
            fontSize: 12,
          }}
        />
      </div>

      <div
        style={{
          flex: 1,
          minHeight: 0,
          overflow: 'auto',
          padding: '4px 0',
        }}
      >
        {catalogError ? (
          <div style={{ padding: 12, color: 'var(--tx)', fontSize: 11 }}>
            {catalogError}
          </div>
        ) : null}
        {!catalogLoaded && catalogInflight ? (
          <div style={{ padding: 12, color: 'var(--fg-2)', fontSize: 11 }}>
            Scanning plugin paths…
          </div>
        ) : null}
        {catalogLoaded && filtered.length === 0 && search.trim().length === 0 ? (
          <div style={{ padding: 12, color: 'var(--fg-2)', fontSize: 11 }}>
            No plugins found in standard paths. Add a folder below and
            click RESCAN.
          </div>
        ) : null}
        {catalogLoaded && filtered.length === 0 && search.trim().length > 0 ? (
          <div style={{ padding: 12, color: 'var(--fg-2)', fontSize: 11 }}>
            No matches for “{search}”.
          </div>
        ) : null}

        <ul
          style={{
            listStyle: 'none',
            margin: 0,
            padding: 0,
            display: 'flex',
            flexDirection: 'column',
          }}
        >
          {filtered.map((entry) => (
            <li
              key={entry.filePath}
              style={{
                display: 'grid',
                gridTemplateColumns: '1fr auto',
                gap: 8,
                padding: '6px 12px',
                borderBottom: '1px solid var(--panel-border)',
                alignItems: 'center',
                fontSize: 11,
              }}
            >
              <div style={{ minWidth: 0 }}>
                <div
                  style={{
                    color: 'var(--fg-0)',
                    overflow: 'hidden',
                    textOverflow: 'ellipsis',
                    whiteSpace: 'nowrap',
                  }}
                >
                  {entry.displayName || entry.filePath}
                </div>
                <div
                  title={entry.filePath}
                  style={{
                    color: 'var(--fg-3)',
                    fontSize: 10,
                    overflow: 'hidden',
                    textOverflow: 'ellipsis',
                    whiteSpace: 'nowrap',
                  }}
                >
                  {entry.format} · {entry.platform} · {entry.bitness} ·{' '}
                  {entry.filePath}
                </div>
              </div>
              {targetSlot !== null ? (
                <button
                  type="button"
                  className="btn sm"
                  onClick={async () => {
                    // VST3: send the .vst3 bundle directory (the SDK
                    // Module::create expects the bundle path, not the
                    // inner .so). VST2 / CLAP: send the file path
                    // directly — the sidecar's PluginChain dispatches
                    // by extension at LoadSlot time.
                    const path = entry.format === 'Vst3'
                      ? (entry.bundlePath ?? entry.filePath)
                      : entry.filePath;
                    await loadSlot(targetSlot, path);
                    onClose();
                  }}
                >
                  LOAD INTO SLOT {targetSlot + 1}
                </button>
              ) : null}
            </li>
          ))}
        </ul>
      </div>

      <footer
        style={{
          padding: '8px 12px',
          borderTop: '1px solid var(--panel-border)',
          background: 'var(--bg-0)',
        }}
      >
        <VstHostSearchPaths />
      </footer>
    </div>
  );
}
