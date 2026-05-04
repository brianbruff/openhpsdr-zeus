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

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { useVstHostStore } from './vst-host-store';
import { VST_HOST_SLOT_COUNT } from '../api/vst-host';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

function emptySlots() {
  return Array.from({ length: VST_HOST_SLOT_COUNT }, (_, i) => ({
    index: i,
    plugin: null,
    bypass: false,
    parameterCount: 0,
  }));
}

function resetStore() {
  useVstHostStore.setState({
    loaded: false,
    inflight: false,
    loadError: null,
    master: {
      masterEnabled: false,
      isRunning: false,
      slots: emptySlots(),
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

describe('vst-host-store', () => {
  beforeEach(resetStore);
  afterEach(() => vi.unstubAllGlobals());

  it('refresh() loads state from /api/plughost/state', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      jsonResponse({
        masterEnabled: true,
        isRunning: true,
        slots: [
          {
            index: 0,
            plugin: { name: 'EQ', vendor: 'Acme', version: '1.0' },
            bypass: false,
            parameterCount: 4,
          },
        ],
        customSearchPaths: ['/opt/vst3'],
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    await useVstHostStore.getState().refresh();
    const s = useVstHostStore.getState();
    expect(s.loaded).toBe(true);
    expect(s.master.masterEnabled).toBe(true);
    expect(s.master.isRunning).toBe(true);
    // First slot from server preserved with plugin info; remainder padded.
    expect(s.master.slots).toHaveLength(VST_HOST_SLOT_COUNT);
    expect(s.master.slots[0]?.plugin?.name).toBe('EQ');
    expect(s.master.slots[1]?.plugin).toBeNull();
    expect(s.master.customSearchPaths).toEqual(['/opt/vst3']);
  });

  it('setMasterEnabled() optimistically updates and confirms via server', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      jsonResponse({
        masterEnabled: true,
        isRunning: true,
        slots: emptySlots(),
        customSearchPaths: [],
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    const promise = useVstHostStore.getState().setMasterEnabled(true);
    // Optimistic flip should be visible before the POST resolves.
    expect(useVstHostStore.getState().master.masterEnabled).toBe(true);
    await promise;

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/plughost/master');
    expect(init?.method).toBe('POST');
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({ enabled: true });

    expect(useVstHostStore.getState().master.isRunning).toBe(true);
  });

  it('setMasterEnabled() rolls back on server error', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue(
        jsonResponse({ error: 'sidecar refused to start' }, 409),
      ),
    );

    await useVstHostStore.getState().setMasterEnabled(true);
    const s = useVstHostStore.getState();
    expect(s.master.masterEnabled).toBe(false);
    expect(s.loadError).toBe('sidecar refused to start');
  });

  it('applyEvent("chainEnabledChanged:1") flips the master flag', () => {
    useVstHostStore.getState().applyEvent('chainEnabledChanged:1');
    expect(useVstHostStore.getState().master.masterEnabled).toBe(true);
    useVstHostStore.getState().applyEvent('chainEnabledChanged:0');
    expect(useVstHostStore.getState().master.masterEnabled).toBe(false);
  });

  it('applyEvent("slotStateChanged:N") triggers a slot re-fetch', () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      jsonResponse({
        index: 3,
        plugin: { name: 'Gate', vendor: 'V', version: '2.0', path: '/p' },
        bypass: false,
        parameters: [],
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    useVstHostStore.getState().applyEvent('slotStateChanged:3');
    // applyEvent dispatches the fetch but doesn't await; verify the URL.
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/plughost/slots/3',
      expect.anything(),
    );
  });

  it('applyEvent("parameterChanged:N:ID:VAL") patches the cached parameter', () => {
    useVstHostStore.setState({
      slotParameters: new Map([
        [
          0,
          [
            {
              id: 7,
              name: 'Gain',
              units: 'dB',
              defaultValue: 0.5,
              currentValue: 0.5,
              stepCount: 0,
              flags: 0,
            },
          ],
        ],
      ]),
    });
    useVstHostStore.getState().applyEvent('parameterChanged:0:7:0.875000');
    const list = useVstHostStore.getState().slotParameters.get(0);
    expect(list?.[0]?.currentValue).toBeCloseTo(0.875, 3);
  });

  it('applyEvent("sidecarExited:N") notifies and disables master flag locally', () => {
    useVstHostStore.setState({
      master: {
        ...useVstHostStore.getState().master,
        masterEnabled: true,
        isRunning: true,
      },
    });
    useVstHostStore.getState().applyEvent('sidecarExited:1');
    const s = useVstHostStore.getState();
    expect(s.master.isRunning).toBe(false);
    expect(s.master.masterEnabled).toBe(false);
    expect(s.notice).toMatch(/Plugin host stopped/);
  });

  it('applyEvent("slotEditorResized:N:W:H") updates editor geometry', () => {
    useVstHostStore.getState().applyEvent('slotEditorResized:2:633:225');
    const ed = useVstHostStore.getState().editors.get(2);
    expect(ed).toEqual({ width: 633, height: 225, open: true });
    useVstHostStore.getState().applyEvent('slotEditorClosed:2');
    expect(useVstHostStore.getState().editors.get(2)?.open).toBe(false);
  });

  it('refreshCatalog(false) hits /api/plughost/catalog', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      jsonResponse({
        plugins: [
          {
            filePath: '/p/eq.vst3',
            displayName: 'EQ',
            format: 'VST3',
            platform: 'Linux',
            bitness: 'X64',
          },
        ],
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    await useVstHostStore.getState().refreshCatalog(false);
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/plughost/catalog',
      expect.anything(),
    );
    const s = useVstHostStore.getState();
    expect(s.catalogLoaded).toBe(true);
    expect(s.catalog).toHaveLength(1);
    expect(s.catalog[0]?.displayName).toBe('EQ');
  });

  it('refreshCatalog(true) appends ?rescan=true', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      jsonResponse({ plugins: [] }),
    );
    vi.stubGlobal('fetch', fetchMock);

    await useVstHostStore.getState().refreshCatalog(true);
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/plughost/catalog?rescan=true',
      expect.anything(),
    );
  });

  it('addSearchPath() POSTs and updates customSearchPaths from echo', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      jsonResponse({ added: true, paths: ['/foo'] }),
    );
    vi.stubGlobal('fetch', fetchMock);

    await useVstHostStore.getState().addSearchPath('/foo');
    expect(useVstHostStore.getState().master.customSearchPaths).toEqual([
      '/foo',
    ]);
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/plughost/searchPaths');
    expect(init?.method).toBe('POST');
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({ path: '/foo' });
  });

  it('removeSearchPath() DELETEs with url-encoded path', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      jsonResponse({ removed: true, paths: [] }),
    );
    vi.stubGlobal('fetch', fetchMock);

    await useVstHostStore.getState().removeSearchPath('/with space');
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/plughost/searchPaths?path=%2Fwith%20space');
    expect(init?.method).toBe('DELETE');
  });

  it('setSlotBypass() optimistically toggles and reconciles on success', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      jsonResponse({ index: 1, bypass: true }),
    );
    vi.stubGlobal('fetch', fetchMock);

    const promise = useVstHostStore.getState().setSlotBypass(1, true);
    // Optimistic flip is immediate.
    expect(useVstHostStore.getState().master.slots[1]?.bypass).toBe(true);
    await promise;
    const [url] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/plughost/slots/1/bypass');
  });
});
