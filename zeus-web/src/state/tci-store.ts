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

import { create } from 'zustand';
import {
  getTciStatus,
  postTciConfig,
  testTciPort,
  type TciConfig,
  type TciStatus,
  type TciTestResult,
} from '../api/tci';

// Persist the enabled/bindAddress/port the user has chosen so the settings
// panel stays usable across reloads. The backend persists this to disk and
// is the source of truth; this is just the form-default memory.
const CONFIG_STORAGE_KEY = 'zeus.tci.config';
const DEFAULT_CONFIG: TciConfig = {
  enabled: false,
  bindAddress: '127.0.0.1',
  port: 40001,
};

function readSavedConfig(): TciConfig {
  try {
    if (typeof localStorage === 'undefined') return DEFAULT_CONFIG;
    const raw = localStorage.getItem(CONFIG_STORAGE_KEY);
    if (!raw) return DEFAULT_CONFIG;
    const parsed = JSON.parse(raw) as Partial<TciConfig>;
    return {
      enabled: Boolean(parsed.enabled),
      bindAddress: typeof parsed.bindAddress === 'string' && parsed.bindAddress ? parsed.bindAddress : DEFAULT_CONFIG.bindAddress,
      port: typeof parsed.port === 'number' && parsed.port > 0 ? parsed.port : DEFAULT_CONFIG.port,
    };
  } catch {
    return DEFAULT_CONFIG;
  }
}

function writeSavedConfig(cfg: TciConfig): void {
  try {
    if (typeof localStorage === 'undefined') return;
    localStorage.setItem(CONFIG_STORAGE_KEY, JSON.stringify(cfg));
  } catch {
    /* quota — silent */
  }
}

export type TciStoreState = {
  config: TciConfig;
  status: TciStatus | null;
  testInFlight: boolean;
  lastTestResult: TciTestResult | null;

  refreshStatus: () => Promise<void>;
  saveConfig: (cfg: TciConfig) => Promise<TciStatus>;
  test: (bindAddress: string, port: number) => Promise<TciTestResult>;
};

export const useTciStore = create<TciStoreState>((set) => ({
  config: readSavedConfig(),
  status: null,
  testInFlight: false,
  lastTestResult: null,

  refreshStatus: async () => {
    try {
      const status = await getTciStatus();
      set({ status });
    } catch {
      /* transient — next poll recovers */
    }
  },

  saveConfig: async (cfg) => {
    writeSavedConfig(cfg);
    const status = await postTciConfig(cfg);
    set({ config: cfg, status });
    return status;
  },

  test: async (bindAddress, port) => {
    set({ testInFlight: true, lastTestResult: null });
    const result = await testTciPort(bindAddress, port);
    set({ testInFlight: false, lastTestResult: result });
    return result;
  },
}));

// Kick off an initial status probe at module load, then poll at 2 s while the
// page is alive. When TCI config changes (requires restart), this updates the
// UI to show the RequiresRestart flag and current client count.
if (typeof window !== 'undefined') {
  void useTciStore.getState().refreshStatus();
  window.setInterval(() => {
    void useTciStore.getState().refreshStatus();
  }, 2000);

  // Push the saved config to the backend on first load so the service knows
  // what the user wants for next restart. Backend persists this to disk.
  const initial = useTciStore.getState().config;
  void useTciStore.getState().saveConfig(initial);
}
