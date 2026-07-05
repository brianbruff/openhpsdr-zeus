// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// Regression coverage: the floating AudioSuiteWindow is opened via the TX/RX
// Suite buttons that live inside Settings → Audio Tools, so an OPEN floating
// window MUST stay visible while Settings replaces the workspace — otherwise
// clicking those buttons opens a window the operator can never see (regression
// from #1274, which hid it via display:none). Only a CLOSED floating window
// unmounts. The EMBEDDED instance IS the settings pane content, so it is always
// visible too.

/** @vitest-environment jsdom */

// harness first — installs the localStorage polyfill before any store import.
import { render, act } from '../meters/__tests__/harness';
import { describe, expect, it, beforeEach, afterEach, vi } from 'vitest';

// Keep the render light and network-free: the rack's plugin list is empty and
// the heavy inner panels are stubbed.
vi.mock('../../plugins/runtime/usePluginPanels', () => ({
  usePluginPanels: () => [],
}));
vi.mock('../AudioChainMeters', () => ({
  AudioChainMeters: () => null,
}));
vi.mock('../TxAudioProfileBar', () => ({
  TxAudioProfileBar: () => null,
}));

import { AudioSuiteWindow } from '../AudioSuiteWindow';
import { useAudioSuiteStore } from '../../state/audio-suite-store';
import { useLayoutStore } from '../../state/layout-store';

function suiteEl(container: HTMLElement, label: string): HTMLElement | null {
  return container.querySelector(`[aria-label="${label}"]`);
}

beforeEach(() => {
  // The suite's first-open effect fetches server state; make every fetch a
  // graceful no-op so no real network I/O happens in the unit test.
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => ({
      ok: false,
      status: 503,
      json: async () => ({}),
      text: async () => '',
    })),
  );
  act(() => {
    useAudioSuiteStore.setState({ txOpen: false, rxOpen: false });
    useLayoutStore.setState({ settingsViewOpen: false });
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('AudioSuiteWindow — stays visible when opened from Settings (#1274 regression)', () => {
  it('unmounts the floating window entirely when closed', () => {
    const { container, unmount } = render(<AudioSuiteWindow route="tx" />);
    expect(suiteEl(container, 'TX Audio Suite')).toBeNull();
    unmount();
  });

  it('is visible (display flex) when open and Settings is closed', () => {
    act(() => {
      useAudioSuiteStore.setState({ txOpen: true });
    });
    const { container, unmount } = render(<AudioSuiteWindow route="tx" />);
    const el = suiteEl(container, 'TX Audio Suite');
    expect(el).not.toBeNull();
    expect(el!.getAttribute('role')).toBe('dialog');
    expect(el!.style.display).toBe('flex');
    unmount();
  });

  it('stays mounted and visible (display:flex) when Settings opens — its open button lives in Settings', () => {
    act(() => {
      useAudioSuiteStore.setState({ txOpen: true });
    });
    const { container, unmount } = render(<AudioSuiteWindow route="tx" />);

    const before = suiteEl(container, 'TX Audio Suite');
    expect(before).not.toBeNull();

    act(() => {
      useLayoutStore.setState({ settingsViewOpen: true });
    });

    const during = suiteEl(container, 'TX Audio Suite');
    expect(during).toBe(before); // reused DOM node — not a remount
    expect(during!.style.display).toBe('flex'); // visible over the Settings page

    act(() => {
      useLayoutStore.setState({ settingsViewOpen: false });
    });
    const after = suiteEl(container, 'TX Audio Suite');
    expect(after).toBe(before);
    expect(after!.style.display).toBe('flex');

    unmount();
  });

  it('RX suite is visible too when opened while Settings is open', () => {
    act(() => {
      useAudioSuiteStore.setState({ rxOpen: true });
      useLayoutStore.setState({ settingsViewOpen: true });
    });
    const { container, unmount } = render(<AudioSuiteWindow route="rx" />);
    const el = suiteEl(container, 'RX Audio Suite');
    expect(el).not.toBeNull();
    expect(el!.style.display).toBe('flex');
    unmount();
  });

  it('keeps the embedded instance visible regardless of settingsViewOpen', () => {
    act(() => {
      useLayoutStore.setState({ settingsViewOpen: true });
    });
    // Embedded needs no txOpen — it is the detached popout content itself.
    const { container, unmount } = render(
      <AudioSuiteWindow route="tx" embedded />,
    );
    const el = suiteEl(container, 'TX Audio Suite');
    expect(el).not.toBeNull();
    expect(el!.getAttribute('role')).toBe('group');
    expect(el!.style.display).not.toBe('none');
    unmount();
  });

  it('lets the embedded popout instance fill its host instead of leaving a bottom gap', () => {
    const { container, unmount } = render(
      <AudioSuiteWindow route="tx" embedded />,
    );
    const el = suiteEl(container, 'TX Audio Suite');
    expect(el).not.toBeNull();
    expect(el!.style.height).toBe('100%');
    expect(el!.style.flex).toBe('1 1 auto');
    expect(el!.style.overflow).toBe('hidden');

    const body = Array.from(el!.children).find((child) => {
      const node = child as HTMLElement;
      return node.style.alignItems === 'stretch';
    }) as HTMLElement | undefined;
    expect(body).not.toBeUndefined();
    expect(body!.style.flex).toBe('1 1 0%');
    expect(body!.style.minHeight).toBe('0');
    unmount();
  });
});
