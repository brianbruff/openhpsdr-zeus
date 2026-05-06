// SPDX-License-Identifier: GPL-2.0-or-later
//
// SettingsMenu — verify the TX Audio Tools tab is always present and that
// the VST host submenu inside it is gated by /api/capabilities. CFC is
// WDSP-driven and must remain visible regardless of the sidecar.

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

describe('SettingsMenu — TX Audio Tools', () => {
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

  it('always renders the TX AUDIO TOOLS tab', () => {
    seed(false);
    act(() => {
      root.render(<SettingsMenu open={true} onClose={() => {}} />);
    });
    const tabs = Array.from(
      container.querySelectorAll('[role="tablist"] button'),
    ).map((b) => b.textContent?.trim() ?? '');
    expect(tabs).toContain('TX AUDIO TOOLS');
  });

  it('shows CFC and the VST host submenu when vstHost.available is true', () => {
    seed(true);
    act(() => {
      root.render(
        <SettingsMenu open={true} onClose={() => {}} initialTab="tx-audio" />,
      );
    });
    expect(container.textContent).toContain('Continuous Frequency Compressor');
    expect(container.textContent).toContain('VST Host');
  });

  it('shows CFC but hides the VST host submenu when vstHost.available is false', () => {
    seed(false);
    act(() => {
      root.render(
        <SettingsMenu open={true} onClose={() => {}} initialTab="tx-audio" />,
      );
    });
    expect(container.textContent).toContain('Continuous Frequency Compressor');
    expect(container.textContent).not.toContain('VST Host');
  });
});
