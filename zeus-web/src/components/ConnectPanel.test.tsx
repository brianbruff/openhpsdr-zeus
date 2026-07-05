/** @vitest-environment jsdom */

import { createElement } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { act, render } from './meters/__tests__/harness';
import { useConnectionStore } from '../state/connection-store';
import { useConnectStore } from '../state/connect-store';
import type { RadioInfoDto, RadioStateDto } from '../api/client';
import { ConnectPanel } from './ConnectPanel';

const apiMocks = vi.hoisted(() => ({
  connectP3: vi.fn(),
  fetchRadios: vi.fn(),
  fetchProtocol3SidecarStatus: vi.fn(),
  fetchState: vi.fn(),
  listPrefsDatabases: vi.fn(),
}));

const audioMocks = vi.hoisted(() => ({
  start: vi.fn(),
}));

vi.mock('../api/client', async () => {
  const actual = await vi.importActual<typeof import('../api/client')>('../api/client');
  return {
    ...actual,
    connectP3: apiMocks.connectP3,
    fetchRadios: apiMocks.fetchRadios,
    fetchProtocol3SidecarStatus: apiMocks.fetchProtocol3SidecarStatus,
    fetchState: apiMocks.fetchState,
    listPrefsDatabases: apiMocks.listPrefsDatabases,
  };
});

vi.mock('../audio/audio-client', () => ({
  getAudioClient: () => ({ start: audioMocks.start }),
}));

function stateSnapshot(): RadioStateDto {
  return useConnectionStore.getState() as unknown as RadioStateDto;
}

async function flushEffects() {
  await act(async () => {
    await Promise.resolve();
    await Promise.resolve();
  });
}

function exactButtons(container: HTMLElement, label: string): HTMLButtonElement[] {
  return Array.from(container.querySelectorAll<HTMLButtonElement>('button'))
    .filter((button) => button.textContent?.trim() === label);
}

describe('ConnectPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useConnectionStore.setState({
      status: 'Disconnected',
      endpoint: null,
      inflight: false,
      lastConnectedEndpoint: null,
      connectedProtocol: null,
      wisdomPhase: 'ready',
      wisdomStatus: '',
    });
    useConnectStore.setState({
      mode: 'discover',
      savedEndpoints: [],
      lastConnectedId: undefined,
      manualFormDefaults: {
        ip: '',
        port: 1024,
        protocol: 'P1',
        sampleRate: 192_000,
        board: 'Auto',
        label: '',
      },
    });
    apiMocks.connectP3.mockResolvedValue({});
    apiMocks.fetchProtocol3SidecarStatus.mockResolvedValue({
      configured: true,
      running: false,
      diagnosticsUrl: null,
      sidecarOutput: null,
    });
    apiMocks.fetchState.mockImplementation(async () => stateSnapshot());
    apiMocks.listPrefsDatabases.mockResolvedValue({
      activeRelativePath: 'zeus-prefs.db',
      databases: [
        {
          name: 'Default',
          relativePath: 'zeus-prefs.db',
          sizeBytes: 0,
          modifiedUtcMs: 0,
          active: true,
        },
      ],
    });
  });

  it('shows and routes Connect for a Protocol 3 discovery row', async () => {
    const p3Radio: RadioInfoDto = {
      macAddress: '',
      ipAddress: '192.168.1.25',
      boardId: 'G2',
      firmwareVersion: '0x024001BF',
      busy: false,
      details: {
        protocol: 'P3',
        protocol3Available: 'true',
        protocol3Port: '1030',
      },
    };
    apiMocks.fetchRadios.mockResolvedValue([p3Radio]);

    const { container, unmount } = render(createElement(ConnectPanel));
    await flushEffects();

    const connect = exactButtons(container, 'Connect')[0];
    if (!connect) throw new Error('expected a Connect button for the Protocol 3 row');
    expect(connect.title).toContain('Protocol 3');

    await act(async () => {
      connect.click();
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(apiMocks.connectP3).toHaveBeenCalledWith({
      endpoint: '192.168.1.25:1030',
      sampleRate: 1_536_000,
    });
    expect(audioMocks.start).toHaveBeenCalled();
    unmount();
  });

  it('accepts an already-connected Protocol 3 session for the same radio IP', async () => {
    const p3Radio: RadioInfoDto = {
      macAddress: '',
      ipAddress: '192.168.1.25',
      boardId: 'G2',
      firmwareVersion: '0x024001BF',
      busy: false,
      details: {
        protocol: 'P3',
        protocol3Available: 'true',
        protocol3Port: '1030',
      },
    };
    apiMocks.fetchRadios.mockResolvedValue([p3Radio]);
    apiMocks.connectP3.mockRejectedValueOnce(new Error('Already connected. Disconnect first.'));
    apiMocks.fetchState
      .mockImplementationOnce(async () => stateSnapshot())
      .mockImplementationOnce(async () => ({
        ...stateSnapshot(),
        status: 'Connected',
        endpoint: '192.168.1.25:1024',
        connectedProtocol: 'P3',
      }));

    const { container, unmount } = render(createElement(ConnectPanel));
    await flushEffects();

    const connect = exactButtons(container, 'Connect')[0];
    if (!connect) throw new Error('expected a Connect button for the Protocol 3 row');

    await act(async () => {
      connect.click();
      await Promise.resolve();
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(apiMocks.connectP3).toHaveBeenCalledWith({
      endpoint: '192.168.1.25:1030',
      sampleRate: 1_536_000,
    });
    expect(useConnectionStore.getState().status).toBe('Connected');
    expect(useConnectionStore.getState().endpoint).toBe('192.168.1.25:1024');
    expect(container.textContent).not.toContain('Already connected. Disconnect first.');
    expect(audioMocks.start).toHaveBeenCalled();
    unmount();
  });

  it('does not offer takeover for a busy Protocol 3 discovery row', async () => {
    const p3Radio: RadioInfoDto = {
      macAddress: '',
      ipAddress: '192.168.1.25',
      boardId: 'G2',
      firmwareVersion: '0x024001BF',
      busy: true,
      details: {
        protocol: 'P3',
        protocol3Available: 'true',
        protocol3Port: '1030',
      },
    };
    apiMocks.fetchRadios.mockResolvedValue([p3Radio]);

    const { container, unmount } = render(createElement(ConnectPanel));
    await flushEffects();

    expect(exactButtons(container, 'Take over')).toHaveLength(0);
    expect(exactButtons(container, 'Connect')).toHaveLength(1);
    unmount();
  });

  it('blocks a Protocol 3 discovery row when the local sidecar is unconfigured', async () => {
    const p3Radio: RadioInfoDto = {
      macAddress: '',
      ipAddress: '192.168.1.25',
      boardId: 'G2',
      firmwareVersion: '0x024001BF',
      busy: false,
      details: {
        protocol: 'P3',
        protocol3Available: 'true',
        protocol3Port: '1030',
      },
    };
    apiMocks.fetchRadios.mockResolvedValue([p3Radio]);
    apiMocks.fetchProtocol3SidecarStatus.mockResolvedValue({
      configured: false,
      running: false,
      diagnosticsUrl: null,
      sidecarOutput: null,
    });

    const { container, unmount } = render(createElement(ConnectPanel));
    await flushEffects();

    const connect = exactButtons(container, 'Connect')[0];
    if (!connect) throw new Error('expected a Connect button for the Protocol 3 row');
    expect(connect.disabled).toBe(false);
    expect(connect.title).toContain('local N9DSP sidecar configuration');

    await act(async () => {
      connect.click();
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(apiMocks.connectP3).not.toHaveBeenCalled();
    expect(container.textContent).toContain('local N9DSP sidecar configuration');
    unmount();
  });
});
