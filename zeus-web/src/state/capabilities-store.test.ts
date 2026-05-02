// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

// Node 25 ships a stub `localStorage` global that lacks the Storage API
// methods, shadowing jsdom's implementation. Install a minimal in-memory
// stand-in BEFORE importing the module under test so its first read sees
// a working API. Mirrors the shim in serverUrl.test.ts.
function installLocalStorageShim() {
  const store = new Map<string, string>();
  const shim: Storage = {
    getItem: (k) => (store.has(k) ? (store.get(k) as string) : null),
    setItem: (k, v) => void store.set(k, String(v)),
    removeItem: (k) => void store.delete(k),
    clear: () => store.clear(),
    key: (i) => Array.from(store.keys())[i] ?? null,
    get length() {
      return store.size;
    },
  };
  Object.defineProperty(globalThis, 'localStorage', {
    configurable: true,
    value: shim,
  });
  Object.defineProperty(window, 'localStorage', {
    configurable: true,
    value: shim,
  });
}
installLocalStorageShim();

const { useCapabilitiesStore } = await import('./capabilities-store');
const { setServerBaseUrl } = await import('../serverUrl');

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

function resetStore() {
  useCapabilitiesStore.setState({
    loaded: false,
    inflight: false,
    loadError: null,
    capabilities: null,
    localToServer: false,
  });
}

describe('useCapabilitiesStore', () => {
  beforeEach(() => {
    resetStore();
    setServerBaseUrl('');
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    setServerBaseUrl('');
  });

  it('hydrates from /api/capabilities and derives localToServer from desktop host', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      jsonResponse({
        host: 'desktop',
        platform: 'linux',
        architecture: 'x64',
        version: '0.6.0',
        features: {
          vstHost: {
            available: true,
            reason: null,
            sidecarPath: '/usr/local/bin/zeus-plughost',
          },
        },
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    await useCapabilitiesStore.getState().refresh();
    const s = useCapabilitiesStore.getState();
    expect(s.loaded).toBe(true);
    expect(s.capabilities?.host).toBe('desktop');
    expect(s.capabilities?.features.vstHost.available).toBe(true);
    // Desktop host always = local regardless of browser hostname.
    expect(s.localToServer).toBe(true);
  });

  it('marks server-host + LAN base URL as remote', async () => {
    setServerBaseUrl('http://192.168.1.23:6060');
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      jsonResponse({
        host: 'server',
        platform: 'linux',
        architecture: 'x64',
        version: '0.6.0',
        features: {
          vstHost: { available: true, reason: null, sidecarPath: '/x' },
        },
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    await useCapabilitiesStore.getState().refresh();
    expect(useCapabilitiesStore.getState().localToServer).toBe(false);
  });

  it('records loadError when the fetch fails', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue(
        new Response('boom', { status: 500 }),
      ),
    );

    await useCapabilitiesStore.getState().refresh();
    const s = useCapabilitiesStore.getState();
    expect(s.loaded).toBe(false);
    expect(s.loadError).not.toBeNull();
  });
});
