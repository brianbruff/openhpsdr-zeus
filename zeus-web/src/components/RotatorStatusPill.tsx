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
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF), with contributions from (DH1KLM); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { useEffect, useRef, useState } from 'react';
import { useRotatorStore } from '../state/rotator-store';

export function RotatorStatusPill() {
  const config = useRotatorStore((s) => s.config);
  const status = useRotatorStore((s) => s.status);
  const testInFlight = useRotatorStore((s) => s.testInFlight);
  const lastTestResult = useRotatorStore((s) => s.lastTestResult);
  const saveConfig = useRotatorStore((s) => s.saveConfig);
  const stop = useRotatorStore((s) => s.stop);
  const test = useRotatorStore((s) => s.test);

  const [open, setOpen] = useState(false);
  const [host, setHost] = useState(config.host);
  const [port, setPort] = useState(String(config.port));
  const [enabled, setEnabled] = useState(config.enabled);
  const [saving, setSaving] = useState(false);
  const wrapperRef = useRef<HTMLDivElement | null>(null);

  // Rehydrate the form when the store finishes reading localStorage after mount.
  useEffect(() => {
    setHost(config.host);
    setPort(String(config.port));
    setEnabled(config.enabled);
  }, [config.host, config.port, config.enabled]);

  useEffect(() => {
    if (!open) return;
    function onClick(e: MouseEvent) {
      if (wrapperRef.current && !wrapperRef.current.contains(e.target as Node)) setOpen(false);
    }
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') setOpen(false);
    }
    window.addEventListener('mousedown', onClick);
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('mousedown', onClick);
      window.removeEventListener('keydown', onKey);
    };
  }, [open]);

  const connected = !!status?.connected;
  const moving = !!status?.moving;
  const currentAz = status?.currentAz;
  const targetAz = status?.targetAz;

  // Pill label mirrors log4ym's feel: off / connecting / NNN° / → NNN°.
  let label: string;
  let pillClass: string;
  if (!config.enabled) {
    label = 'Rotator: off';
    pillClass = 'bg-neutral-800 text-neutral-400 border border-neutral-600/60';
  } else if (!connected) {
    label = 'Rotator: …';
    pillClass = 'bg-amber-700/30 text-amber-200 border border-amber-500/70';
  } else if (moving && targetAz != null) {
    label = `Rotator: ${formatAz(currentAz)} → ${formatAz(targetAz)}`;
    pillClass = 'bg-cyan-700/40 text-cyan-200 border border-cyan-500/70';
  } else {
    label = `Rotator: ${formatAz(currentAz)}`;
    pillClass = 'bg-emerald-700/50 text-emerald-200 border border-emerald-600/70';
  }

  async function onSave(e: React.FormEvent) {
    e.preventDefault();
    setSaving(true);
    try {
      const portNum = Number(port);
      if (!Number.isFinite(portNum) || portNum <= 0 || portNum >= 65536) return;
      await saveConfig({
        enabled,
        host: host.trim() || '127.0.0.1',
        port: portNum,
        pollingIntervalMs: config.pollingIntervalMs,
      });
      setOpen(false);
    } finally {
      setSaving(false);
    }
  }

  async function onTest() {
    const portNum = Number(port);
    if (!Number.isFinite(portNum) || portNum <= 0 || portNum >= 65536) return;
    await test(host.trim() || '127.0.0.1', portNum);
  }

  return (
    <div ref={wrapperRef} className="relative">
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        className={`${pillClass} rounded px-2 py-0.5 text-xs hover:brightness-125`}
        title="rotctld (hamlib rotator) session"
      >
        {connected ? '●' : '○'} {label}
      </button>
      {open && (
        <div className="absolute right-0 top-full z-40 mt-1 w-80 rounded border border-neutral-700 bg-neutral-900 p-3 text-xs shadow-lg">
          <form onSubmit={onSave} className="flex flex-col gap-2">
            <div className="flex items-center justify-between">
              <span className="font-semibold tracking-wider text-neutral-200">ROTCTLD</span>
              <label className="flex items-center gap-1 text-neutral-400">
                <input
                  type="checkbox"
                  checked={enabled}
                  onChange={(e) => setEnabled(e.target.checked)}
                  className="accent-emerald-600"
                />
                Enabled
              </label>
            </div>
            <label className="flex flex-col gap-1 text-neutral-400">
              Host
              <input
                type="text"
                value={host}
                onChange={(e) => setHost(e.target.value)}
                spellCheck={false}
                className="rounded border border-neutral-700 bg-neutral-950 px-2 py-1 font-mono text-neutral-100 focus:border-emerald-600 focus:outline-none"
              />
            </label>
            <label className="flex flex-col gap-1 text-neutral-400">
              Port
              <input
                type="number"
                value={port}
                onChange={(e) => setPort(e.target.value)}
                min={1}
                max={65535}
                className="rounded border border-neutral-700 bg-neutral-950 px-2 py-1 font-mono text-neutral-100 focus:border-emerald-600 focus:outline-none"
              />
            </label>

            {status?.error && (
              <div className="text-rose-400">{status.error}</div>
            )}
            {lastTestResult && (
              <div className={lastTestResult.ok ? 'text-emerald-400' : 'text-rose-400'}>
                {lastTestResult.ok
                  ? `Test OK — rotctld is reachable at ${host}:${port}`
                  : `Test failed: ${lastTestResult.error ?? 'unknown error'}`}
              </div>
            )}

            {connected && (
              <div className="flex items-center gap-2 text-neutral-300">
                <span className="text-neutral-500">Current:</span>
                <span className="font-mono text-emerald-300">{formatAz(currentAz)}</span>
                {targetAz != null && (
                  <>
                    <span className="text-neutral-500">· Target:</span>
                    <span className="font-mono text-cyan-300">{formatAz(targetAz)}</span>
                  </>
                )}
                {moving && <span className="text-cyan-400">moving</span>}
              </div>
            )}

            <div className="mt-1 flex gap-2">
              <button
                type="button"
                onClick={onTest}
                disabled={testInFlight}
                className="rounded border border-neutral-700 px-2 py-1 text-neutral-300 hover:bg-neutral-800 disabled:opacity-50"
              >
                {testInFlight ? 'Testing…' : 'Test'}
              </button>
              {connected && (
                <button
                  type="button"
                  onClick={() => stop()}
                  className="rounded border border-amber-600 px-2 py-1 text-amber-200 hover:bg-amber-900/30"
                >
                  Stop
                </button>
              )}
              <span className="flex-1" />
              <button
                type="submit"
                disabled={saving}
                className="rounded bg-emerald-700 px-3 py-1 text-neutral-50 hover:bg-emerald-600 disabled:opacity-50"
              >
                {saving ? 'Saving…' : 'Save'}
              </button>
            </div>

            <div className="text-[10px] leading-tight text-neutral-500">
              rotctld is hamlib's rotator daemon. Start it with e.g.{' '}
              <span className="font-mono">rotctld -m 2 -r /dev/ttyUSB0 -s 9600 -t 4533</span>{' '}
              (model 2 = dummy rotor for testing). Settings are remembered locally;
              the backend holds no persistent state across restarts.
            </div>
          </form>
        </div>
      )}
    </div>
  );
}

function formatAz(az: number | null | undefined): string {
  if (az == null || !Number.isFinite(az)) return '—';
  // hamlib can report signed azimuths when the rotator crosses its zero
  // point (e.g. -79° on a rotor that can swing past 0°). For display we
  // want the equivalent 0..359 heading so the compass-style reading is
  // unambiguous (−79° → 281°).
  const normalized = ((az % 360) + 360) % 360;
  return `${normalized.toFixed(0).padStart(3, '0')}°`;
}
