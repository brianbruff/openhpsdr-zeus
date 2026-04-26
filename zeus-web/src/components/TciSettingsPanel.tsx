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

import { useEffect, useState } from 'react';
import { useTciStore } from '../state/tci-store';

export function TciSettingsPanel() {
  const settings = useTciStore((s) => s.settings);
  const loading = useTciStore((s) => s.loading);
  const saving = useTciStore((s) => s.saving);
  const storeError = useTciStore((s) => s.error);
  const load = useTciStore((s) => s.load);
  const save = useTciStore((s) => s.save);

  const [enabled, setEnabled] = useState(settings?.enabled ?? false);
  const [port, setPort] = useState(String(settings?.port ?? 40001));
  const [bindAddress, setBindAddress] = useState(settings?.bindAddress ?? '127.0.0.1');

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    if (settings) {
      setEnabled(settings.enabled);
      setPort(String(settings.port));
      setBindAddress(settings.bindAddress);
    }
  }, [settings]);

  async function onSave(e: React.FormEvent) {
    e.preventDefault();
    const portNum = Number(port);
    if (!Number.isFinite(portNum) || portNum <= 0 || portNum >= 65536) return;
    await save({ enabled, port: portNum, bindAddress: bindAddress.trim() || '127.0.0.1' });
  }

  const portBound = settings?.portBound ?? false;
  const portError = settings?.portError ?? null;
  const restartRequired = settings?.restartRequired ?? false;
  const clientCount = settings?.clientCount ?? 0;

  return (
    <div style={{ maxWidth: 600 }}>
      <h3
        style={{
          margin: '0 0 14px',
          fontSize: 11,
          fontWeight: 700,
          letterSpacing: '0.12em',
          textTransform: 'uppercase',
          color: 'var(--fg-2)',
        }}
      >
        TCI — TRANSCEIVER CONTROL INTERFACE
      </h3>

      {loading && (
        <div style={{ fontSize: 12, color: 'var(--fg-3)', marginBottom: 12 }}>Loading…</div>
      )}

      {portError && (
        <div
          style={{
            padding: 10,
            marginBottom: 12,
            fontSize: 12,
            color: 'var(--tx)',
            background: 'rgba(230, 58, 43, 0.1)',
            border: '1px solid var(--tx)',
            borderRadius: 'var(--r-sm)',
          }}
        >
          ⚠ {portError}
        </div>
      )}

      {portBound && (
        <div
          style={{
            padding: 10,
            marginBottom: 12,
            background: 'var(--bg-0)',
            border: '1px solid var(--panel-border)',
            borderRadius: 'var(--r-sm)',
            display: 'flex',
            alignItems: 'center',
            gap: 10,
            fontSize: 12,
            color: 'var(--fg-1)',
          }}
        >
          <span style={{ color: 'var(--accent)' }}>📡</span>
          <span style={{ color: 'var(--accent)', fontWeight: 600 }}>Running</span>
          <span style={{ color: 'var(--fg-3)' }}>·</span>
          <span style={{ color: 'var(--fg-2)' }}>
            {clientCount === 0 ? 'No clients connected' : `${clientCount} client${clientCount !== 1 ? 's' : ''} connected`}
          </span>
        </div>
      )}

      <form onSubmit={onSave} style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
        <label style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <input
            type="checkbox"
            checked={enabled}
            onChange={(e) => setEnabled(e.target.checked)}
            style={{ accentColor: 'var(--accent)' }}
          />
          <span style={{ fontSize: 12, fontWeight: 600, color: 'var(--fg-1)' }}>Enable TCI server</span>
        </label>

        <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
          <span style={{ fontSize: 11, fontWeight: 600, color: 'var(--fg-2)' }}>Port</span>
          <input
            type="number"
            value={port}
            onChange={(e) => setPort(e.target.value)}
            min={1}
            max={65535}
            style={{
              padding: '6px 8px',
              fontSize: 12,
              fontFamily: 'monospace',
              background: 'var(--bg-0)',
              border: '1px solid var(--panel-border)',
              borderRadius: 'var(--r-sm)',
              color: 'var(--fg-0)',
              width: 120,
            }}
          />
        </label>

        <label style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
          <span style={{ fontSize: 11, fontWeight: 600, color: 'var(--fg-2)' }}>Bind address</span>
          <input
            type="text"
            value={bindAddress}
            onChange={(e) => setBindAddress(e.target.value)}
            spellCheck={false}
            placeholder="127.0.0.1"
            style={{
              padding: '6px 8px',
              fontSize: 12,
              fontFamily: 'monospace',
              background: 'var(--bg-0)',
              border: '1px solid var(--panel-border)',
              borderRadius: 'var(--r-sm)',
              color: 'var(--fg-0)',
              width: 200,
            }}
          />
        </label>

        {restartRequired && (
          <div
            style={{
              padding: 10,
              fontSize: 12,
              color: 'var(--power)',
              background: 'rgba(255,201,58,0.08)',
              border: '1px solid rgba(255,201,58,0.4)',
              borderRadius: 'var(--r-sm)',
            }}
          >
            ↺ Restart Zeus Server to apply changes
          </div>
        )}

        {storeError && (
          <div
            style={{
              padding: 10,
              fontSize: 12,
              color: 'var(--tx)',
              background: 'rgba(230, 58, 43, 0.1)',
              border: '1px solid var(--tx)',
              borderRadius: 'var(--r-sm)',
            }}
          >
            {storeError}
          </div>
        )}

        <div style={{ display: 'flex', gap: 6 }}>
          <button type="submit" disabled={saving} className="btn sm active">
            {saving ? 'SAVING…' : 'SAVE'}
          </button>
        </div>

        <div
          style={{
            fontSize: 10,
            lineHeight: 1.5,
            color: 'var(--fg-3)',
          }}
        >
          TCI (Transceiver Control Interface) is an ExpertSDR3-compatible WebSocket protocol
          on port 40001. Spoken by loggers (Log4OM, N1MM+) and digital-mode apps (JTDX, WSJT-X).
          TCI has no authentication — use 127.0.0.1 unless clients are on a trusted LAN.
          Port and enable changes take effect on next server restart.
        </div>
      </form>
    </div>
  );
}
