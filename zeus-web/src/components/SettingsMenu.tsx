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

type TabId =
  | 'general'
  | 'radio'
  | 'dsp'
  | 'display'
  | 'audio'
  | 'network'
  | 'recording'
  | 'keyboard'
  | 'pa'
  | 'advanced';

const TABS: ReadonlyArray<{ id: TabId; label: string }> = [
  { id: 'general', label: 'GENERAL' },
  { id: 'radio', label: 'RADIO' },
  { id: 'dsp', label: 'DSP' },
  { id: 'display', label: 'DISPLAY' },
  { id: 'audio', label: 'AUDIO' },
  { id: 'network', label: 'NETWORK' },
  { id: 'recording', label: 'RECORDING' },
  { id: 'keyboard', label: 'KEYBOARD' },
  { id: 'pa', label: 'PA SETTINGS' },
  { id: 'advanced', label: 'ADVANCED' },
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
  const [active, setActive] = useState<TabId>('general');
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

  const panelStyle: React.CSSProperties =
    pos === null
      ? {
          position: 'fixed',
          left: '50%',
          top: '50%',
          transform: 'translate(-50%, -50%)',
          width: 'min(1100px, 92vw)',
          maxHeight: '85vh',
          zIndex: 50,
        }
      : {
          position: 'fixed',
          left: `${pos.x}px`,
          top: `${pos.y}px`,
          width: 'min(1100px, 92vw)',
          maxHeight: '85vh',
          zIndex: 50,
        };

  return (
    <div
      ref={panelRef}
      style={panelStyle}
      className="flex flex-col overflow-hidden rounded-lg border border-neutral-700 bg-neutral-900/95 shadow-2xl"
      role="dialog"
      aria-modal="false"
      aria-labelledby="settings-title"
    >
      <div
        onMouseDown={startDrag}
        className="flex cursor-move select-none items-center justify-between border-b border-neutral-800 px-4 py-3"
        title="Drag to move"
      >
        <h2
          id="settings-title"
          className="text-sm font-semibold tracking-widest"
          style={{ color: '#FFA028' }}
        >
          SETTINGS
        </h2>
        <button
          type="button"
          onClick={onClose}
          onMouseDown={(e) => e.stopPropagation()}
          aria-label="Close"
          className="rounded px-2 py-0.5 text-lg leading-none text-neutral-400 hover:bg-neutral-800 hover:text-neutral-100"
        >
          ×
        </button>
      </div>

      <div
        className="flex flex-wrap gap-1 border-b border-neutral-800 px-3 py-2"
        role="tablist"
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
              className="rounded px-3 py-1 text-xs font-semibold tracking-wider transition-colors"
              style={
                isActive
                  ? { backgroundColor: '#FFA028', color: '#1a1a1a' }
                  : undefined
              }
            >
              <span
                className={
                  isActive ? '' : 'text-neutral-400 hover:text-neutral-100'
                }
              >
                {t.label}
              </span>
            </button>
          );
        })}
      </div>

      <div
        className="flex-1 overflow-auto px-6 py-6 text-sm text-neutral-300"
        role="tabpanel"
      >
        <p className="italic text-neutral-500">
          {TABS.find((t) => t.id === active)?.label} settings — coming soon.
        </p>
      </div>

      <div className="flex items-center justify-between border-t border-neutral-800 px-4 py-3">
        <button type="button" className="btn sm">
          EXPORT SETTINGS
        </button>
        <div className="flex items-center gap-2">
          <button type="button" className="btn sm" onClick={onClose}>
            OK
          </button>
          <button type="button" className="btn sm" onClick={onClose}>
            CANCEL
          </button>
          <button type="button" className="btn sm">
            APPLY
          </button>
        </div>
      </div>
    </div>
  );
}
