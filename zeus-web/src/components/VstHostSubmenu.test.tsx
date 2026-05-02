// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Smoke tests for VstHostSubmenu — uses React 19's act() + createRoot
// directly because the project doesn't have @testing-library installed.
// These tests verify mount/unmount and master-toggle interaction
// against a mocked fetch; richer component assertions stay in the
// vst-host-store unit tests where they're cheaper to maintain.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';

import { VstHostSubmenu } from './VstHostSubmenu';
import { useCapabilitiesStore } from '../state/capabilities-store';
import { useVstHostStore } from '../state/vst-host-store';
import { VST_HOST_SLOT_COUNT } from '../api/vst-host';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

function emptyStateBody() {
  return {
    masterEnabled: false,
    isRunning: false,
    slots: Array.from({ length: VST_HOST_SLOT_COUNT }, (_, i) => ({
      index: i,
      plugin: null,
      bypass: false,
      parameterCount: 0,
    })),
    customSearchPaths: [],
  };
}

function resetStore() {
  useVstHostStore.setState({
    loaded: true,
    inflight: false,
    loadError: null,
    master: {
      masterEnabled: false,
      isRunning: false,
      slots: Array.from({ length: VST_HOST_SLOT_COUNT }, (_, i) => ({
        index: i,
        plugin: null,
        bypass: false,
        parameterCount: 0,
      })),
      customSearchPaths: [],
    },
    slotParameters: new Map(),
    slotErrors: new Map(),
    editors: new Map(),
    catalog: [],
    catalogLoaded: false,
    catalogInflight: false,
    catalogError: null,
    notice: null,
  });
}

function seedCapabilities(localToServer: boolean) {
  useCapabilitiesStore.setState({
    loaded: true,
    inflight: false,
    loadError: null,
    capabilities: {
      host: localToServer ? 'desktop' : 'server',
      platform: 'linux',
      architecture: 'x64',
      version: 'test',
      features: {
        vstHost: { available: true, reason: null, sidecarPath: '/x' },
      },
    },
    localToServer,
  });
}

describe('VstHostSubmenu', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    resetStore();
    seedCapabilities(true);
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
  });

  afterEach(() => {
    act(() => {
      root.unmount();
    });
    container.remove();
    vi.unstubAllGlobals();
  });

  it('renders without crashing and shows the master toggle', () => {
    act(() => {
      root.render(<VstHostSubmenu />);
    });
    // The "VST Chain" label sits next to the master checkbox.
    expect(container.textContent).toContain('VST Chain');
    expect(container.textContent).toContain('VST Host');
    expect(container.textContent).toContain('Enable VST chain to load plugins.');
    // 8 slot rows render even with master OFF (visible-but-disabled).
    const slotLabels = container.querySelectorAll('[aria-label^="VST slot "]');
    expect(slotLabels.length).toBe(VST_HOST_SLOT_COUNT);
  });

  it('shows BROWSE PLUGINS when local to the server (desktop host)', () => {
    act(() => {
      root.render(<VstHostSubmenu />);
    });
    expect(container.textContent).toContain('BROWSE PLUGINS');
    // The remote-mode notice must not be visible.
    expect(container.textContent).not.toContain(
      'editable only from the server console',
    );
  });

  it('hides BROWSE PLUGINS and shows the remote-mode notice when not local', () => {
    seedCapabilities(false);
    act(() => {
      root.render(<VstHostSubmenu />);
    });
    expect(container.textContent).not.toContain('BROWSE PLUGINS');
    expect(container.textContent).toContain(
      'editable only from the server console',
    );
    // The master toggle stays available even when remote.
    const checkbox = container.querySelector(
      'input[type="checkbox"][aria-label="Toggle VST chain"]',
    );
    expect(checkbox).toBeTruthy();
  });

  it('toggling the master checkbox calls /api/plughost/master', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      jsonResponse({ ...emptyStateBody(), masterEnabled: true, isRunning: true }),
    );
    vi.stubGlobal('fetch', fetchMock);

    act(() => {
      root.render(<VstHostSubmenu />);
    });

    const checkbox = container.querySelector(
      'input[type="checkbox"][aria-label="Toggle VST chain"]',
    ) as HTMLInputElement;
    expect(checkbox).toBeTruthy();

    await act(async () => {
      checkbox.click();
    });

    const urls = fetchMock.mock.calls.map((c) => c[0]);
    expect(urls).toContain('/api/plughost/master');
    expect(useVstHostStore.getState().master.masterEnabled).toBe(true);
  });
});
