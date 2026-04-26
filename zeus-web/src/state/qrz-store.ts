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

import { create } from 'zustand';
import { qrzLogin, qrzLogout, qrzLookup, qrzStatus, qrzSetApiKey, type QrzStation, type QrzStatus } from '../api/qrz';
import { ApiError } from '../api/client';

const USERNAME_STORAGE_KEY = 'zeus.qrz.username';

function readSavedUsername(): string {
  try {
    if (typeof localStorage === 'undefined') return '';
    return localStorage.getItem(USERNAME_STORAGE_KEY) ?? '';
  } catch {
    return '';
  }
}

function writeSavedUsername(user: string): void {
  try {
    if (typeof localStorage === 'undefined') return;
    if (user) localStorage.setItem(USERNAME_STORAGE_KEY, user);
    else localStorage.removeItem(USERNAME_STORAGE_KEY);
  } catch {
    /* quota / private mode — accept silently */
  }
}

export type QrzStoreState = {
  // Session state mirrored from the backend; null means we haven't checked yet.
  connected: boolean;
  hasXmlSubscription: boolean;
  hasApiKey: boolean;
  home: QrzStation | null;
  // Remembered username (password is never persisted).
  rememberedUsername: string;
  // Last lookup result, shown in QrzCard / used to render map target.
  lastLookup: QrzStation | null;
  // Transient UI state for async ops.
  loginInFlight: boolean;
  lookupInFlight: boolean;
  loginError: string | null;
  lookupError: string | null;

  // Actions.
  refreshStatus: () => Promise<void>;
  login: (username: string, password: string) => Promise<boolean>;
  logout: () => Promise<void>;
  lookup: (callsign: string) => Promise<QrzStation | null>;
  clearLookup: () => void;
  setApiKey: (apiKey: string | null) => Promise<void>;
};

function applyStatus(status: QrzStatus): Partial<QrzStoreState> {
  return {
    connected: status.connected,
    hasXmlSubscription: status.hasXmlSubscription,
    hasApiKey: status.hasApiKey,
    home: status.home,
    loginError: status.error,
  };
}

export const useQrzStore = create<QrzStoreState>((set) => ({
  connected: false,
  hasXmlSubscription: false,
  hasApiKey: false,
  home: null,
  rememberedUsername: readSavedUsername(),
  lastLookup: null,
  loginInFlight: false,
  lookupInFlight: false,
  loginError: null,
  lookupError: null,

  refreshStatus: async () => {
    try {
      const status = await qrzStatus();
      set(applyStatus(status));
    } catch {
      // Status is a read-only sanity probe; leave state as-is on transient fetch failures.
    }
  },

  login: async (username, password) => {
    set({ loginInFlight: true, loginError: null });
    try {
      const status = await qrzLogin(username, password);
      writeSavedUsername(username);
      set({ ...applyStatus(status), rememberedUsername: username, loginInFlight: false });
      return status.connected;
    } catch (err) {
      const msg = err instanceof ApiError ? err.message : String(err);
      set({ loginInFlight: false, loginError: msg, connected: false, home: null });
      return false;
    }
  },

  logout: async () => {
    try {
      await qrzLogout();
    } finally {
      set({ connected: false, hasXmlSubscription: false, home: null, lastLookup: null, loginError: null, lookupError: null });
    }
  },

  lookup: async (callsign) => {
    const trimmed = callsign.trim().toUpperCase();
    if (!trimmed) return null;
    set({ lookupInFlight: true, lookupError: null });
    try {
      const station = await qrzLookup(trimmed);
      set({ lastLookup: station, lookupInFlight: false });
      return station;
    } catch (err) {
      const msg = err instanceof ApiError ? err.message : String(err);
      set({ lookupInFlight: false, lookupError: msg });
      return null;
    }
  },

  clearLookup: () => set({ lastLookup: null, lookupError: null }),

  setApiKey: async (apiKey) => {
    try {
      const status = await qrzSetApiKey(apiKey);
      set(applyStatus(status));
    } catch (err) {
      const msg = err instanceof ApiError ? err.message : String(err);
      set({ loginError: msg });
    }
  },
}));

// Kick off a status probe at module load so reconnected sessions (backend still has
// a session key from an earlier page) rehydrate without the user having to log in.
void useQrzStore.getState().refreshStatus();
