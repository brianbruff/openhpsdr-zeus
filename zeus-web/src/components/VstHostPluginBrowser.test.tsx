// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';

import { VstHostPluginBrowser } from './VstHostPluginBrowser';
import { useVstHostStore } from '../state/vst-host-store';
import { VST_HOST_SLOT_COUNT } from '../api/vst-host';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

function resetStore(catalogPreloaded = true) {
  useVstHostStore.setState({
    loaded: true,
    inflight: false,
    loadError: null,
    master: {
      masterEnabled: true,
      isRunning: true,
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
    catalog: catalogPreloaded
      ? [
          {
            filePath: '/p/eq.vst3',
            bundlePath: null,
            displayName: 'AwesomeEQ',
            format: 'Vst3',
            platform: 'Linux',
            bitness: 'X64',
          },
          {
            filePath: '/p/comp.vst3',
            bundlePath: null,
            displayName: 'BigComp',
            format: 'Vst3',
            platform: 'Linux',
            bitness: 'X64',
          },
        ]
      : [],
    catalogLoaded: catalogPreloaded,
    catalogInflight: false,
    catalogError: null,
    notice: null,
  });
}

describe('VstHostPluginBrowser', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    resetStore();
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

  it('lists the catalog when open', () => {
    act(() => {
      root.render(
        <VstHostPluginBrowser
          open={true}
          targetSlot={0}
          onClose={() => {}}
        />,
      );
    });
    expect(container.textContent).toContain('AwesomeEQ');
    expect(container.textContent).toContain('BigComp');
    // With a target slot, each row gets a "LOAD INTO SLOT N" button.
    const loadButtons = Array.from(container.querySelectorAll('button')).filter(
      (b) => b.textContent?.startsWith('LOAD INTO SLOT'),
    );
    expect(loadButtons.length).toBe(2);
  });

  it('renders nothing when closed', () => {
    act(() => {
      root.render(
        <VstHostPluginBrowser
          open={false}
          targetSlot={null}
          onClose={() => {}}
        />,
      );
    });
    expect(container.textContent).toBe('');
  });

  it('search filter narrows results', () => {
    act(() => {
      root.render(
        <VstHostPluginBrowser
          open={true}
          targetSlot={0}
          onClose={() => {}}
        />,
      );
    });
    const input = container.querySelector(
      'input[type="search"]',
    ) as HTMLInputElement;
    expect(input).toBeTruthy();
    act(() => {
      // React's synthetic events expect a native input event, but the
      // controlled input here just needs `onChange` to fire — set the
      // value and dispatch an input event.
      const proto = Object.getPrototypeOf(input);
      const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
      setter?.call(input, 'Awe');
      input.dispatchEvent(new Event('input', { bubbles: true }));
    });
    expect(container.textContent).toContain('AwesomeEQ');
    expect(container.textContent).not.toContain('BigComp');
  });

  it('clicking LOAD INTO SLOT triggers /api/plughost/slots/N/load and closes', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      // First call: load
      .mockResolvedValueOnce(jsonResponse({ index: 0, plugin: null }))
      // Second call: refreshSlot
      .mockResolvedValueOnce(
        jsonResponse({
          index: 0,
          plugin: { name: 'AwesomeEQ', vendor: 'V', version: '1', path: '/p/eq.vst3' },
          bypass: false,
          parameters: [],
        }),
      );
    vi.stubGlobal('fetch', fetchMock);

    const onClose = vi.fn();
    act(() => {
      root.render(
        <VstHostPluginBrowser
          open={true}
          targetSlot={0}
          onClose={onClose}
        />,
      );
    });

    const firstLoadBtn = Array.from(container.querySelectorAll('button')).find(
      (b) => b.textContent?.trim() === 'LOAD INTO SLOT 1',
    ) as HTMLButtonElement;
    expect(firstLoadBtn).toBeTruthy();

    await act(async () => {
      firstLoadBtn.click();
    });

    const urls = fetchMock.mock.calls.map((c) => c[0]);
    expect(urls).toContain('/api/plughost/slots/0/load');
    expect(onClose).toHaveBeenCalled();
  });
});
