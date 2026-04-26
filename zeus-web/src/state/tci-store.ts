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
import { getTciSettings, setTciSettings, type TciSettings, type TciSettingsSetRequest } from '../api/tci';

type TciStore = {
  settings: TciSettings | null;
  loading: boolean;
  saving: boolean;
  error: string | null;
  load: () => Promise<void>;
  save: (req: TciSettingsSetRequest) => Promise<void>;
};

const DEFAULT_SETTINGS: TciSettings = {
  enabled: false,
  port: 40001,
  bindAddress: '127.0.0.1',
  portBound: false,
  portError: null,
  clientCount: 0,
  restartRequired: false,
};

export const useTciStore = create<TciStore>((set) => ({
  settings: null,
  loading: false,
  saving: false,
  error: null,

  load: async () => {
    set({ loading: true, error: null });
    try {
      const settings = await getTciSettings();
      set({ settings, loading: false });
    } catch (e) {
      set({ loading: false, error: String(e), settings: DEFAULT_SETTINGS });
    }
  },

  save: async (req) => {
    set({ saving: true, error: null });
    try {
      const settings = await setTciSettings(req);
      set({ settings, saving: false });
    } catch (e) {
      set({ saving: false, error: String(e) });
    }
  },
}));
