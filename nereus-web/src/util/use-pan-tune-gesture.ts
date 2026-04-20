import { useEffect, type RefObject } from 'react';
import { setVfo } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useDisplayStore } from '../state/display-store';

const MAX_HZ = 60_000_000;
const CLICK_SLOP_PX = 3;
// Pan gestures (click + drag on pan/wf) snap to this step. Typed-freq input
// and band presets bypass it. Ham-friendly default; becomes user-settable
// once the UX exists.
const PAN_STEP_HZ = 500;

function snapHz(hz: number): number {
  if (!Number.isFinite(hz)) return 0;
  const snapped = Math.round(hz / PAN_STEP_HZ) * PAN_STEP_HZ;
  return Math.min(MAX_HZ, Math.max(0, snapped));
}

function readView(): { centerHz: number; spanHz: number } | null {
  const s = useDisplayStore.getState();
  if (!s.panDb || s.hzPerPixel <= 0) return null;
  return {
    centerHz: Number(s.centerHz),
    spanHz: s.panDb.length * s.hzPerPixel,
  };
}

/**
 * Install click-to-tune and drag-to-pan pointer handlers on a spectrum canvas.
 * Both panadapter and waterfall share this so the user can tune from whichever
 * view they prefer. Values snap to PAN_STEP_HZ (500 Hz) — the per-gesture
 * default. Drags coalesce to one POST per animation frame; releases commit
 * final and re-sync from the server response.
 */
export function usePanTuneGesture(
  canvasRef: RefObject<HTMLCanvasElement | null>,
) {
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;

    type Drag = { startX: number; startHz: number; spanHz: number; moved: boolean };
    let drag: Drag | null = null;
    let pendingHz: number | null = null;
    let pendingAbort: AbortController | null = null;
    let pendingRaf = 0;

    const flushPending = () => {
      pendingRaf = 0;
      const hz = pendingHz;
      pendingHz = null;
      if (hz == null) return;
      useConnectionStore.setState({ vfoHz: hz });
      pendingAbort?.abort();
      const ctrl = new AbortController();
      pendingAbort = ctrl;
      setVfo(hz, ctrl.signal).catch(() => {});
    };

    const scheduleFlush = () => {
      if (pendingRaf === 0) pendingRaf = requestAnimationFrame(flushPending);
    };

    const commitFinal = (hz: number) => {
      const snapped = snapHz(hz);
      useConnectionStore.setState({ vfoHz: snapped });
      pendingAbort?.abort();
      pendingAbort = null;
      if (pendingRaf !== 0) {
        cancelAnimationFrame(pendingRaf);
        pendingRaf = 0;
      }
      pendingHz = null;
      setVfo(snapped)
        .then((s) => useConnectionStore.getState().applyState(s))
        .catch(() => {});
    };

    const onPointerDown = (e: PointerEvent) => {
      if (e.button !== 0) return;
      const view = readView();
      if (!view) return;
      e.preventDefault();
      try {
        canvas.setPointerCapture(e.pointerId);
      } catch {
        /* synthetic events don't have an active pointer; real mouse/touch does */
      }
      drag = {
        startX: e.clientX,
        startHz: view.centerHz,
        spanHz: view.spanHz,
        moved: false,
      };
      canvas.style.cursor = 'grabbing';
    };

    const onPointerMove = (e: PointerEvent) => {
      if (!drag) return;
      const dx = e.clientX - drag.startX;
      if (!drag.moved && Math.abs(dx) <= CLICK_SLOP_PX) return;
      drag.moved = true;
      const rect = canvas.getBoundingClientRect();
      if (rect.width <= 0) return;
      const newHz = snapHz(drag.startHz - (dx / rect.width) * drag.spanHz);
      pendingHz = newHz;
      scheduleFlush();
    };

    const onPointerUp = (e: PointerEvent) => {
      const d = drag;
      if (!d) return;
      drag = null;
      canvas.style.cursor = 'grab';
      if (canvas.hasPointerCapture(e.pointerId)) {
        canvas.releasePointerCapture(e.pointerId);
      }
      const rect = canvas.getBoundingClientRect();
      if (rect.width <= 0) return;
      if (d.moved) {
        const dx = e.clientX - d.startX;
        commitFinal(d.startHz - (dx / rect.width) * d.spanHz);
      } else {
        // click-to-tune: resolve the clicked frequency against the live view.
        const view = readView();
        if (!view) return;
        const frac = (e.clientX - rect.left) / rect.width;
        commitFinal(view.centerHz + (frac - 0.5) * view.spanHz);
      }
    };

    canvas.style.cursor = 'grab';
    canvas.addEventListener('pointerdown', onPointerDown);
    canvas.addEventListener('pointermove', onPointerMove);
    canvas.addEventListener('pointerup', onPointerUp);
    canvas.addEventListener('pointercancel', onPointerUp);

    return () => {
      if (pendingRaf !== 0) cancelAnimationFrame(pendingRaf);
      pendingAbort?.abort();
      canvas.removeEventListener('pointerdown', onPointerDown);
      canvas.removeEventListener('pointermove', onPointerMove);
      canvas.removeEventListener('pointerup', onPointerUp);
      canvas.removeEventListener('pointercancel', onPointerUp);
    };
  }, [canvasRef]);
}
