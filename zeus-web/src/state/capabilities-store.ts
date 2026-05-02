// SPDX-License-Identifier: GPL-2.0-or-later
//
// Capabilities store. Holds the /api/capabilities snapshot plus a derived
// `localToServer` flag that gates the VST chain editor sub-tree.
//
// Lifecycle: App.tsx fires refresh() once on mount; the backend snapshot
// is captured at startup and doesn't change at runtime, so re-fetching
// during navigation isn't necessary.

import { create } from 'zustand';

import {
  fetchCapabilities,
  isLoopbackHost,
  type Capabilities,
} from '../api/capabilities';
import { getServerBaseUrl, onServerBaseUrlChanged } from '../serverUrl';

export type CapabilitiesStoreState = {
  loaded: boolean;
  inflight: boolean;
  loadError: string | null;
  capabilities: Capabilities | null;
  // Derived from `capabilities.host === 'desktop'` OR (browser is talking
  // to a loopback host). True means plugin GUIs that open on the host's
  // display are reachable to the operator.
  localToServer: boolean;
  refresh: () => Promise<void>;
};

// Computed once and on serverUrl-change events. Browser host can change
// at runtime when a Capacitor user re-points at a different LAN server.
function computeLocality(caps: Capabilities | null): boolean {
  if (caps?.host === 'desktop') return true;
  return isLoopbackHost(getServerBaseUrl());
}

export const useCapabilitiesStore = create<CapabilitiesStoreState>((set, get) => ({
  loaded: false,
  inflight: false,
  loadError: null,
  capabilities: null,
  localToServer: false,
  refresh: async () => {
    if (get().inflight) return;
    set({ inflight: true, loadError: null });
    try {
      const caps = await fetchCapabilities();
      set({
        capabilities: caps,
        loaded: true,
        inflight: false,
        loadError: null,
        localToServer: computeLocality(caps),
      });
    } catch (err) {
      set({
        inflight: false,
        loadError: err instanceof Error ? err.message : String(err),
      });
    }
  },
}));

// Re-evaluate locality whenever the operator changes the configured
// server URL (Capacitor / standalone-host flow). Idempotent and inexpensive.
if (typeof window !== 'undefined') {
  onServerBaseUrlChanged(() => {
    const caps = useCapabilitiesStore.getState().capabilities;
    useCapabilitiesStore.setState({ localToServer: computeLocality(caps) });
  });
}
