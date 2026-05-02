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
// VST host submenu — issue #106 / Wave 6b. Master toggle + 8-slot chain
// editor + plugin browser. Plugin GUIs open as native OS windows on the
// operator's desktop, NOT inside the browser. The submenu lives inside
// TxAudioToolsPanel beside CFC.

import { useCallback, useEffect, useState } from 'react';

import { setTxMonitor } from '../api/client';
import { useCapabilitiesStore } from '../state/capabilities-store';
import { useTxStore } from '../state/tx-store';
import { useVstHostStore } from '../state/vst-host-store';
import { VST_HOST_SLOT_COUNT } from '../api/vst-host';
import { VstHostPluginBrowser } from './VstHostPluginBrowser';
import { VstHostSlotRow } from './VstHostSlotRow';

export function VstHostSubmenu() {
  const loaded = useVstHostStore((s) => s.loaded);
  const inflight = useVstHostStore((s) => s.inflight);
  const loadError = useVstHostStore((s) => s.loadError);
  const masterEnabled = useVstHostStore((s) => s.master.masterEnabled);
  const isRunning = useVstHostStore((s) => s.master.isRunning);
  const notice = useVstHostStore((s) => s.notice);
  const refresh = useVstHostStore((s) => s.refresh);
  const setMasterEnabled = useVstHostStore((s) => s.setMasterEnabled);
  const clearNotice = useVstHostStore((s) => s.clearNotice);

  // Browser drawer overlays the chain area; null = hidden, number =
  // "load into slot N", -1 = preview / browse-only.
  const [browserSlot, setBrowserSlot] = useState<number | null>(null);
  // While a master-toggle POST is in flight, lock the toggle so a second
  // click doesn't race the first.
  const [masterPending, setMasterPending] = useState(false);

  // Plugin GUIs open as native OS windows on the host running Zeus. When
  // the operator's browser is on a different machine we hide the chain
  // editor (load / unload / edit / parameter sliders) and the catalog
  // browser, but keep the master toggle and per-slot Bypass — those are
  // operationally important kill-switches that don't depend on the
  // plugin's editor being on screen.
  const localToServer = useCapabilitiesStore((s) => s.localToServer);

  // TX Monitor — audition path so the operator can hear the chain output
  // (post-bandpass / post-CFIR demod) at the actual TX bandwidth without
  // keying. Lives in this submenu (not the main GUI) per maintainer rule.
  // Optimistic local toggle + POST with rollback on failure, same shape as
  // MoxButton / PsMonitor.
  const txMonitorOn = useTxStore((s) => s.txMonitorEnabled);
  const setTxMonitorLocal = useTxStore((s) => s.setTxMonitorEnabled);
  const onTxMonitorClick = useCallback(() => {
    const next = !txMonitorOn;
    setTxMonitorLocal(next);
    setTxMonitor(next).catch(() => setTxMonitorLocal(!next));
  }, [txMonitorOn, setTxMonitorLocal]);

  useEffect(() => {
    // First mount kicks the initial /api/plughost/state fetch. WS
    // events keep it warm thereafter.
    if (!loaded && !inflight) void refresh();
  }, [loaded, inflight, refresh]);

  const onToggleMaster = async (next: boolean) => {
    setMasterPending(true);
    try {
      await setMasterEnabled(next);
    } finally {
      setMasterPending(false);
    }
  };

  const slotsDisabled = !masterEnabled;

  return (
    <section
      style={{
        position: 'relative',
        border: '1px solid var(--panel-border)',
        borderRadius: 6,
        padding: '10px 12px',
        background: 'var(--bg-1)',
        display: 'flex',
        flexDirection: 'column',
        gap: 10,
        // The plugin browser overlays this section, so cap height so the
        // overlay has bounded geometry.
        minHeight: 320,
      }}
    >
      <header
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 12,
        }}
      >
        <h3
          style={{
            margin: 0,
            fontSize: 11,
            fontWeight: 700,
            letterSpacing: '0.12em',
            textTransform: 'uppercase',
            color: 'var(--fg-1)',
            flex: 1,
          }}
        >
          VST Host
        </h3>
        <label
          style={{
            display: 'flex',
            alignItems: 'center',
            gap: 6,
            color: 'var(--fg-2)',
            fontSize: 11,
          }}
        >
          <input
            type="checkbox"
            checked={masterEnabled}
            disabled={masterPending}
            onChange={(e) => void onToggleMaster(e.target.checked)}
            aria-label="Toggle VST chain"
          />
          VST Chain
        </label>
        <button
          type="button"
          onClick={onTxMonitorClick}
          className={`btn tx-btn ${txMonitorOn ? 'tx' : ''}`}
          title={
            txMonitorOn
              ? 'Monitor on — auditioning TX audio at TX bandwidth (RX muted)'
              : 'Monitor off (click to audition TX chain at TX bandwidth)'
          }
        >
          <span className={`led ${txMonitorOn ? 'tx' : ''}`} style={{ marginRight: 8 }} />
          MONITOR
        </button>
        {localToServer ? (
          <button
            type="button"
            className="btn sm"
            onClick={() => setBrowserSlot(-1)}
          >
            BROWSE PLUGINS
          </button>
        ) : null}
      </header>

      <p style={{ margin: 0, fontSize: 11, color: 'var(--fg-2)' }}>
        Out-of-process VST3 host: 8 chain slots run in a sidecar and are
        bypassed when the master toggle is off. Plugin editors open as
        native OS windows on this device — not inside the browser.
      </p>

      {!localToServer ? (
        <div
          style={{
            fontSize: 11,
            color: 'var(--fg-2)',
            background: 'var(--bg-2)',
            border: '1px solid var(--panel-border)',
            borderRadius: 4,
            padding: '6px 8px',
          }}
        >
          Plugin chain is editable only from the server console. You can
          enable, disable, or bypass slots from here; loading plugins and
          editing parameters requires being at the host running Zeus.
        </div>
      ) : null}

      {loadError ? (
        <div style={{ fontSize: 11, color: 'var(--tx)' }}>
          Failed to load VST host state: {loadError}
        </div>
      ) : null}

      {notice ? (
        <div
          style={{
            fontSize: 11,
            color: 'var(--power)',
            display: 'flex',
            alignItems: 'center',
            gap: 6,
          }}
        >
          <span>{notice}</span>
          <button
            type="button"
            onClick={clearNotice}
            style={{ fontSize: 10, color: 'var(--fg-2)' }}
            aria-label="Dismiss notice"
          >
            ×
          </button>
        </div>
      ) : null}

      {masterEnabled && !isRunning ? (
        <div style={{ fontSize: 11, color: 'var(--fg-2)' }}>
          Plugin host sidecar starting…
        </div>
      ) : null}

      {!masterEnabled ? (
        <div style={{ fontSize: 11, color: 'var(--fg-3)' }}>
          Enable VST chain to load plugins.
        </div>
      ) : null}

      <div
        style={{
          display: 'flex',
          flexDirection: 'column',
          gap: 4,
        }}
      >
        {Array.from({ length: VST_HOST_SLOT_COUNT }, (_, i) => (
          <VstHostSlotRow
            key={i}
            index={i}
            disabled={slotsDisabled}
            remote={!localToServer}
            onRequestLoad={(idx) => setBrowserSlot(idx)}
          />
        ))}
      </div>

      <VstHostPluginBrowser
        open={browserSlot !== null}
        targetSlot={browserSlot !== null && browserSlot >= 0 ? browserSlot : null}
        onClose={() => setBrowserSlot(null)}
      />
    </section>
  );
}
