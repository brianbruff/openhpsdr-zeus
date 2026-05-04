// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// LeftLayoutBar — vertical bar listing the current radio's named layouts.
// Click to switch active, "+" creates a new one (seeded from the default
// workspace), "✕" deletes the focused layout, "⟳" resets the active layout
// to its default tile arrangement.
//
// Issue #241: visual chrome reuses tokens.css; no new colors are introduced.

import { useState } from 'react';
import { useLayoutStore } from '../state/layout-store';

export function LeftLayoutBar() {
  const layouts = useLayoutStore((s) => s.layouts);
  const activeLayoutId = useLayoutStore((s) => s.activeLayoutId);
  const setActiveLayout = useLayoutStore((s) => s.setActiveLayout);
  const addLayout = useLayoutStore((s) => s.addLayout);
  const removeLayout = useLayoutStore((s) => s.removeLayout);
  const renameLayout = useLayoutStore((s) => s.renameLayout);
  const resetActiveLayout = useLayoutStore((s) => s.resetActiveLayout);
  const isLoaded = useLayoutStore((s) => s.isLoaded);

  const [renamingId, setRenamingId] = useState<string | null>(null);
  const [renameText, setRenameText] = useState('');

  const handleAdd = () => {
    const proposed = `Layout ${layouts.length + 1}`;
    const name = window.prompt('Name for the new layout', proposed)?.trim();
    if (!name) return;
    addLayout(name);
  };

  const handleDelete = (id: string, name: string) => {
    if (layouts.length <= 1) return;
    if (!window.confirm(`Delete layout “${name}”? Its panel arrangement will be lost.`)) return;
    removeLayout(id);
  };

  const handleReset = () => {
    const active = layouts.find((l) => l.id === activeLayoutId);
    if (!active) return;
    if (!window.confirm(`Reset “${active.name}” to the default panel arrangement?`)) return;
    resetActiveLayout();
  };

  const startRename = (id: string, currentName: string) => {
    setRenamingId(id);
    setRenameText(currentName);
  };

  const commitRename = () => {
    if (!renamingId) return;
    const name = renameText.trim();
    if (name) renameLayout(renamingId, name);
    setRenamingId(null);
    setRenameText('');
  };

  return (
    <aside className="left-layout-bar" aria-label="Layouts">
      <div className="lb-list" role="tablist" aria-orientation="vertical">
        {!isLoaded ? (
          <div className="lb-empty" aria-hidden>…</div>
        ) : (
          layouts.map((l) => {
            const active = l.id === activeLayoutId;
            const renaming = renamingId === l.id;
            return (
              <div key={l.id} className={`lb-item ${active ? 'active' : ''}`}>
                {renaming ? (
                  <input
                    autoFocus
                    className="lb-rename"
                    value={renameText}
                    onChange={(e) => setRenameText(e.target.value)}
                    onBlur={commitRename}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter') commitRename();
                      else if (e.key === 'Escape') {
                        setRenamingId(null);
                        setRenameText('');
                      }
                    }}
                  />
                ) : (
                  <button
                    type="button"
                    className="lb-tab"
                    role="tab"
                    aria-selected={active}
                    onClick={() => setActiveLayout(l.id)}
                    onDoubleClick={() => startRename(l.id, l.name)}
                    title={`${l.name} (double-click to rename)`}
                  >
                    <span className="lb-tab-name">{l.name}</span>
                  </button>
                )}
                {active && layouts.length > 1 && !renaming && (
                  <button
                    type="button"
                    className="lb-x"
                    onClick={() => handleDelete(l.id, l.name)}
                    title={`Delete ${l.name}`}
                    aria-label={`Delete ${l.name}`}
                  >
                    ✕
                  </button>
                )}
              </div>
            );
          })
        )}
      </div>

      <div className="lb-actions">
        <button
          type="button"
          className="btn sm lb-action"
          onClick={handleAdd}
          title="Add a new layout"
          aria-label="Add a new layout"
        >
          +
        </button>
        <button
          type="button"
          className="btn ghost sm lb-action"
          onClick={handleReset}
          title="Reset active layout to default"
          aria-label="Reset active layout to default"
          disabled={!isLoaded || layouts.length === 0}
        >
          ⟳
        </button>
      </div>
    </aside>
  );
}
