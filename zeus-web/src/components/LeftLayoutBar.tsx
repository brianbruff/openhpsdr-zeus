// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// LeftLayoutBar — vertical bar listing the current radio's named layouts.
// Each item shows a large emoji icon with a small label beneath. Clicking
// switches to that layout; the gear opens LayoutSettingsModal to edit the
// label, icon, and tooltip description. The "+" button opens the same modal
// in create mode; "✕" deletes the active layout; "⟳" resets it to the
// default tile arrangement.
//
// Issue #241: visual chrome reuses tokens.css; no new colors are introduced.

import { useMemo, useState, type CSSProperties } from 'react';
import { useLayoutStore } from '../state/layout-store';
import { useDisplaySettingsStore } from '../state/display-settings-store';
import {
  LayoutSettingsModal,
  type LayoutSettingsValue,
} from '../layout/LayoutSettingsModal';

type ModalState =
  | { kind: 'closed' }
  | { kind: 'create' }
  | { kind: 'edit'; id: string };

export function LeftLayoutBar() {
  const layouts = useLayoutStore((s) => s.layouts);
  const activeLayoutId = useLayoutStore((s) => s.activeLayoutId);
  const setActiveLayout = useLayoutStore((s) => s.setActiveLayout);
  const addLayout = useLayoutStore((s) => s.addLayout);
  const removeLayout = useLayoutStore((s) => s.removeLayout);
  const updateLayoutMeta = useLayoutStore((s) => s.updateLayoutMeta);
  const resetActiveLayout = useLayoutStore((s) => s.resetActiveLayout);
  const isLoaded = useLayoutStore((s) => s.isLoaded);
  // The bar's blue gradient + dot wash follows the operator's panadapter
  // trace colour from the Display tab. CLAUDE.md flags trace amber as
  // "panadapter-only", but the maintainer explicitly asked for the chrome
  // to track the trace selection — so the bar tints to whatever hue the
  // operator chose. Wash uses a 0.25× darkened variant so the original
  // top-half-fades-out gradient style is preserved at any hue.
  const rxTraceColor = useDisplaySettingsStore((s) => s.rxTraceColor);
  const tintStyle = useMemo<CSSProperties | undefined>(() => {
    const m = /^#([0-9A-Fa-f]{2})([0-9A-Fa-f]{2})([0-9A-Fa-f]{2})$/.exec(rxTraceColor);
    if (!m || !m[1] || !m[2] || !m[3]) return undefined;
    const r = parseInt(m[1], 16);
    const g = parseInt(m[2], 16);
    const b = parseInt(m[3], 16);
    const dr = Math.round(r * 0.25);
    const dg = Math.round(g * 0.25);
    const db = Math.round(b * 0.25);
    return {
      ['--lb-tint-r' as string]: r,
      ['--lb-tint-g' as string]: g,
      ['--lb-tint-b' as string]: b,
      ['--lb-wash-r' as string]: dr,
      ['--lb-wash-g' as string]: dg,
      ['--lb-wash-b' as string]: db,
    };
  }, [rxTraceColor]);

  const [modal, setModal] = useState<ModalState>({ kind: 'closed' });

  const handleAdd = () => setModal({ kind: 'create' });

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

  const openEdit = (id: string) => setModal({ kind: 'edit', id });

  const handleModalSave = (value: LayoutSettingsValue) => {
    if (modal.kind === 'create') {
      addLayout(value.name, {
        icon: value.icon || undefined,
        description: value.description || undefined,
      });
    } else if (modal.kind === 'edit') {
      updateLayoutMeta(modal.id, {
        name: value.name,
        icon: value.icon,
        description: value.description,
      });
    }
    setModal({ kind: 'closed' });
  };

  const editingLayout =
    modal.kind === 'edit' ? layouts.find((l) => l.id === modal.id) : undefined;

  return (
    <aside className="left-layout-bar" aria-label="Layouts" style={tintStyle}>
      <div className="lb-list" role="tablist" aria-orientation="vertical">
        {!isLoaded ? (
          <div className="lb-empty" aria-hidden>…</div>
        ) : (
          layouts.map((l) => {
            const active = l.id === activeLayoutId;
            const tooltip = l.description?.trim()
              ? `${l.name} — ${l.description}`
              : `${l.name} (gear to edit)`;
            return (
              <div key={l.id} className={`lb-item ${active ? 'active' : ''}`}>
                <button
                  type="button"
                  className="lb-tab"
                  role="tab"
                  aria-selected={active}
                  onClick={() => setActiveLayout(l.id)}
                  title={tooltip}
                >
                  <span
                    className={`lb-tab-icon ${l.icon ? '' : 'lb-tab-icon-fallback'}`}
                    aria-hidden
                  >
                    {l.icon || initialLetter(l.name)}
                  </span>
                  <span className="lb-tab-name">{l.name}</span>
                </button>
                <button
                  type="button"
                  className="lb-gear"
                  onClick={() => openEdit(l.id)}
                  title={`Edit ${l.name}`}
                  aria-label={`Edit ${l.name}`}
                >
                  ⚙
                </button>
                {active && layouts.length > 1 && (
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

      {modal.kind === 'create' && (
        <LayoutSettingsModal
          title="New layout"
          initial={{
            name: `Layout ${layouts.length + 1}`,
            icon: '',
            description: '',
          }}
          onSave={handleModalSave}
          onClose={() => setModal({ kind: 'closed' })}
        />
      )}
      {modal.kind === 'edit' && editingLayout && (
        <LayoutSettingsModal
          title="Layout settings"
          initial={{
            name: editingLayout.name,
            icon: editingLayout.icon ?? '',
            description: editingLayout.description ?? '',
          }}
          onSave={handleModalSave}
          onClose={() => setModal({ kind: 'closed' })}
        />
      )}
    </aside>
  );
}

function initialLetter(name: string): string {
  const ch = name.trim().charAt(0);
  return ch ? ch.toUpperCase() : '·';
}
