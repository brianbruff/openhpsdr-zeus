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
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  connect as apiConnect,
  connectP2 as apiConnectP2,
  disconnect as apiDisconnect,
  disconnectP2 as apiDisconnectP2,
  fetchRadios,
  fetchState,
  setDrive,
  setLevelerMaxGain,
  setMicGain,
  type RadioInfoDto,
} from '../api/client';
import { getAudioClient } from '../audio/audio-client';
import { useConnectionStore } from '../state/connection-store';
import { useTxStore } from '../state/tx-store';

const DISCOVERY_INTERVAL_MS = 10_000;
const DEFAULT_DATA_PORT = 1024;
const DEFAULT_SAMPLE_RATE = 192_000;
// Only surface the error + Retry button after this many consecutive failed
// scans — a single transient timeout during hand-off shouldn't startle the
// user.
const RETRY_THRESHOLD = 2;

function endpointFor(r: RadioInfoDto): string {
  if (!r.ipAddress) return '';
  return r.ipAddress.includes(':')
    ? r.ipAddress
    : `${r.ipAddress}:${DEFAULT_DATA_PORT}`;
}

function errorMessage(err: unknown): string {
  if (err instanceof Error) return err.message;
  return String(err);
}

export function ConnectPanel() {
  const status = useConnectionStore((s) => s.status);
  const endpoint = useConnectionStore((s) => s.endpoint);
  const applyState = useConnectionStore((s) => s.applyState);
  const inflight = useConnectionStore((s) => s.inflight);
  const setInflight = useConnectionStore((s) => s.setInflight);
  const setBoardId = useConnectionStore((s) => s.setBoardId);
  const lastConnectedEndpoint = useConnectionStore(
    (s) => s.lastConnectedEndpoint,
  );
  const setLastConnectedEndpoint = useConnectionStore(
    (s) => s.setLastConnectedEndpoint,
  );
  const wisdomPhase = useConnectionStore((s) => s.wisdomPhase);
  const dspPreparing = wisdomPhase === 'building';

  const [radios, setRadios] = useState<RadioInfoDto[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [scanning, setScanning] = useState(false);
  const [failureCount, setFailureCount] = useState(0);
  const inflightRef = useRef(false);
  // Trigger for the discovery loop to fire a scan immediately instead of
  // waiting for its next 10-second tick. Bumping this re-runs the effect.
  const [retryNonce, setRetryNonce] = useState(0);

  useEffect(() => {
    inflightRef.current = inflight;
  }, [inflight]);

  useEffect(() => {
    const ctrl = new AbortController();
    fetchState(ctrl.signal)
      .then(applyState)
      .catch((err) => {
        if (ctrl.signal.aborted) return;
        setError(errorMessage(err));
        setFailureCount((n) => n + 1);
      });
    return () => ctrl.abort();
  }, [applyState]);

  useEffect(() => {
    if (status === 'Connected') return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | null = null;

    const tick = async () => {
      if (!inflightRef.current) {
        if (!cancelled) setScanning(true);
        try {
          const list = await fetchRadios();
          if (!cancelled) {
            setRadios(list);
            setError(null);
            setFailureCount(0);
          }
        } catch (err) {
          if (!cancelled) {
            setError(errorMessage(err));
            setFailureCount((n) => n + 1);
          }
        } finally {
          if (!cancelled) setScanning(false);
        }
      }
      if (!cancelled) timer = setTimeout(tick, DISCOVERY_INTERVAL_MS);
    };
    tick();

    return () => {
      cancelled = true;
      if (timer != null) clearTimeout(timer);
    };
  }, [status, retryNonce]);

  const handleConnect = useCallback(
    async (r: RadioInfoDto) => {
      if (inflightRef.current) return;
      const ep = endpointFor(r);
      const isP2 = (r.details?.protocol ?? 'P1') === 'P2';
      setInflight(true);
      setError(null);
      try {
        if (isP2) {
          await apiConnectP2({
            endpoint: ep,
            sampleRate: 48_000,
          });
          const fresh = await fetchState();
          applyState(fresh);
        } else {
          const next = await apiConnect({
            endpoint: ep,
            sampleRate: DEFAULT_SAMPLE_RATE,
          });
          applyState(next);
        }
        setBoardId(r.boardId || null);
        setLastConnectedEndpoint(ep || null);
        // Auto-unmute on connect: the user's Connect click is the gesture
        // that satisfies the browser's autoplay policy, so AudioContext can
        // resume here without a second Play click. If the user has already
        // started audio this is a no-op (AudioClient.start short-circuits
        // when already playing).
        void getAudioClient().start();
        // Push the TX store's persisted values to the freshly-connected
        // radio. Drive and mic-gain live in localStorage (zustand persist)
        // so the slider thumb shows the right number immediately, but the
        // HL2/backend has no memory of the last session — without these
        // POSTs the radio runs on its boot defaults while the UI reads the
        // user's saved value, so the first MOX would transmit at the wrong
        // power. Fire-and-forget: the sliders themselves will retry on the
        // next drag, and a transient failure here isn't worth surfacing.
        const tx = useTxStore.getState();
        void setDrive(tx.drivePercent).catch(() => {});
        void setMicGain(tx.micGainDb).catch(() => {});
        // Leveler max-gain is stateless across backend restart; re-POST the
        // persisted value so the radio uses our preference instead of the
        // server default (+5 dB).
        void setLevelerMaxGain(tx.levelerMaxGainDb).catch(() => {});
      } catch (err) {
        setError(errorMessage(err));
      } finally {
        setInflight(false);
      }
    },
    [applyState, setBoardId, setInflight, setLastConnectedEndpoint],
  );

  const handleDisconnect = useCallback(async () => {
    if (inflightRef.current) return;
    setInflight(true);
    setError(null);
    try {
      // Try P1 disconnect first; if the server reports it's a P2 session
      // (no P1 client active), fall back to the P2 endpoint. Fire both as a
      // safety net — each is idempotent and returns cleanly if nothing is
      // connected on its side.
      try { await apiDisconnect(); } catch { /* may be P2 */ }
      try { await apiDisconnectP2(); } catch { /* may have been P1 */ }
      const fresh = await fetchState();
      applyState(fresh);
      setBoardId(null);
      setRadios(null);
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setInflight(false);
    }
  }, [applyState, setBoardId, setInflight]);

  const handleRetry = useCallback(() => {
    setError(null);
    setFailureCount(0);
    setRetryNonce((n) => n + 1);
  }, []);

  // Float the last-connected radio to the top so one-tap reconnect lands on
  // the right row without the user scanning the list. Falls back to the
  // server's discovery order for everything else.
  const sortedRadios = useMemo(() => {
    if (!radios || !lastConnectedEndpoint) return radios;
    const preferred: RadioInfoDto[] = [];
    const rest: RadioInfoDto[] = [];
    for (const r of radios) {
      if (endpointFor(r) === lastConnectedEndpoint) preferred.push(r);
      else rest.push(r);
    }
    return [...preferred, ...rest];
  }, [radios, lastConnectedEndpoint]);

  const showError = error !== null && failureCount >= RETRY_THRESHOLD;

  if (status === 'Connected') {
    return (
      <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
        <span className="chip accent">
          <span className="k">RADIO</span>
          <span className="v mono">{endpoint ?? '—'}</span>
        </span>
        {error && (
          <span className="label-xs" style={{ color: 'var(--tx)' }}>
            {error}
          </span>
        )}
        <button type="button" onClick={handleDisconnect} disabled={inflight} className="btn sm">
          {inflight ? 'Disconnecting…' : 'Disconnect'}
        </button>
      </div>
    );
  }

  const statusRight = dspPreparing
    ? 'Preparing DSP…'
    : status === 'Connecting'
      ? 'Connecting…'
      : inflight
        ? 'Working…'
        : scanning
          ? 'Scanning…'
          : 'Refreshes every 10 s';

  return (
    <div className="panel" style={{ padding: 16, minWidth: 420, maxWidth: 540 }}>
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'space-between',
          marginBottom: 10,
        }}
      >
        <span className="label-xs" style={{ fontSize: 11, letterSpacing: '0.14em' }}>
          DISCOVER RADIO
        </span>
        <span className="label-xs" style={{ color: 'var(--fg-3)' }}>
          {scanning && <span aria-hidden>· </span>}
          {statusRight}
        </span>
      </div>
      {showError && (
        <div
          className="mono"
          style={{
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
            padding: '6px 10px',
            background: 'rgba(230,58,43,0.12)',
            border: '1px solid rgba(230,58,43,0.35)',
            borderRadius: 4,
            color: 'var(--tx)',
            marginBottom: 8,
            fontSize: 11,
          }}
        >
          <span>{error}</span>
          <button type="button" onClick={handleRetry} className="btn sm">
            Retry
          </button>
        </div>
      )}
      {sortedRadios === null ? (
        <div className="label-xs" style={{ color: 'var(--fg-3)' }}>
          Scanning LAN…
        </div>
      ) : sortedRadios.length === 0 ? (
        <div className="label-xs" style={{ color: 'var(--fg-3)' }}>
          No radios found. Check power, ethernet, and subnet.
        </div>
      ) : (
        <ul style={{ listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: 4 }}>
          {sortedRadios.map((r) => {
            const ep = endpointFor(r);
            const isLast = !!ep && ep === lastConnectedEndpoint;
            const protocol = r.details?.protocol ?? 'P1';
            const isP2 = protocol === 'P2';
            return (
              <li
                key={r.macAddress || r.ipAddress}
                style={{
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'space-between',
                  gap: 10,
                  padding: '6px 10px',
                  background: 'var(--bg-2)',
                  border: '1px solid var(--panel-border)',
                  borderRadius: 4,
                }}
              >
                <div style={{ display: 'flex', flexDirection: 'column', minWidth: 0 }}>
                  <span style={{ color: 'var(--fg-0)', fontSize: 12, fontWeight: 600 }}>
                    {r.boardId || 'radio'}{' '}
                    <span className="label-xs" style={{ color: 'var(--fg-3)' }}>
                      fw {r.firmwareVersion || '?'}
                    </span>
                    <span
                      className="chip"
                      style={{ marginLeft: 6 }}
                      title={`Discovered via Protocol ${protocol === 'P2' ? '2' : '1'}`}
                    >
                      <span className="v">{protocol}</span>
                    </span>
                    {isLast && (
                      <span className="chip accent" style={{ marginLeft: 6 }} title="Last connected radio">
                        <span className="v">LAST</span>
                      </span>
                    )}
                  </span>
                  <span className="mono label-xs" style={{ color: 'var(--fg-3)' }}>
                    {ep || '—'} · {r.macAddress || '—'}
                  </span>
                </div>
                <button
                  type="button"
                  onClick={() => handleConnect(r)}
                  disabled={r.busy || inflight || (dspPreparing && !isP2)}
                  title={
                    r.busy
                      ? 'Radio is busy (in use by another client)'
                      : dspPreparing && !isP2
                        ? 'DSP is preparing FFTW plans (first-run only, up to ~2 min)'
                        : isP2
                          ? 'Protocol 2 path — experimental, RX only'
                          : undefined
                  }
                  className={`btn sm ${r.busy ? '' : 'active'} ${dspPreparing && !isP2 ? 'pulsing' : ''}`}
                >
                  {r.busy
                    ? 'Busy'
                    : dspPreparing && !isP2
                      ? 'Preparing DSP…'
                      : inflight
                        ? 'Connecting…'
                        : 'Connect'}
                </button>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}
