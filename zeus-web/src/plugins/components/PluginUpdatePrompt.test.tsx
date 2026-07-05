// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';

import { PluginUpdatePrompt } from './PluginUpdatePrompt';
import type { PluginUpdate } from '../updates';

function makeRoot() {
  const container = document.createElement('div');
  document.body.appendChild(container);
  const root = createRoot(container);
  return { container, root };
}

function update(id: string, patch: Partial<PluginUpdate> = {}): PluginUpdate {
  return {
    id,
    name: `${id} Plugin`,
    installedVersion: '1.0.0',
    latestVersion: '1.1.0',
    ...patch,
  };
}

describe('PluginUpdatePrompt', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    const m = makeRoot();
    container = m.container;
    root = m.root;
  });

  afterEach(() => {
    act(() => root.unmount());
    container.remove();
  });

  it('renders a list when updates are present', () => {
    act(() => {
      root.render(
        <PluginUpdatePrompt
          updates={[
            update('demo', {
              name: 'Demo Plugin',
              installedVersion: '1.0.0',
              latestVersion: '1.2.0',
            }),
          ]}
          onDismiss={() => {}}
          onOpenPlugins={() => {}}
        />,
      );
    });

    expect(container.textContent).toContain('PLUGIN UPDATES AVAILABLE');
    expect(container.textContent).toContain('Demo Plugin');
    expect(container.textContent).toContain('1.0.0 → 1.2.0');
  });

  it('renders nothing when the update list is empty', () => {
    act(() => {
      root.render(
        <PluginUpdatePrompt
          updates={[]}
          onDismiss={() => {}}
          onOpenPlugins={() => {}}
        />,
      );
    });

    expect(container.textContent).toBe('');
  });

  it('fires onDismiss from the LATER button', () => {
    const onDismiss = vi.fn();
    act(() => {
      root.render(
        <PluginUpdatePrompt
          updates={[update('demo')]}
          onDismiss={onDismiss}
          onOpenPlugins={() => {}}
        />,
      );
    });

    const later = Array.from(
      container.querySelectorAll<HTMLButtonElement>('button'),
    ).find((button) => button.textContent?.trim() === 'LATER');
    expect(later).toBeDefined();
    act(() => {
      later!.click();
    });

    expect(onDismiss).toHaveBeenCalledTimes(1);
  });

  it('fires onOpenPlugins from the OPEN PLUGINS button', () => {
    const onOpenPlugins = vi.fn();
    act(() => {
      root.render(
        <PluginUpdatePrompt
          updates={[update('demo')]}
          onDismiss={() => {}}
          onOpenPlugins={onOpenPlugins}
        />,
      );
    });

    const openPlugins = Array.from(
      container.querySelectorAll<HTMLButtonElement>('button'),
    ).find((button) => button.textContent?.trim() === 'OPEN PLUGINS');
    expect(openPlugins).toBeDefined();
    act(() => {
      openPlugins!.click();
    });

    expect(onOpenPlugins).toHaveBeenCalledTimes(1);
  });

  it('renders an overflow line when more than five updates are present', () => {
    act(() => {
      root.render(
        <PluginUpdatePrompt
          updates={[
            update('one'),
            update('two'),
            update('three'),
            update('four'),
            update('five'),
            update('six'),
            update('seven'),
          ]}
          onDismiss={() => {}}
          onOpenPlugins={() => {}}
        />,
      );
    });

    expect(container.textContent).toContain('+2 more');
    expect(container.textContent).toContain('five Plugin');
    expect(container.textContent).not.toContain('six Plugin');
  });
});
