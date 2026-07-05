// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

import { useEffect, useState } from 'react';

import type { PluginUpdate } from '../updates';

type Props = {
  updates: PluginUpdate[];
  onDismiss: () => void;
  onOpenPlugins: () => void;
};

const MAX_VISIBLE_UPDATES = 5;

export function PluginUpdatePrompt({
  updates,
  onDismiss,
  onOpenPlugins,
}: Props) {
  const [visible, setVisible] = useState(false);

  useEffect(() => {
    if (updates.length === 0) {
      setVisible(false);
      return;
    }
    const timer = setTimeout(() => setVisible(true), 100);
    return () => clearTimeout(timer);
  }, [updates.length]);

  if (updates.length === 0) return null;

  const visibleUpdates = updates.slice(0, MAX_VISIBLE_UPDATES);
  const overflow = updates.length - visibleUpdates.length;

  return (
    <div
      role="status"
      aria-live="polite"
      style={{
        position: 'fixed',
        top: 16,
        right: 16,
        zIndex: 10000,
        maxWidth: 420,
        width: 'calc(100% - 32px)',
        transform: `translateY(${visible ? '0' : '-120%'})`,
        transition: 'transform 0.25s ease-out',
      }}
    >
      <div
        style={{
          display: 'flex',
          flexDirection: 'column',
          gap: 10,
          padding: 14,
          borderRadius: 'var(--r-md)',
          border: '1px solid var(--panel-border)',
          background: 'var(--panel-top)',
          color: 'var(--fg-1)',
          boxShadow: '0 10px 28px rgba(0, 0, 0, 0.35)',
        }}
      >
        <div>
          <div
            style={{
              fontSize: 11,
              fontWeight: 700,
              letterSpacing: '0.12em',
              textTransform: 'uppercase',
              color: 'var(--power)',
              marginBottom: 4,
            }}
          >
            PLUGIN UPDATES AVAILABLE
          </div>
          <div style={{ fontSize: 13, fontWeight: 700 }}>
            New plugin releases are ready to download
          </div>
        </div>

        <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          {visibleUpdates.map((update) => (
            <div
              key={update.id}
              style={{
                display: 'flex',
                justifyContent: 'space-between',
                gap: 10,
                padding: '6px 8px',
                borderRadius: 'var(--r-sm)',
                background: 'var(--bg-2)',
                border: '1px solid var(--line)',
              }}
            >
              <span
                style={{
                  color: 'var(--fg-0)',
                  fontSize: 12,
                  fontWeight: 700,
                  minWidth: 0,
                  overflowWrap: 'anywhere',
                }}
              >
                {update.name}
              </span>
              <span
                style={{
                  color: 'var(--fg-2)',
                  fontSize: 11,
                  fontFamily: 'var(--font-mono)',
                  whiteSpace: 'nowrap',
                }}
              >
                {update.installedVersion} → {update.latestVersion}
              </span>
            </div>
          ))}
          {overflow > 0 && (
            <div style={{ color: 'var(--fg-3)', fontSize: 11 }}>
              +{overflow} more
            </div>
          )}
        </div>

        <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8, flexWrap: 'wrap' }}>
          <button type="button" className="btn sm" onClick={onDismiss}>
            LATER
          </button>
          <button type="button" className="btn sm active" onClick={onOpenPlugins}>
            OPEN PLUGINS
          </button>
        </div>
      </div>
    </div>
  );
}
