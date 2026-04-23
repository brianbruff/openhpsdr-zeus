// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Filter visualization PRD §3.2.1 — mini-panadapter inside the advanced
// filter ribbon. Renders a 10 kHz-span spectrum strip centered on the VFO,
// with a translucent amber passband overlay and edge-drag handles.
//
// Implementation note: uses Canvas 2D rather than a second WebGL context.
// At the ribbon's small target size (~640×80 px) the 2D path comfortably
// hits the <2 ms/frame target on integrated GPUs without the complexity of
// scissor-clipping the main canvas. If a future profile shows 2D as the
// bottleneck we can swap in a shared-context GL renderer without changing
// the component's surface.

import { useEffect, useRef } from 'react';
import { useDisplayStore } from '../../state/display-store';
import { useConnectionStore } from '../../state/connection-store';
import { setFilter } from '../../api/client';

const RIBBON_SPAN_HZ = 10_000;
const DB_FLOOR = -130;
const DB_CEIL = -30;
const DRAG_MIN_INTERVAL_MS = 50;
const EDGE_HIT_PX = 6;

type DragMode = 'lo' | 'hi' | 'inside';

function presetIsFixed(name: string | null): boolean {
  return !!name && /^F([1-9]|10)$/.test(name);
}

export function FilterMiniPan() {
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const dragRef = useRef<{
    mode: DragMode;
    rect: DOMRect;
    activeSlot: string;
    startLoHz: number;
    startHiHz: number;
    startX: number;
    pendingLo: number;
    pendingHi: number;
    lastWriteAt: number;
    flushTimer: number | null;
    pointerId: number;
  } | null>(null);

  // Subscribe imperatively to avoid a React re-render per frame — we paint
  // from display-store snapshots whenever the frame seq ticks.
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext('2d', { alpha: false });
    if (!ctx) return;

    let rafHandle = 0;
    let lastSeq = -1;

    const draw = () => {
      rafHandle = 0;
      const d = useDisplayStore.getState();
      const c = useConnectionStore.getState();
      if (d.lastSeq === lastSeq) return;
      lastSeq = d.lastSeq;

      // Size canvas to its CSS box. Guarded against zero-sized offscreen
      // mounts during the first paint.
      const dpr = window.devicePixelRatio || 1;
      const cssW = canvas.clientWidth;
      const cssH = canvas.clientHeight;
      if (cssW <= 0 || cssH <= 0) return;
      const w = Math.floor(cssW * dpr);
      const h = Math.floor(cssH * dpr);
      if (canvas.width !== w) canvas.width = w;
      if (canvas.height !== h) canvas.height = h;

      // Fill background.
      ctx.fillStyle = '#000';
      ctx.fillRect(0, 0, w, h);

      const panDb = d.panDb;
      const binsPerHz = d.hzPerPixel > 0 ? 1 / d.hzPerPixel : 0;

      if (panDb && binsPerHz > 0) {
        const vfo = Number(d.centerHz);
        const loHz = vfo - RIBBON_SPAN_HZ / 2;

        // Map full pan span to bins; the ribbon extracts a 10 kHz window
        // centered on the VFO. If the main pan span is narrower than the
        // ribbon's 10 kHz we render what we have — the rest stays floor.
        const fullSpanHz = panDb.length * d.hzPerPixel;
        const fullStartHz = vfo - fullSpanHz / 2;
        const binStart = Math.max(0, Math.floor((loHz - fullStartHz) * binsPerHz));
        const binEnd = Math.min(panDb.length, Math.ceil((loHz + RIBBON_SPAN_HZ - fullStartHz) * binsPerHz));

        // Decimate to w px, max-of-bins per pixel column.
        ctx.strokeStyle = '#FFA028';
        ctx.lineWidth = 1 * dpr;
        ctx.beginPath();
        const bins = binEnd - binStart;
        if (bins > 0) {
          for (let x = 0; x < w; x++) {
            const b0 = binStart + Math.floor((x * bins) / w);
            const b1 = binStart + Math.floor(((x + 1) * bins) / w);
            let peak = -Infinity;
            for (let i = b0; i < b1; i++) {
              const v = panDb[i] ?? DB_FLOOR;
              if (v > peak) peak = v;
            }
            if (peak === -Infinity) peak = DB_FLOOR;
            const norm = (peak - DB_FLOOR) / (DB_CEIL - DB_FLOOR);
            const y = h - Math.max(0, Math.min(1, norm)) * h;
            if (x === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
          }
          ctx.stroke();
        }

        // Passband rectangle (amber fill).
        const passLeftPx = ((c.filterLowHz + RIBBON_SPAN_HZ / 2) / RIBBON_SPAN_HZ) * w;
        const passRightPx = ((c.filterHighHz + RIBBON_SPAN_HZ / 2) / RIBBON_SPAN_HZ) * w;
        const passW = Math.max(0, passRightPx - passLeftPx);
        ctx.fillStyle = 'rgba(255, 160, 40, 0.18)';
        ctx.fillRect(passLeftPx, 0, passW, h);
        ctx.strokeStyle = 'rgba(255, 160, 40, 0.85)';
        ctx.lineWidth = 1 * dpr;
        ctx.beginPath();
        ctx.moveTo(Math.round(passLeftPx) + 0.5, 0);
        ctx.lineTo(Math.round(passLeftPx) + 0.5, h);
        ctx.moveTo(Math.round(passRightPx) + 0.5, 0);
        ctx.lineTo(Math.round(passRightPx) + 0.5, h);
        ctx.stroke();

        // Corner handles (triangular amber marks).
        const handle = 6 * dpr;
        ctx.fillStyle = 'rgba(255, 160, 40, 0.9)';
        ctx.beginPath();
        ctx.moveTo(passLeftPx, 0);
        ctx.lineTo(passLeftPx + handle, 0);
        ctx.lineTo(passLeftPx, handle);
        ctx.closePath();
        ctx.fill();
        ctx.beginPath();
        ctx.moveTo(passRightPx, 0);
        ctx.lineTo(passRightPx - handle, 0);
        ctx.lineTo(passRightPx, handle);
        ctx.closePath();
        ctx.fill();

        // VFO center tick.
        ctx.strokeStyle = 'rgba(255, 160, 40, 0.35)';
        ctx.beginPath();
        ctx.moveTo(w / 2, 0);
        ctx.lineTo(w / 2, h);
        ctx.stroke();
      }
    };

    // Schedule repaint whenever display-store updates (frame arrival or
    // resize). Also repaint when the filter edges move optimistically.
    const unsubDisplay = useDisplayStore.subscribe(() => {
      if (rafHandle === 0) rafHandle = requestAnimationFrame(draw);
    });
    const unsubConn = useConnectionStore.subscribe((s, p) => {
      if (s.filterLowHz !== p.filterLowHz || s.filterHighHz !== p.filterHighHz) {
        lastSeq = -1; // force redraw — same FFT, different overlay
        if (rafHandle === 0) rafHandle = requestAnimationFrame(draw);
      }
    });

    const ro = new ResizeObserver(() => {
      lastSeq = -1;
      if (rafHandle === 0) rafHandle = requestAnimationFrame(draw);
    });
    ro.observe(canvas);

    // Initial paint
    rafHandle = requestAnimationFrame(draw);

    return () => {
      if (rafHandle !== 0) cancelAnimationFrame(rafHandle);
      unsubDisplay();
      unsubConn();
      ro.disconnect();
    };
  }, []);

  // Drag logic — identical rate-limiting strategy to FilterEdgeDrag.
  const flushPending = () => {
    const d = dragRef.current;
    if (!d) return;
    d.flushTimer = null;
    d.lastWriteAt = performance.now();
    setFilter(d.pendingLo, d.pendingHi, d.activeSlot).catch(() => {});
  };

  const schedule = () => {
    const d = dragRef.current;
    if (!d) return;
    const now = performance.now();
    const elapsed = now - d.lastWriteAt;
    if (elapsed >= DRAG_MIN_INTERVAL_MS) {
      flushPending();
    } else if (d.flushTimer == null) {
      d.flushTimer = window.setTimeout(flushPending, DRAG_MIN_INTERVAL_MS - elapsed);
    }
  };

  const onPointerDown = (e: React.PointerEvent<HTMLCanvasElement>) => {
    if (e.button !== 0) return;
    const canvas = canvasRef.current;
    if (!canvas) return;
    const rect = canvas.getBoundingClientRect();
    if (rect.width <= 0) return;

    const c = useConnectionStore.getState();
    const passLeftPx = ((c.filterLowHz + RIBBON_SPAN_HZ / 2) / RIBBON_SPAN_HZ) * rect.width;
    const passRightPx = ((c.filterHighHz + RIBBON_SPAN_HZ / 2) / RIBBON_SPAN_HZ) * rect.width;
    const relX = e.clientX - rect.left;

    let mode: DragMode;
    if (Math.abs(relX - passLeftPx) <= EDGE_HIT_PX) mode = 'lo';
    else if (Math.abs(relX - passRightPx) <= EDGE_HIT_PX) mode = 'hi';
    else if (relX > passLeftPx && relX < passRightPx) mode = 'inside';
    else return;

    e.preventDefault();
    try { canvas.setPointerCapture(e.pointerId); } catch { /* ok */ }

    const activeSlot = presetIsFixed(c.filterPresetName) || !c.filterPresetName ? 'VAR1' : c.filterPresetName;

    dragRef.current = {
      mode,
      rect,
      activeSlot,
      startLoHz: c.filterLowHz,
      startHiHz: c.filterHighHz,
      startX: e.clientX,
      pendingLo: c.filterLowHz,
      pendingHi: c.filterHighHz,
      lastWriteAt: 0,
      flushTimer: null,
      pointerId: e.pointerId,
    };

    if (activeSlot !== c.filterPresetName) {
      useConnectionStore.setState({ filterPresetName: activeSlot });
    }
  };

  const onPointerMove = (e: React.PointerEvent<HTMLCanvasElement>) => {
    const d = dragRef.current;
    if (!d || e.pointerId !== d.pointerId) return;
    e.stopPropagation();

    const hzPerPx = RIBBON_SPAN_HZ / d.rect.width;

    let loHz = d.startLoHz;
    let hiHz = d.startHiHz;
    if (d.mode === 'lo') {
      const relX = e.clientX - d.rect.left;
      loHz = Math.round(relX * hzPerPx - RIBBON_SPAN_HZ / 2);
      if (loHz > d.startHiHz - 50) loHz = d.startHiHz - 50;
    } else if (d.mode === 'hi') {
      const relX = e.clientX - d.rect.left;
      hiHz = Math.round(relX * hzPerPx - RIBBON_SPAN_HZ / 2);
      if (hiHz < d.startLoHz + 50) hiHz = d.startLoHz + 50;
    } else {
      const dxHz = Math.round((e.clientX - d.startX) * hzPerPx);
      loHz = d.startLoHz + dxHz;
      hiHz = d.startHiHz + dxHz;
    }

    d.pendingLo = loHz;
    d.pendingHi = hiHz;
    useConnectionStore.setState({ filterLowHz: loHz, filterHighHz: hiHz });
    schedule();
  };

  const onPointerUp = (e: React.PointerEvent<HTMLCanvasElement>) => {
    const d = dragRef.current;
    if (!d || e.pointerId !== d.pointerId) return;
    e.stopPropagation();

    const canvas = canvasRef.current;
    if (canvas && canvas.hasPointerCapture(e.pointerId)) {
      try { canvas.releasePointerCapture(e.pointerId); } catch { /* ok */ }
    }
    if (d.flushTimer != null) {
      clearTimeout(d.flushTimer);
      d.flushTimer = null;
    }
    const lo = d.pendingLo;
    const hi = d.pendingHi;
    const slot = d.activeSlot;
    dragRef.current = null;
    const applyState = useConnectionStore.getState().applyState;
    setFilter(lo, hi, slot)
      .then(applyState)
      .catch(() => {});
  };

  // Hover-driven cursor hint (ew-resize on edges, move inside).
  const onPointerMoveHover = (e: React.PointerEvent<HTMLCanvasElement>) => {
    if (dragRef.current) return; // during drag, don't fight the cursor
    const canvas = canvasRef.current;
    if (!canvas) return;
    const rect = canvas.getBoundingClientRect();
    const c = useConnectionStore.getState();
    const passLeftPx = ((c.filterLowHz + RIBBON_SPAN_HZ / 2) / RIBBON_SPAN_HZ) * rect.width;
    const passRightPx = ((c.filterHighHz + RIBBON_SPAN_HZ / 2) / RIBBON_SPAN_HZ) * rect.width;
    const relX = e.clientX - rect.left;
    if (Math.abs(relX - passLeftPx) <= EDGE_HIT_PX || Math.abs(relX - passRightPx) <= EDGE_HIT_PX) {
      canvas.style.cursor = 'ew-resize';
    } else if (relX > passLeftPx && relX < passRightPx) {
      canvas.style.cursor = 'move';
    } else {
      canvas.style.cursor = 'default';
    }
  };

  return (
    <canvas
      ref={canvasRef}
      style={{
        display: 'block',
        width: '100%',
        height: '100%',
        touchAction: 'none',
      }}
      onPointerDown={onPointerDown}
      onPointerMove={(e) => {
        if (dragRef.current) onPointerMove(e);
        else onPointerMoveHover(e);
      }}
      onPointerUp={onPointerUp}
      onPointerCancel={onPointerUp}
    />
  );
}
