// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

/** @vitest-environment jsdom */

import { describe, expect, it } from 'vitest';
import { render, act } from '../../../components/meters/__tests__/harness';
import { MetersPanelInner } from '../MetersPanel';
import {
  EMPTY_METERS_CONFIG,
  type MetersPanelConfig,
} from '../../../components/meters/metersConfig';
import { MeterReadingId } from '../../../components/meters/meterCatalog';
import { createElement } from 'react';

interface State {
  current: MetersPanelConfig;
}

function setup(initial: MetersPanelConfig = EMPTY_METERS_CONFIG) {
  const state: State = { current: initial };
  const setConfig = (next: MetersPanelConfig) => {
    state.current = next;
    rerender();
  };
  const { container, rerender: rawRerender, unmount } = render(
    createElement(MetersPanelInner, {
      config: state.current,
      setConfig,
    }),
  );
  function rerender() {
    rawRerender(
      createElement(MetersPanelInner, {
        config: state.current,
        setConfig,
      }),
    );
  }
  return { state, container, rerender, unmount };
}

describe('MetersPanel', () => {
  it('renders the empty-state message when no widgets are configured', () => {
    const { container, unmount } = setup();
    const empty = container.querySelector('[data-testid="meters-empty-state"]');
    expect(empty).not.toBeNull();
    expect(empty?.textContent ?? '').toContain('No meters yet');
    unmount();
  });

  it('toggles the Library drawer via the gear button', () => {
    const { container, unmount } = setup();
    const drawer = container.querySelector(
      '[data-testid="meters-library-drawer"]',
    ) as HTMLElement | null;
    expect(drawer).not.toBeNull();
    expect(drawer?.getAttribute('aria-hidden')).toBe('true');

    const gear = container.querySelector(
      '[data-testid="meters-library-toggle"]',
    ) as HTMLButtonElement | null;
    expect(gear).not.toBeNull();
    act(() => {
      gear?.click();
    });
    expect(drawer?.getAttribute('aria-hidden')).toBe('false');

    act(() => {
      gear?.click();
    });
    expect(drawer?.getAttribute('aria-hidden')).toBe('true');
    unmount();
  });

  it('inserts a widget when a Library entry is clicked', () => {
    const { state, container, unmount } = setup();
    const gear = container.querySelector(
      '[data-testid="meters-library-toggle"]',
    ) as HTMLButtonElement | null;
    act(() => {
      gear?.click();
    });

    const fwdEntry = container.querySelector(
      `[data-meter-id="${MeterReadingId.TxFwdWatts}"]`,
    ) as HTMLButtonElement | null;
    expect(fwdEntry).not.toBeNull();
    act(() => {
      fwdEntry?.click();
    });

    expect(state.current.widgets.length).toBe(1);
    expect(state.current.widgets[0]?.reading).toBe(MeterReadingId.TxFwdWatts);
    expect(state.current.widgets[0]?.kind).toBe('dial'); // catalog default
    unmount();
  });

  it('removes a widget when the Settings drawer remove button is clicked', () => {
    const initial: MetersPanelConfig = {
      schemaVersion: 1,
      widgets: [
        {
          uid: 'w1',
          reading: MeterReadingId.TxSwr,
          kind: 'digital',
          settings: {},
        },
      ],
    };
    const { state, container, unmount } = setup(initial);

    // Click the widget body to select it (selection opens the Settings drawer).
    const widgetEl = container.querySelector(
      '[data-widget-uid="w1"]',
    ) as HTMLElement | null;
    expect(widgetEl).not.toBeNull();
    act(() => {
      widgetEl?.click();
    });

    const removeBtn = container.querySelector(
      '[data-testid="meters-remove-widget"]',
    ) as HTMLButtonElement | null;
    expect(removeBtn).not.toBeNull();
    act(() => {
      removeBtn?.click();
    });

    expect(state.current.widgets.length).toBe(0);
    unmount();
  });
});
