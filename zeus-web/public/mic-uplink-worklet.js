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

// AudioWorkletProcessor that accumulates mono mic samples into 960-sample
// (20 ms @ 48 kHz) blocks and transfers each block to the main thread over
// this.port. Matches the server-side MSG_TYPE_MIC_PCM = 0x20 contract.
//
// Lives in public/ (not src/) so Vite serves it verbatim at /mic-uplink-worklet.js
// without ES-module transpilation. AudioWorklet loader (addModule) needs a URL
// to a standalone JS file with no imports; a bundled worker chunk won't work.
//
// Sample rate: we rely on the caller creating the AudioContext with
// sampleRate: 48000 so the implicit `sampleRate` global inside this processor
// equals 48000. If that contract breaks we'd need a resampler here.

const BLOCK_SAMPLES = 960;

class MicUplinkProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this._buf = new Float32Array(BLOCK_SAMPLES);
    this._fill = 0;
  }

  process(inputs) {
    const input = inputs[0];
    if (!input || input.length === 0) return true;
    const ch = input[0];
    if (!ch) return true;

    let srcIdx = 0;
    while (srcIdx < ch.length) {
      const room = BLOCK_SAMPLES - this._fill;
      const take = Math.min(room, ch.length - srcIdx);
      this._buf.set(ch.subarray(srcIdx, srcIdx + take), this._fill);
      this._fill += take;
      srcIdx += take;

      if (this._fill === BLOCK_SAMPLES) {
        // Transfer the buffer to the main thread (zero-copy) and allocate
        // a fresh one. New-alloc cost is ~192 KB/s at 50 Hz, acceptable.
        // Peak-level computation runs here (once per 20 ms block) so the
        // main thread can drive a mic meter at exactly the uplink cadence.
        const out = this._buf;
        let peak = 0;
        for (let i = 0; i < BLOCK_SAMPLES; i++) {
          const a = out[i] < 0 ? -out[i] : out[i];
          if (a > peak) peak = a;
        }
        this._buf = new Float32Array(BLOCK_SAMPLES);
        this._fill = 0;
        this.port.postMessage({ samples: out, peak }, [out.buffer]);
      }
    }
    return true;
  }
}

registerProcessor('mic-uplink', MicUplinkProcessor);
