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
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { MSG_TYPE_MIC_PCM, sendMicPcm } from './ws-client';

// sendMicPcm reads a module-scoped `activeWs` that's only set by startRealtime.
// For tests we stub the WebSocket constructor so we can capture what was sent.

class MockWebSocket {
  static OPEN = 1;
  static instances: MockWebSocket[] = [];
  readyState = MockWebSocket.OPEN;
  sent: ArrayBuffer[] = [];
  binaryType = 'arraybuffer';
  // ws-client assigns these callbacks; we keep them harmless.
  onopen: ((ev: unknown) => void) | null = null;
  onclose: ((ev: unknown) => void) | null = null;
  onerror: ((ev: unknown) => void) | null = null;
  onmessage: ((ev: unknown) => void) | null = null;
  constructor() {
    MockWebSocket.instances.push(this);
  }
  send(data: ArrayBuffer) {
    this.sent.push(data);
  }
  close() { /* no-op */ }
}

describe('sendMicPcm', () => {
  let origWs: typeof WebSocket;

  beforeEach(() => {
    MockWebSocket.instances.length = 0;
    origWs = globalThis.WebSocket;
    // @ts-expect-error — structural match is fine for the surface we use.
    globalThis.WebSocket = MockWebSocket;
  });

  afterEach(() => {
    globalThis.WebSocket = origWs;
    vi.restoreAllMocks();
  });

  it('no-ops when no active WS (does not throw)', () => {
    const samples = new Float32Array(960);
    expect(() => sendMicPcm(samples)).not.toThrow();
  });

  it('wire format is [0x20] + 960 × f32le when WS is open', async () => {
    // startRealtime is the only path that sets the module-internal activeWs.
    const { startRealtime } = await import('./ws-client');
    const stop = startRealtime('/ws');
    try {
      const ws = MockWebSocket.instances[0];
      expect(ws).toBeDefined();
      if (ws?.onopen) ws.onopen({} as unknown);

      const samples = new Float32Array(960);
      // Distinguishable pattern so we can verify LE ordering in the wire buffer.
      for (let i = 0; i < 960; i++) samples[i] = i * 0.001;

      sendMicPcm(samples);

      expect(ws?.sent.length).toBe(1);
      const sentBuf = ws!.sent[0];
      if (!(sentBuf instanceof ArrayBuffer)) throw new Error('expected ArrayBuffer');
      expect(sentBuf.byteLength).toBe(1 + 960 * 4);
      const view = new DataView(sentBuf);
      expect(view.getUint8(0)).toBe(MSG_TYPE_MIC_PCM);
      for (let i = 0; i < 960; i++) {
        expect(view.getFloat32(1 + i * 4, true)).toBeCloseTo(i * 0.001, 6);
      }
    } finally {
      stop();
    }
  });

  it('drops blocks with wrong length', async () => {
    const { startRealtime } = await import('./ws-client');
    const stop = startRealtime('/ws');
    try {
      const ws = MockWebSocket.instances[0];
      if (ws?.onopen) ws.onopen({} as unknown);
      sendMicPcm(new Float32Array(128));
      expect(ws?.sent.length).toBe(0);
    } finally {
      stop();
    }
  });
});
