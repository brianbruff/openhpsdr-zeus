// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';

import { VstHostSlotRow } from './VstHostSlotRow';
import { useVstHostStore } from '../state/vst-host-store';
import { VST_HOST_SLOT_COUNT } from '../api/vst-host';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

function withSlotPatched(index: number, patch: Record<string, unknown>) {
  const cur = useVstHostStore.getState().master;
  useVstHostStore.setState({
    master: {
      ...cur,
      slots: cur.slots.map((s, i) =>
        i === index ? { ...s, ...patch } : s,
      ),
    },
  });
}

function resetStore() {
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
    catalog: [],
    catalogLoaded: false,
    catalogInflight: false,
    catalogError: null,
    notice: null,
  });
}

describe('VstHostSlotRow', () => {
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

  it('empty slot exposes a LOAD button that requests plugin browser', () => {
    const onRequestLoad = vi.fn();
    act(() => {
      root.render(
        <VstHostSlotRow index={0} disabled={false} onRequestLoad={onRequestLoad} />,
      );
    });
    expect(container.textContent).toContain('Empty');

    const loadBtn = Array.from(container.querySelectorAll('button')).find(
      (b) => b.textContent?.trim() === 'LOAD',
    ) as HTMLButtonElement;
    expect(loadBtn).toBeTruthy();

    act(() => {
      loadBtn.click();
    });
    expect(onRequestLoad).toHaveBeenCalledWith(0);
  });

  it('loaded slot renders BYPASS / EDIT / UNLOAD controls', () => {
    withSlotPatched(2, {
      plugin: {
        name: 'TestEQ',
        vendor: 'Acme',
        version: '1.0',
        path: '/p/eq.vst3',
      },
      parameterCount: 3,
    });

    act(() => {
      root.render(
        <VstHostSlotRow index={2} disabled={false} onRequestLoad={() => {}} />,
      );
    });

    expect(container.textContent).toContain('TestEQ');
    expect(container.textContent).toContain('Acme');
    const labels = Array.from(container.querySelectorAll('button')).map(
      (b) => b.textContent?.trim() ?? '',
    );
    expect(labels).toContain('EDIT');
    expect(labels).toContain('UNLOAD');
    // Bypass label sits inside a <label>, easier to assert via outer text.
    expect(container.textContent).toContain('Bypass');
  });

  it('remote mode hides LOAD on empty slots', () => {
    act(() => {
      root.render(
        <VstHostSlotRow
          index={0}
          disabled={false}
          remote={true}
          onRequestLoad={() => {}}
        />,
      );
    });
    const labels = Array.from(container.querySelectorAll('button')).map(
      (b) => b.textContent?.trim() ?? '',
    );
    expect(labels).not.toContain('LOAD');
  });

  it('remote mode keeps Bypass but hides EDIT/UNLOAD on loaded slots', () => {
    withSlotPatched(1, {
      plugin: {
        name: 'TestEQ',
        vendor: 'Acme',
        version: '1.0',
        path: '/p/eq.vst3',
      },
      parameterCount: 3,
    });

    act(() => {
      root.render(
        <VstHostSlotRow
          index={1}
          disabled={false}
          remote={true}
          onRequestLoad={() => {}}
        />,
      );
    });

    expect(container.textContent).toContain('TestEQ');
    expect(container.textContent).toContain('Bypass');
    const labels = Array.from(container.querySelectorAll('button')).map(
      (b) => b.textContent?.trim() ?? '',
    );
    expect(labels).not.toContain('EDIT');
    expect(labels).not.toContain('UNLOAD');
  });

  it('clicking UNLOAD POSTs /api/plughost/slots/N/unload', async () => {
    withSlotPatched(0, {
      plugin: { name: 'EQ', vendor: 'V', version: '1', path: '/p' },
    });
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(jsonResponse({ index: 0 }));
    vi.stubGlobal('fetch', fetchMock);

    act(() => {
      root.render(
        <VstHostSlotRow index={0} disabled={false} onRequestLoad={() => {}} />,
      );
    });

    const unloadBtn = Array.from(container.querySelectorAll('button')).find(
      (b) => b.textContent?.trim() === 'UNLOAD',
    ) as HTMLButtonElement;
    await act(async () => {
      unloadBtn.click();
    });

    const urls = fetchMock.mock.calls.map((c) => c[0]);
    expect(urls).toContain('/api/plughost/slots/0/unload');
  });
});
