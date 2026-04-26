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
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { useEffect, useRef, useState } from 'react';
import { useQrzStore } from '../state/qrz-store';

export function QrzStatusPill() {
  const connected = useQrzStore((s) => s.connected);
  const hasXml = useQrzStore((s) => s.hasXmlSubscription);
  const hasApiKey = useQrzStore((s) => s.hasApiKey);
  const home = useQrzStore((s) => s.home);
  const rememberedUsername = useQrzStore((s) => s.rememberedUsername);
  const loginInFlight = useQrzStore((s) => s.loginInFlight);
  const loginError = useQrzStore((s) => s.loginError);
  const login = useQrzStore((s) => s.login);
  const logout = useQrzStore((s) => s.logout);
  const setApiKey = useQrzStore((s) => s.setApiKey);

  const [open, setOpen] = useState(false);
  const [username, setUsername] = useState(rememberedUsername);
  const [password, setPassword] = useState('');
  const [apiKeyInput, setApiKeyInput] = useState('');
  const [showApiKeyInput, setShowApiKeyInput] = useState(false);
  const wrapperRef = useRef<HTMLDivElement | null>(null);

  // Keep the form's username in sync when the store hydrates from localStorage
  // after first render (initial value of rememberedUsername may have been '').
  useEffect(() => {
    if (!username && rememberedUsername) setUsername(rememberedUsername);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rememberedUsername]);

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

  const label = connected
    ? `${home?.callsign ?? 'ON'}${hasXml ? '' : ' (no XML)'}`
    : 'Sign in to QRZ';
  const pillClass = connected
    ? 'bg-emerald-700/50 text-emerald-200 border border-emerald-600/70'
    : 'bg-amber-700/30 text-amber-200 border border-amber-500/70';

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    const ok = await login(username.trim(), password);
    if (ok) {
      setPassword('');
      setOpen(false);
    }
  }

  async function onSaveApiKey() {
    await setApiKey(apiKeyInput.trim() || null);
    setShowApiKeyInput(false);
    setApiKeyInput('');
  }

  return (
    <div ref={wrapperRef} className="relative">
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        className={`${pillClass} rounded px-2 py-0.5 text-xs hover:brightness-125`}
        title="QRZ.com session"
      >
        {connected ? '●' : '○'} {label}
      </button>
      {open && (
        <div className="absolute right-0 top-full z-40 mt-1 w-72 rounded border border-neutral-700 bg-neutral-900 p-3 text-xs shadow-lg">
          {connected ? (
            <div className="flex flex-col gap-2">
              <div className="text-neutral-300">
                Signed in as <span className="font-semibold text-emerald-300">{home?.callsign}</span>
              </div>
              {home?.grid && (
                <div className="text-neutral-400">
                  Home grid <span className="font-mono">{home.grid}</span>
                  {home.lat != null && home.lon != null && (
                    <>
                      {' · '}
                      {home.lat.toFixed(2)}, {home.lon.toFixed(2)}
                    </>
                  )}
                </div>
              )}
              <div className={hasXml ? 'text-emerald-400' : 'text-amber-400'}>
                {hasXml ? 'XML subscription active' : 'No XML subscription — lookups disabled'}
              </div>
              <div className="mt-2 border-t border-neutral-700 pt-2">
                <div className="mb-1 text-neutral-400">
                  QRZ API Key {hasApiKey && <span className="text-emerald-400">●</span>}
                </div>
                {showApiKeyInput ? (
                  <div className="flex flex-col gap-2">
                    <input
                      type="password"
                      value={apiKeyInput}
                      onChange={(e) => setApiKeyInput(e.target.value)}
                      placeholder="Enter API key"
                      className="rounded border border-neutral-700 bg-neutral-950 px-2 py-1 font-mono text-xs text-neutral-100 focus:border-emerald-600 focus:outline-none"
                    />
                    <div className="flex gap-2">
                      <button
                        type="button"
                        onClick={onSaveApiKey}
                        className="rounded bg-emerald-700 px-2 py-1 text-xs text-neutral-50 hover:bg-emerald-600"
                      >
                        Save
                      </button>
                      <button
                        type="button"
                        onClick={() => {
                          setShowApiKeyInput(false);
                          setApiKeyInput('');
                        }}
                        className="rounded border border-neutral-700 px-2 py-1 text-xs text-neutral-300 hover:bg-neutral-800"
                      >
                        Cancel
                      </button>
                    </div>
                  </div>
                ) : (
                  <button
                    type="button"
                    onClick={() => setShowApiKeyInput(true)}
                    className="text-xs text-emerald-400 hover:underline"
                  >
                    {hasApiKey ? 'Update API key' : 'Set API key'}
                  </button>
                )}
                <div className="mt-1 text-[10px] leading-tight text-neutral-500">
                  Required for publishing QSOs to QRZ logbook
                </div>
              </div>
              <button
                type="button"
                onClick={() => logout()}
                className="mt-1 self-end rounded border border-neutral-700 px-2 py-1 text-neutral-300 hover:bg-neutral-800"
              >
                Sign out
              </button>
            </div>
          ) : (
            <form onSubmit={onSubmit} className="flex flex-col gap-2">
              <label className="flex flex-col gap-1 text-neutral-400">
                QRZ username
                <input
                  type="text"
                  value={username}
                  onChange={(e) => setUsername(e.target.value)}
                  autoComplete="username"
                  spellCheck={false}
                  className="rounded border border-neutral-700 bg-neutral-950 px-2 py-1 font-mono text-neutral-100 focus:border-emerald-600 focus:outline-none"
                />
              </label>
              <label className="flex flex-col gap-1 text-neutral-400">
                Password
                <input
                  type="password"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  autoComplete="current-password"
                  className="rounded border border-neutral-700 bg-neutral-950 px-2 py-1 font-mono text-neutral-100 focus:border-emerald-600 focus:outline-none"
                />
              </label>
              {loginError && <div className="text-rose-400">{loginError}</div>}
              <button
                type="submit"
                disabled={loginInFlight || !username || !password}
                className="mt-1 rounded bg-emerald-700 px-3 py-1 text-neutral-50 hover:bg-emerald-600 disabled:opacity-50"
              >
                {loginInFlight ? 'Signing in…' : 'Sign in'}
              </button>
              <div className="text-[10px] leading-tight text-neutral-500">
                Credentials are sent to the Zeus backend and used to fetch a QRZ session key.
                Username is remembered locally; the password is not stored.
              </div>
            </form>
          )}
        </div>
      )}
    </div>
  );
}
