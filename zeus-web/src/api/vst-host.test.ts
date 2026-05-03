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

import { afterEach, describe, expect, it, vi } from 'vitest';

import {
  addVstHostSearchPath,
  fetchVstHostCatalog,
  fetchVstHostSlot,
  fetchVstHostState,
  hideVstHostSlotEditor,
  loadVstHostSlot,
  parseVstHostState,
  removeVstHostSearchPath,
  setVstHostMaster,
  setVstHostSlotBypass,
  setVstHostSlotParameter,
  showVstHostSlotEditor,
  unloadVstHostSlot,
  VST_HOST_SLOT_COUNT,
  VstHostApiError,
} from './vst-host';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

describe('parseVstHostState', () => {
  it('pads slots to VST_HOST_SLOT_COUNT', () => {
    const s = parseVstHostState({
      masterEnabled: true,
      isRunning: true,
      slots: [{ index: 0, plugin: null, bypass: false, parameterCount: 0 }],
      customSearchPaths: ['/x'],
    });
    expect(s.slots).toHaveLength(VST_HOST_SLOT_COUNT);
    expect(s.masterEnabled).toBe(true);
    expect(s.customSearchPaths).toEqual(['/x']);
  });

  it('returns empty defaults for malformed payload', () => {
    const s = parseVstHostState({});
    expect(s.masterEnabled).toBe(false);
    expect(s.isRunning).toBe(false);
    expect(s.slots).toHaveLength(VST_HOST_SLOT_COUNT);
    expect(s.customSearchPaths).toEqual([]);
  });
});

describe('vst-host REST helpers', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('fetchVstHostState() hits /api/plughost/state', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      jsonResponse({
        masterEnabled: false,
        isRunning: false,
        slots: [],
        customSearchPaths: [],
      }),
    );
    vi.stubGlobal('fetch', fetchMock);
    await fetchVstHostState();
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/plughost/state',
      expect.anything(),
    );
  });

  it('fetchVstHostCatalog(true) appends ?rescan=true', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(jsonResponse({ plugins: [] }));
    vi.stubGlobal('fetch', fetchMock);
    await fetchVstHostCatalog(true);
    expect(fetchMock.mock.calls[0]?.[0]).toBe(
      '/api/plughost/catalog?rescan=true',
    );
  });

  it('setVstHostMaster() POSTs { enabled }', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      jsonResponse({
        masterEnabled: true,
        isRunning: true,
        slots: [],
        customSearchPaths: [],
      }),
    );
    vi.stubGlobal('fetch', fetchMock);
    await setVstHostMaster(true);
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/plughost/master');
    expect(init?.method).toBe('POST');
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({ enabled: true });
  });

  it('fetchVstHostSlot(idx) returns parameters list', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      jsonResponse({
        index: 2,
        plugin: { name: 'EQ', vendor: 'V', version: '1', path: '/p' },
        bypass: false,
        parameters: [
          {
            id: 9,
            name: 'Gain',
            units: 'dB',
            defaultValue: 0.5,
            currentValue: 0.5,
            stepCount: 0,
            flags: 2,
          },
        ],
      }),
    );
    vi.stubGlobal('fetch', fetchMock);
    const detail = await fetchVstHostSlot(2);
    expect(detail.index).toBe(2);
    expect(detail.parameters).toHaveLength(1);
    expect(detail.parameters[0]?.name).toBe('Gain');
    expect(fetchMock.mock.calls[0]?.[0]).toBe('/api/plughost/slots/2');
  });

  it('loadVstHostSlot() POSTs { path }', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(jsonResponse({ index: 0, plugin: null }));
    vi.stubGlobal('fetch', fetchMock);
    await loadVstHostSlot(0, '/abs/eq.vst3');
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/plughost/slots/0/load');
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({
      path: '/abs/eq.vst3',
    });
  });

  it('unloadVstHostSlot() POSTs to /unload', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(jsonResponse({ index: 5 }));
    vi.stubGlobal('fetch', fetchMock);
    const out = await unloadVstHostSlot(5);
    expect(out.index).toBe(5);
    expect(fetchMock.mock.calls[0]?.[0]).toBe('/api/plughost/slots/5/unload');
  });

  it('setVstHostSlotBypass() POSTs { bypass }', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(jsonResponse({ index: 1, bypass: true }));
    vi.stubGlobal('fetch', fetchMock);
    await setVstHostSlotBypass(1, true);
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/plughost/slots/1/bypass');
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({ bypass: true });
  });

  it('setVstHostSlotParameter() POSTs normalized value', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      jsonResponse({ index: 0, paramId: 7, value: 0.75 }),
    );
    vi.stubGlobal('fetch', fetchMock);
    await setVstHostSlotParameter(0, 7, 0.75);
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('/api/plughost/slots/0/parameters/7');
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({ value: 0.75 });
  });

  it('showVstHostSlotEditor() returns geometry from server', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(jsonResponse({ index: 3, width: 633, height: 225 }));
    vi.stubGlobal('fetch', fetchMock);
    const out = await showVstHostSlotEditor(3);
    expect(out).toEqual({ index: 3, width: 633, height: 225 });
    expect(fetchMock.mock.calls[0]?.[0]).toBe(
      '/api/plughost/slots/3/editor/show',
    );
  });

  it('hideVstHostSlotEditor() returns closed flag', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(jsonResponse({ index: 3, closed: true }));
    vi.stubGlobal('fetch', fetchMock);
    const out = await hideVstHostSlotEditor(3);
    expect(out).toEqual({ index: 3, closed: true });
  });

  it('addVstHostSearchPath() POSTs { path }', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      jsonResponse({ added: true, paths: ['/foo'] }),
    );
    vi.stubGlobal('fetch', fetchMock);
    const out = await addVstHostSearchPath('/foo');
    expect(out.paths).toEqual(['/foo']);
    expect(fetchMock.mock.calls[0]?.[0]).toBe('/api/plughost/searchPaths');
  });

  it('removeVstHostSearchPath() encodes path into the query', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      jsonResponse({ removed: true, paths: [] }),
    );
    vi.stubGlobal('fetch', fetchMock);
    await removeVstHostSearchPath('/a b');
    expect(fetchMock.mock.calls[0]?.[0]).toBe(
      '/api/plughost/searchPaths?path=%2Fa%20b',
    );
  });

  it('throws VstHostApiError with server-provided error string on 400', async () => {
    vi.stubGlobal(
      'fetch',
      vi
        .fn<typeof fetch>()
        .mockResolvedValue(
          jsonResponse({ error: 'directory does not exist' }, 400),
        ),
    );
    await expect(addVstHostSearchPath('/nope')).rejects.toMatchObject({
      name: 'VstHostApiError',
      status: 400,
      message: 'directory does not exist',
    });
  });

  it('throws VstHostApiError with detail string on ProblemDetails 409', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue(
        jsonResponse({ detail: 'sidecar refused' }, 409),
      ),
    );
    await expect(setVstHostMaster(true)).rejects.toMatchObject({
      name: 'VstHostApiError',
      status: 409,
      message: 'sidecar refused',
    });
  });

  it('falls back to status text when error body is non-JSON', async () => {
    vi.stubGlobal(
      'fetch',
      vi
        .fn<typeof fetch>()
        .mockResolvedValue(
          new Response('oops', { status: 500, statusText: 'Internal' }),
        ),
    );
    try {
      await setVstHostMaster(true);
      expect.fail('expected throw');
    } catch (e) {
      expect(e).toBeInstanceOf(VstHostApiError);
      expect((e as VstHostApiError).status).toBe(500);
    }
  });
});
