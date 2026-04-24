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

import { useEffect, useRef } from 'react';
import { createPanRenderer } from '../gl/panadapter';
import { planWaterfallUpdate } from '../gl/wf-shift';
import { useDisplayStore } from '../state/display-store';
import { useDisplaySettingsStore } from '../state/display-settings-store';
import { useTxStore } from '../state/tx-store';
import { usePanTuneGesture } from '../util/use-pan-tune-gesture';
import { FreqAxis } from './FreqAxis';
import { PassbandOverlay } from './PassbandOverlay';
import { DbScale } from './DbScale';

export function Panadapter() {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);

  useEffect(() => {
    const canvas = canvasRef.current;
    const container = containerRef.current;
    if (!canvas || !container) return;

    const gl = canvas.getContext('webgl2', { antialias: true, alpha: true, premultipliedAlpha: true });
    if (!gl) {
      console.error('WebGL2 not available');
      return;
    }

    const renderer = createPanRenderer(gl);
    // Mirror the waterfall's shift state so pan and wf agree on what a VFO
    // retune does to the spectrum. On a 'shift' tick the waterfall suppresses
    // its new row and shifts the old history (doc 08 §5); the panadapter
    // shows the prior trace with the same x-offset so the two views line up.
    // On 'push'/'reset' the offset is 0 and the freshest trace is drawn.
    let lastPan: Float32Array | null = null;
    let lastCenterHz: bigint | null = null;
    let lastHzPerPixel = 0;
    let lastWidth = 0;
    let drawPan: Float32Array | null = null;
    let drawOffsetPx = 0;
    let rafHandle = 0;

    const redraw = () => {
      rafHandle = 0;
      if (!drawPan) return;
      const s = useDisplaySettingsStore.getState();
      // While keyed (MOX or TUN — server already feeds TX pixels via
      // DspPipelineService.Tick) use the TX-specific dB range so the
      // operator's RX noise-floor view is untouched. Thetis parity, see
      // TX_FIXED_DB_MIN/MAX in display-settings-store.
      const { moxOn, tunOn } = useTxStore.getState();
      const keyed = moxOn || tunOn;
      const dbMin = keyed ? s.txDbMin : s.dbMin;
      const dbMax = keyed ? s.txDbMax : s.dbMax;
      renderer.draw(drawPan, dbMin, dbMax, drawOffsetPx);
    };
    const requestRedraw = () => {
      if (rafHandle === 0) rafHandle = requestAnimationFrame(redraw);
    };

    const resize = () => {
      const { width, height } = container.getBoundingClientRect();
      const dpr = window.devicePixelRatio || 1;
      const w = Math.max(1, Math.round(width * dpr));
      const h = Math.max(1, Math.round(height * dpr));
      canvas.width = w;
      canvas.height = h;
      renderer.resize(w, h);
      requestRedraw();
    };

    const ro = new ResizeObserver(resize);
    ro.observe(container);
    resize();

    let lastSeqDrawn = -1;
    const unsub = useDisplayStore.subscribe((state) => {
      if (state.lastSeq === lastSeqDrawn) return;
      lastSeqDrawn = state.lastSeq;
      if (!state.panValid || !state.panDb) return;

      const decision = planWaterfallUpdate({
        lastCenterHz,
        lastHzPerPixel,
        lastWidth,
        nextCenterHz: state.centerHz,
        nextHzPerPixel: state.hzPerPixel,
        nextWidth: state.panDb.length,
      });

      switch (decision.kind) {
        case 'reset':
          drawPan = state.panDb;
          drawOffsetPx = 0;
          lastPan = state.panDb;
          lastCenterHz = state.centerHz;
          lastHzPerPixel = state.hzPerPixel;
          lastWidth = state.panDb.length;
          break;
        case 'push':
          drawPan = state.panDb;
          drawOffsetPx = 0;
          lastPan = state.panDb;
          // lastCenterHz unchanged so sub-pixel retunes accumulate.
          break;
        case 'shift':
          // Show the last pushed frame with the accumulated integer-pixel
          // offset the waterfall has applied to its history — the post-shift
          // top row and this trace land the same carriers in the same
          // columns. Offset accumulates across consecutive shift ticks and
          // resets on the next push (which updates lastPan to fresh data).
          drawPan = lastPan ?? state.panDb;
          drawOffsetPx += decision.shiftPx;
          lastCenterHz = decision.residualCenterHz;
          break;
      }

      requestRedraw();
    });

    // Repaint on auto-range updates so palette changes apply without waiting
    // for the next server frame.
    const unsubSettings = useDisplaySettingsStore.subscribe(() => {
      requestRedraw();
    });

    // Repaint when MOX / TUN flips so the RX-vs-TX dB range swap is
    // reflected immediately, even if no fresh pan frame arrived yet.
    const unsubTx = useTxStore.subscribe(() => {
      requestRedraw();
    });

    return () => {
      unsub();
      unsubSettings();
      unsubTx();
      ro.disconnect();
      if (rafHandle !== 0) cancelAnimationFrame(rafHandle);
      renderer.dispose();
    };
  }, []);

  usePanTuneGesture(canvasRef);

  return (
    <div
      ref={containerRef}
      className="spectrum-canvas"
      style={{
        position: 'relative',
        minHeight: 0,
        width: '100%',
        height: '100%',
        background: 'var(--spec-bg)',
      }}
    >
      <canvas ref={canvasRef} style={{ position: 'absolute', inset: 0, width: '100%', height: '100%' }} />
      <PassbandOverlay />
      <FreqAxis />
      <DbScale />
    </div>
  );
}
