import { useEffect, useRef } from 'react';
import { COLORMAPS, type ColormapId } from '../gl/colormap';
import { createWfRenderer } from '../gl/waterfall';
import { useDisplayStore } from '../state/display-store';
import { useDisplaySettingsStore } from '../state/display-settings-store';
import { usePanTuneGesture } from '../util/use-pan-tune-gesture';

// Throttle row uploads so the waterfall scrolls at ~(server tick / N).
// With a 30 Hz server tick N=2 gives ~15 Hz, which matches the deskhpsdr
// feel without costing much CPU. Shift/reset still run every frame so VFO
// retunes stay synchronised with the panadapter's offset.
// TODO(phase-3.1): expose as a UI setting.
const WF_PUSH_EVERY_N = 2;

type WaterfallProps = {
  /** When true, noise floor fades to transparent so the QRZ-mode map shows through. */
  transparent?: boolean;
};

export function Waterfall({ transparent = false }: WaterfallProps = {}) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const rendererRef = useRef<ReturnType<typeof createWfRenderer> | null>(null);
  const autoRange = useDisplaySettingsStore((s) => s.autoRange);
  const setAutoRange = useDisplaySettingsStore((s) => s.setAutoRange);
  const colormap = useDisplaySettingsStore((s) => s.colormap);
  const setColormap = useDisplaySettingsStore((s) => s.setColormap);

  useEffect(() => {
    const canvas = canvasRef.current;
    const container = containerRef.current;
    if (!canvas || !container) return;

    const gl = canvas.getContext('webgl2', { antialias: false, alpha: true, premultipliedAlpha: true });
    if (!gl) {
      console.error('WebGL2 not available');
      return;
    }

    const renderer = createWfRenderer(gl);
    rendererRef.current = renderer;
    // Seed with the current store value so the palette survives remount
    // (e.g. after a resize that cycles the canvas).
    renderer.setColormap(useDisplaySettingsStore.getState().colormap);
    renderer.setTransparent(transparent);
    let rafHandle = 0;
    let lastSeqDrawn = -1;
    let tickCounter = 0;
    let lastColormap: ColormapId = useDisplaySettingsStore.getState().colormap;

    const redraw = () => {
      rafHandle = 0;
      const { dbMin, dbMax } = useDisplaySettingsStore.getState();
      renderer.draw(dbMin, dbMax);
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

    const unsub = useDisplayStore.subscribe((state) => {
      if (state.lastSeq === lastSeqDrawn) return;
      lastSeqDrawn = state.lastSeq;
      if (state.wfValid && state.wfDb) {
        tickCounter++;
        const skipRowUpload = tickCounter % WF_PUSH_EVERY_N !== 0;
        renderer.pushFrame(state.wfDb, state.centerHz, state.hzPerPixel, {
          skipRowUpload,
        });
        useDisplaySettingsStore.getState().updateAutoRange(state.wfDb);
      }
      requestRedraw();
    });

    // Auto-range changes the dbMin/dbMax uniforms without new frames — repaint
    // when the settings store updates so the toggle feels immediate. Same
    // subscription also catches colormap swaps; re-upload the LUT only when
    // the id actually changed to avoid a texImage2D per auto-range tick.
    const unsubSettings = useDisplaySettingsStore.subscribe((state) => {
      if (state.colormap !== lastColormap) {
        lastColormap = state.colormap;
        renderer.setColormap(state.colormap);
      }
      requestRedraw();
    });

    return () => {
      unsub();
      unsubSettings();
      ro.disconnect();
      if (rafHandle !== 0) cancelAnimationFrame(rafHandle);
      renderer.dispose();
      rendererRef.current = null;
    };
  }, []);

  // Keep the renderer's transparency flag in sync without remounting so the
  // history texture survives a QRZ engage/disengage. draw() runs on the next
  // frame via the realtime store subscription.
  useEffect(() => {
    rendererRef.current?.setTransparent(transparent);
  }, [transparent]);

  usePanTuneGesture(canvasRef);

  return (
    <div
      ref={containerRef}
      className="waterfall-canvas"
      style={{
        position: 'relative',
        minHeight: 0,
        width: '100%',
        height: '100%',
        background: 'var(--wf-0)',
      }}
    >
      <canvas ref={canvasRef} style={{ position: 'absolute', inset: 0, width: '100%', height: '100%' }} />
      <div
        className="tuning-cursor"
        style={{ left: '50%', pointerEvents: 'none' }}
      />
      <div style={{ position: 'absolute', top: 6, right: 6, display: 'flex', alignItems: 'center', gap: 4 }}>
        <div role="radiogroup" aria-label="Colormap" className="btn-row">
          {COLORMAPS.map((cm) => {
            const active = colormap === cm.id;
            return (
              <button
                key={cm.id}
                type="button"
                role="radio"
                aria-checked={active}
                onClick={() => setColormap(cm.id)}
                title={`Waterfall colormap: ${cm.label}`}
                className={`btn sm ${active ? 'active' : ''}`}
              >
                {cm.label}
              </button>
            );
          })}
        </div>
        <button
          type="button"
          onClick={() => setAutoRange(!autoRange)}
          aria-pressed={autoRange}
          title={
            autoRange
              ? 'Auto dB range: tracking p5/p95 of waterfall samples'
              : 'Fixed dB range: −120 to −30 dBFS'
          }
          className={`btn sm ${autoRange ? 'active' : ''}`}
        >
          {autoRange ? 'dB: AUTO' : 'dB: FIXED'}
        </button>
      </div>
    </div>
  );
}
