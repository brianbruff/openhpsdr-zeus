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

import { create } from 'zustand';
import {
  getRotatorStatus,
  postRotatorConfig,
  setRotatorAz,
  stopRotator,
  testRotator,
  type RotctldConfig,
  type RotctldStatus,
  type RotctldTestResult,
} from '../api/rotator';

// Persist the host/port/enabled/interval the user has chosen so the pill
// stays usable across reloads. The backend is the source of truth while
// connected; this is just the form-default memory.
const CONFIG_STORAGE_KEY = 'zeus.rotator.config';
const DEFAULT_CONFIG: RotctldConfig = {
  enabled: false,
  host: '127.0.0.1',
  port: 4533,
  pollingIntervalMs: 500,
};

function readSavedConfig(): RotctldConfig {
  try {
    if (typeof localStorage === 'undefined') return DEFAULT_CONFIG;
    const raw = localStorage.getItem(CONFIG_STORAGE_KEY);
    if (!raw) return DEFAULT_CONFIG;
    const parsed = JSON.parse(raw) as Partial<RotctldConfig>;
    return {
      enabled: Boolean(parsed.enabled),
      host: typeof parsed.host === 'string' && parsed.host ? parsed.host : DEFAULT_CONFIG.host,
      port: typeof parsed.port === 'number' && parsed.port > 0 ? parsed.port : DEFAULT_CONFIG.port,
      pollingIntervalMs:
        typeof parsed.pollingIntervalMs === 'number' && parsed.pollingIntervalMs > 0
          ? parsed.pollingIntervalMs
          : DEFAULT_CONFIG.pollingIntervalMs,
    };
  } catch {
    return DEFAULT_CONFIG;
  }
}

function writeSavedConfig(cfg: RotctldConfig): void {
  try {
    if (typeof localStorage === 'undefined') return;
    localStorage.setItem(CONFIG_STORAGE_KEY, JSON.stringify(cfg));
  } catch {
    /* quota — silent */
  }
}

export type RotatorStoreState = {
  config: RotctldConfig;
  status: RotctldStatus | null;
  testInFlight: boolean;
  lastTestResult: RotctldTestResult | null;

  refreshStatus: () => Promise<void>;
  saveConfig: (cfg: RotctldConfig) => Promise<RotctldStatus>;
  setAzimuth: (az: number) => Promise<RotctldStatus | null>;
  stop: () => Promise<void>;
  test: (host: string, port: number) => Promise<RotctldTestResult>;
};

export const useRotatorStore = create<RotatorStoreState>((set) => ({
  config: readSavedConfig(),
  status: null,
  testInFlight: false,
  lastTestResult: null,

  refreshStatus: async () => {
    try {
      const status = await getRotatorStatus();
      set({ status });
    } catch {
      /* transient — next poll recovers */
    }
  },

  saveConfig: async (cfg) => {
    writeSavedConfig(cfg);
    const status = await postRotatorConfig(cfg);
    set({ config: cfg, status });
    return status;
  },

  setAzimuth: async (az) => {
    try {
      const status = await setRotatorAz(az);
      set({ status });
      return status;
    } catch {
      return null;
    }
  },

  stop: async () => {
    try {
      const status = await stopRotator();
      set({ status });
    } catch {
      /* ignore */
    }
  },

  test: async (host, port) => {
    set({ testInFlight: true, lastTestResult: null });
    const result = await testRotator(host, port);
    set({ testInFlight: false, lastTestResult: result });
    return result;
  },
}));

// Kick off an initial status probe at module load, then poll at 1 s while the
// page is alive. When rotctld is enabled and connected, this keeps the pill's
// current-az readout in sync without the UI having to subscribe to a WS stream.
if (typeof window !== 'undefined') {
  void useRotatorStore.getState().refreshStatus();
  window.setInterval(() => {
    void useRotatorStore.getState().refreshStatus();
  }, 1000);

  // Push the saved config to the backend on first load so the service is in the
  // same state the UI will render (Enabled flag, host/port). Backend state is
  // in-memory only — a restart resets to defaults.
  const initial = useRotatorStore.getState().config;
  if (initial.enabled) {
    void useRotatorStore.getState().saveConfig(initial);
  }
}
