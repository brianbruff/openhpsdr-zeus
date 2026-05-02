// SPDX-License-Identifier: GPL-2.0-or-later
//
// SettingsMenu — verify the TX Audio Tools tab is gated by the VST host
// availability flag from /api/capabilities. Other tabs are static and
// don't need coverage here.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';

import { SettingsMenu } from './SettingsMenu';
import { useCapabilitiesStore } from '../state/capabilities-store';

function seed(vstAvailable: boolean) {
  useCapabilitiesStore.setState({
    loaded: true,
    inflight: false,
    loadError: null,
    capabilities: {
      host: 'server',
      platform: vstAvailable ? 'linux' : 'darwin',
      architecture: 'x64',
      version: 'test',
      features: {
        vstHost: {
          available: vstAvailable,
          reason: vstAvailable ? null : 'unsupported',
          sidecarPath: vstAvailable ? '/x' : null,
        },
      },
    },
    localToServer: false,
  });
}

describe('SettingsMenu — TX Audio Tools tab gate', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
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

  it('renders TX AUDIO TOOLS when vstHost.available is true', () => {
    seed(true);
    act(() => {
      root.render(<SettingsMenu open={true} onClose={() => {}} />);
    });
    const tabs = Array.from(
      container.querySelectorAll('[role="tablist"] button'),
    ).map((b) => b.textContent?.trim() ?? '');
    expect(tabs).toContain('TX AUDIO TOOLS');
  });

  it('hides TX AUDIO TOOLS when vstHost.available is false', () => {
    seed(false);
    act(() => {
      root.render(<SettingsMenu open={true} onClose={() => {}} />);
    });
    const tabs = Array.from(
      container.querySelectorAll('[role="tablist"] button'),
    ).map((b) => b.textContent?.trim() ?? '');
    expect(tabs).not.toContain('TX AUDIO TOOLS');
    // The other tabs are still there.
    expect(tabs).toContain('PA SETTINGS');
    expect(tabs).toContain('ABOUT');
  });
});
