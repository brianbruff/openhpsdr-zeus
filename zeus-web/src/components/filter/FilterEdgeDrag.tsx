// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Filter visualization PRD §3.3 Phase 3 — drag handles for the main panadapter
// passband overlay. Renders two thin hit-zones (±4 px around each edge line)
// that capture pointer-drag and write Lo/Hi through POST /api/filter with
// client-side rate limiting (20 Hz max). On first drag, auto-flip the active
// slot to VAR1 if a fixed preset (F*) was selected.

import { useCallback, useRef } from 'react';
import { useDisplayStore } from '../../state/display-store';
import { useConnectionStore } from '../../state/connection-store';
import { setFilter } from '../../api/client';

// Hit-zone width in pixels (±EDGE_HIT_PX from the edge line).
const EDGE_HIT_PX = 4;
// Minimum ms between wire writes during a drag.
const DRAG_MIN_INTERVAL_MS = 50;

type Edge = 'lo' | 'hi';

function presetIsFixed(name: string | null): boolean {
  return !!name && /^F([1-9]|10)$/.test(name);
}

export function FilterEdgeDrag() {
  const centerHz = useDisplayStore((s) => s.centerHz);
  const hzPerPixel = useDisplayStore((s) => s.hzPerPixel);
  const width = useDisplayStore((s) => s.panDb?.length ?? 0);
  const filterLowHz = useConnectionStore((s) => s.filterLowHz);
  const filterHighHz = useConnectionStore((s) => s.filterHighHz);

  const dragRef = useRef<{
    edge: Edge;
    elementWidth: number;
    elementLeft: number;
    spanHz: number;
    centerHz: number;
    otherEdgeHz: number;
    activeSlot: string;
    pendingLo: number;
    pendingHi: number;
    lastWriteAt: number;
    flushTimer: number | null;
    pointerId: number;
  } | null>(null);

  // Send the current pending Lo/Hi to the server. Called immediately if
  // enough time has elapsed since the last write, otherwise scheduled via
  // setTimeout to coalesce rapid pointer events to ~20 Hz.
  const flushPending = useCallback(() => {
    const d = dragRef.current;
    if (!d) return;
    d.flushTimer = null;
    d.lastWriteAt = performance.now();
    setFilter(d.pendingLo, d.pendingHi, d.activeSlot).catch(() => {});
  }, []);

  const schedule = useCallback(() => {
    const d = dragRef.current;
    if (!d) return;
    const now = performance.now();
    const elapsed = now - d.lastWriteAt;
    if (elapsed >= DRAG_MIN_INTERVAL_MS) {
      flushPending();
    } else if (d.flushTimer == null) {
      d.flushTimer = window.setTimeout(flushPending, DRAG_MIN_INTERVAL_MS - elapsed);
    }
  }, [flushPending]);

  const startDrag = useCallback(
    (edge: Edge) => (e: React.PointerEvent<HTMLDivElement>) => {
      if (e.button !== 0) return;
      const parent = e.currentTarget.parentElement;
      if (!parent) return;
      const rect = parent.getBoundingClientRect();
      if (rect.width <= 0 || hzPerPixel <= 0) return;

      e.stopPropagation();
      e.preventDefault();
      try { e.currentTarget.setPointerCapture(e.pointerId); } catch { /* ok */ }

      const span = rect.width * hzPerPixel;
      const currentSlot = useConnectionStore.getState().filterPresetName;
      // First drag on a fixed preset flips to VAR1 — the contract the server
      // enforces and the user expects (fixed F* slots don't accept edits).
      const activeSlot = presetIsFixed(currentSlot) || !currentSlot ? 'VAR1' : currentSlot;

      dragRef.current = {
        edge,
        elementWidth: rect.width,
        elementLeft: rect.left,
        spanHz: span,
        centerHz: Number(centerHz),
        otherEdgeHz: edge === 'lo' ? filterHighHz : filterLowHz,
        activeSlot,
        pendingLo: filterLowHz,
        pendingHi: filterHighHz,
        lastWriteAt: 0,
        flushTimer: null,
        pointerId: e.pointerId,
      };

      // Optimistic UI: reflect the auto-slot flip immediately.
      if (activeSlot !== currentSlot) {
        useConnectionStore.setState({ filterPresetName: activeSlot });
      }
    },
    [centerHz, filterHighHz, filterLowHz, hzPerPixel],
  );

  const onPointerMove = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      const d = dragRef.current;
      if (!d) return;
      if (e.pointerId !== d.pointerId) return;
      e.stopPropagation();

      // Client X → Hz offset from VFO. VFO is always centered in the
      // panadapter/waterfall span, so 0.5 → centerHz.
      const frac = (e.clientX - d.elementLeft) / d.elementWidth;
      const hzFromVfo = (frac - 0.5) * d.spanHz;

      let loHz = d.pendingLo;
      let hiHz = d.pendingHi;
      if (d.edge === 'lo') {
        loHz = Math.round(hzFromVfo);
        // Don't let Lo exceed Hi - 50 Hz (an inverted filter is a WDSP error).
        if (loHz > d.otherEdgeHz - 50) loHz = d.otherEdgeHz - 50;
      } else {
        hiHz = Math.round(hzFromVfo);
        if (hiHz < d.otherEdgeHz + 50) hiHz = d.otherEdgeHz + 50;
      }

      d.pendingLo = loHz;
      d.pendingHi = hiHz;

      // Optimistic UI so the shaded rectangle moves with the cursor without
      // waiting for the server round-trip to echo back.
      useConnectionStore.setState({ filterLowHz: loHz, filterHighHz: hiHz });

      schedule();
    },
    [schedule],
  );

  const endDrag = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      const d = dragRef.current;
      if (!d || e.pointerId !== d.pointerId) return;
      e.stopPropagation();

      if (e.currentTarget.hasPointerCapture(e.pointerId)) {
        try { e.currentTarget.releasePointerCapture(e.pointerId); } catch { /* ok */ }
      }

      if (d.flushTimer != null) {
        clearTimeout(d.flushTimer);
        d.flushTimer = null;
      }

      // Final commit — always send, even if the throttle would otherwise
      // block it, and sync back through applyState so the server is
      // authoritative on the final value.
      const lo = d.pendingLo;
      const hi = d.pendingHi;
      const slot = d.activeSlot;
      dragRef.current = null;
      const applyState = useConnectionStore.getState().applyState;
      setFilter(lo, hi, slot)
        .then(applyState)
        .catch(() => { /* next state poll reconciles */ });
    },
    [],
  );

  if (!width || hzPerPixel <= 0) return null;

  const spanHz = width * hzPerPixel;
  const startHz = Number(centerHz) - spanHz / 2;
  const passLowHz = Number(centerHz) + filterLowHz;
  const passHighHz = Number(centerHz) + filterHighHz;
  const leftPct = ((passLowHz - startHz) / spanHz) * 100;
  const rightPct = ((passHighHz - startHz) / spanHz) * 100;

  // Render edge handles only when the edges are actually on-screen. Off-screen
  // edges can't be dragged; no hit-zone rendered.
  const loOnScreen = leftPct >= 0 && leftPct <= 100;
  const hiOnScreen = rightPct >= 0 && rightPct <= 100;

  const handleStyleBase: React.CSSProperties = {
    position: 'absolute',
    top: 0,
    bottom: 0,
    width: `${EDGE_HIT_PX * 2}px`,
    marginLeft: `-${EDGE_HIT_PX}px`,
    cursor: 'ew-resize',
    // Transparent hit zone. The visible amber line is drawn by PassbandOverlay.
    background: 'transparent',
    zIndex: 7,
    touchAction: 'none',
  };

  return (
    <>
      {loOnScreen && (
        <div
          aria-label="Drag to adjust filter low edge"
          style={{ ...handleStyleBase, left: `${leftPct}%` }}
          onPointerDown={startDrag('lo')}
          onPointerMove={onPointerMove}
          onPointerUp={endDrag}
          onPointerCancel={endDrag}
        />
      )}
      {hiOnScreen && (
        <div
          aria-label="Drag to adjust filter high edge"
          style={{ ...handleStyleBase, left: `${rightPct}%` }}
          onPointerDown={startDrag('hi')}
          onPointerMove={onPointerMove}
          onPointerUp={endDrag}
          onPointerCancel={endDrag}
        />
      )}
    </>
  );
}
