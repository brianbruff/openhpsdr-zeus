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

import { useEffect, useRef, useState } from 'react';
import { PaSettingsPanel } from './PaSettingsPanel';
import { RadioSelector } from './RadioSelector';
import { usePaStore } from '../state/pa-store';

type TabId = 'pa';

const TABS: ReadonlyArray<{ id: TabId; label: string }> = [
  { id: 'pa', label: 'PA SETTINGS' },
];

type Props = {
  open: boolean;
  onClose: () => void;
};

// Floating, draggable settings panel. Deliberately has NO backdrop: the operator
// must be able to MOX / TUN / tune the radio while the panel is open. The only
// event-capture surface is the panel rectangle itself; everything outside
// (panadapter, top-bar buttons) stays clickable.
export function SettingsMenu({ open, onClose }: Props) {
  const [active, setActive] = useState<TabId>('pa');
  const savePa = usePaStore((s) => s.save);
  const loadPa = usePaStore((s) => s.load);
  const paInflight = usePaStore((s) => s.inflight);

  const handleApply = async () => {
    await savePa();
    onClose();
  };
  const handleCancel = async () => {
    // Discard any in-memory edits by re-fetching the server's canonical state.
    await loadPa();
    onClose();
  };
  // null = use the initial-centering flex layout on first render; after the
  // first drag (or the post-mount centering effect), a concrete {x,y} takes
  // over and the panel positions absolutely. Off-screen values are allowed —
  // the user explicitly wanted to be able to drag the window off-screen.
  const [pos, setPos] = useState<{ x: number; y: number } | null>(null);
  const panelRef = useRef<HTMLDivElement>(null);
  const dragRef = useRef<{ dx: number; dy: number } | null>(null);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, onClose]);

  // Center on first open: measure the panel's natural rect and pin it there,
  // switching from flex-centering to absolute positioning so dragging starts
  // from a stable origin.
  useEffect(() => {
    if (!open) {
      setPos(null);
      return;
    }
    if (pos !== null) return;
    const el = panelRef.current;
    if (!el) return;
    const rect = el.getBoundingClientRect();
    setPos({
      x: Math.max(8, (window.innerWidth - rect.width) / 2),
      y: Math.max(8, (window.innerHeight - rect.height) / 2),
    });
  }, [open, pos]);

  // Global mousemove/up while a drag is in flight. Listener is always mounted
  // so we can start dragging from the header's mousedown without remounting.
  useEffect(() => {
    const move = (e: MouseEvent) => {
      const d = dragRef.current;
      if (!d) return;
      setPos({ x: e.clientX - d.dx, y: e.clientY - d.dy });
    };
    const up = () => {
      dragRef.current = null;
    };
    window.addEventListener('mousemove', move);
    window.addEventListener('mouseup', up);
    return () => {
      window.removeEventListener('mousemove', move);
      window.removeEventListener('mouseup', up);
    };
  }, []);

  const startDrag = (e: React.MouseEvent<HTMLDivElement>) => {
    const el = panelRef.current;
    if (!el) return;
    const rect = el.getBoundingClientRect();
    dragRef.current = {
      dx: e.clientX - rect.left,
      dy: e.clientY - rect.top,
    };
    e.preventDefault();
  };

  if (!open) return null;

  const basePanel: React.CSSProperties = {
    position: 'fixed',
    width: 'min(1100px, 92vw)',
    height: 'min(640px, 85vh)',
    zIndex: 50,
    background: 'var(--bg-1)',
    border: '1px solid var(--panel-border)',
    borderRadius: 'var(--r-md)',
    boxShadow: '0 20px 60px rgba(0,0,0,0.55), 0 0 0 1px rgba(255,255,255,0.03)',
    color: 'var(--fg-1)',
    display: 'flex',
    flexDirection: 'column',
    overflow: 'hidden',
  };
  const panelStyle: React.CSSProperties =
    pos === null
      ? { ...basePanel, left: '50%', top: '50%', transform: 'translate(-50%, -50%)' }
      : { ...basePanel, left: `${pos.x}px`, top: `${pos.y}px` };

  return (
    <div
      ref={panelRef}
      style={panelStyle}
      role="dialog"
      aria-modal="false"
      aria-labelledby="settings-title"
    >
      <div
        onMouseDown={startDrag}
        style={{
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'space-between',
          cursor: 'move',
          userSelect: 'none',
          height: 44,
          padding: '0 14px',
          background:
            'linear-gradient(180deg, var(--panel-head-top), var(--panel-head-bot))',
          borderBottom: '1px solid var(--panel-border)',
          boxShadow: 'inset 0 1px 0 var(--panel-hl-top)',
        }}
        title="Drag to move"
      >
        <h2
          id="settings-title"
          style={{
            margin: 0,
            fontSize: 12,
            fontWeight: 700,
            letterSpacing: '0.18em',
            color: 'var(--fg-0)',
            textTransform: 'uppercase',
          }}
        >
          Settings
        </h2>
        <button
          type="button"
          onClick={onClose}
          onMouseDown={(e) => e.stopPropagation()}
          aria-label="Close"
          style={{
            width: 26,
            height: 26,
            borderRadius: 'var(--r-sm)',
            color: 'var(--fg-2)',
            fontSize: 18,
            lineHeight: 1,
            background: 'transparent',
            cursor: 'pointer',
          }}
          onMouseEnter={(e) => {
            e.currentTarget.style.background = 'rgba(255,255,255,0.06)';
            e.currentTarget.style.color = 'var(--fg-0)';
          }}
          onMouseLeave={(e) => {
            e.currentTarget.style.background = 'transparent';
            e.currentTarget.style.color = 'var(--fg-2)';
          }}
        >
          ×
        </button>
      </div>

      <RadioSelector />

      <div style={{ display: 'flex', flex: 1, minHeight: 0 }}>
        <nav
          role="tablist"
          aria-label="Settings sections"
          style={{
            width: 168,
            flexShrink: 0,
            display: 'flex',
            flexDirection: 'column',
            gap: 2,
            padding: '10px 8px',
            background: 'var(--bg-0)',
            borderRight: '1px solid var(--panel-border)',
            overflowY: 'auto',
          }}
        >
          {TABS.map((t) => {
            const isActive = t.id === active;
            return (
              <button
                key={t.id}
                type="button"
                role="tab"
                aria-selected={isActive}
                onClick={() => setActive(t.id)}
                style={{
                  textAlign: 'left',
                  padding: '8px 12px',
                  borderRadius: 'var(--r-sm)',
                  fontSize: 11,
                  fontWeight: 700,
                  letterSpacing: '0.12em',
                  textTransform: 'uppercase',
                  color: isActive ? 'var(--fg-0)' : 'var(--fg-2)',
                  background: isActive ? 'var(--bg-2)' : 'transparent',
                  borderLeft: isActive
                    ? '2px solid var(--accent)'
                    : '2px solid transparent',
                  cursor: 'pointer',
                  transition: 'background var(--dur-fast), color var(--dur-fast)',
                }}
                onMouseEnter={(e) => {
                  if (!isActive) {
                    e.currentTarget.style.background = 'rgba(255,255,255,0.03)';
                    e.currentTarget.style.color = 'var(--fg-1)';
                  }
                }}
                onMouseLeave={(e) => {
                  if (!isActive) {
                    e.currentTarget.style.background = 'transparent';
                    e.currentTarget.style.color = 'var(--fg-2)';
                  }
                }}
              >
                {t.label}
              </button>
            );
          })}
        </nav>

        <div
          role="tabpanel"
          style={{
            flex: 1,
            minWidth: 0,
            overflow: 'auto',
            padding: '18px 22px',
            background: 'var(--bg-1)',
            color: 'var(--fg-1)',
            fontSize: 12,
          }}
        >
          <PaSettingsPanel />
        </div>
      </div>

      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'flex-end',
          gap: 6,
          padding: '10px 14px',
          background: 'var(--bg-0)',
          borderTop: '1px solid var(--panel-border)',
        }}
      >
        <button type="button" className="btn sm" onClick={handleCancel} disabled={paInflight}>
          CANCEL
        </button>
        <button
          type="button"
          className="btn sm active"
          onClick={handleApply}
          disabled={paInflight}
        >
          {paInflight ? 'SAVING…' : 'APPLY'}
        </button>
      </div>
    </div>
  );
}
