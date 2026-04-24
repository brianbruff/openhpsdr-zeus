// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

import { useCallback, useRef } from 'react';
import {
  CONTRAST_MAX,
  CONTRAST_MIN,
  useDisplaySettingsStore,
} from '../state/display-settings-store';

// Vertical waterfall-contrast (γ) slider. Same shape, layout, and drag
// behaviour as DbScale (lives on the left edge of the panel as a 40-px
// column with tick marks + labels), but:
//  - sits on the WATERFALL so it reads as a continuation of the dB scale
//  - drives the gamma curve in WF_FS rather than the dB-window
//  - uses a slate-cyan accent so the operator can tell at a glance which
//    column controls the dB measurement (amber/grey) vs. waterfall colour
//    mapping (cyan).

const ACCENT = '#5DD0E0';

// Tick stops shown along the column. Identity (1.0) is centred-ish; the
// labelled stops bracket common operator settings without crowding the
// 40-px-wide column.
const TICK_VALUES: number[] = [0.5, 1, 1.5, 2, 3, 4];

function formatTick(g: number): string {
  return Number.isInteger(g) ? String(g) : g.toFixed(1);
}

export function ContrastSlider() {
  const contrast = useDisplaySettingsStore((s) => s.contrast);
  const setContrast = useDisplaySettingsStore((s) => s.setContrast);

  const dragState = useRef<{
    startY: number;
    startContrast: number;
    pointerId: number;
    containerHeight: number;
  } | null>(null);

  const onPointerDown = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      const rect = e.currentTarget.getBoundingClientRect();
      dragState.current = {
        startY: e.clientY,
        startContrast: contrast,
        pointerId: e.pointerId,
        containerHeight: rect.height,
      };
      e.currentTarget.setPointerCapture(e.pointerId);
    },
    [contrast],
  );

  const onPointerMove = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      const d = dragState.current;
      if (!d || e.pointerId !== d.pointerId) return;
      const dySig = e.clientY - d.startY;
      // Drag DOWN (dy > 0) increases γ — peaks stand out, noise floor goes
      // dark. Drag UP lifts weak signal at the cost of noise. Mapping:
      // top of column = CONTRAST_MIN, bottom = CONTRAST_MAX, so a dy of
      // +containerHeight covers the full range.
      const gammaPerPixel = (CONTRAST_MAX - CONTRAST_MIN) / d.containerHeight;
      const next = d.startContrast + dySig * gammaPerPixel;
      setContrast(next);
    },
    [setContrast],
  );

  const onPointerUp = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      const d = dragState.current;
      if (!d || e.pointerId !== d.pointerId) return;
      e.currentTarget.releasePointerCapture(e.pointerId);
      dragState.current = null;
    },
    [],
  );

  // Tick label position: top 0% = CONTRAST_MIN, top 100% = CONTRAST_MAX.
  const ticks = TICK_VALUES.map((g) => ({
    g,
    topPct: ((g - CONTRAST_MIN) / (CONTRAST_MAX - CONTRAST_MIN)) * 100,
  }));

  const currentTopPct =
    ((contrast - CONTRAST_MIN) / (CONTRAST_MAX - CONTRAST_MIN)) * 100;

  return (
    <div
      role="slider"
      aria-label="Waterfall contrast (γ)"
      aria-valuemin={CONTRAST_MIN}
      aria-valuemax={CONTRAST_MAX}
      aria-valuenow={Number(contrast.toFixed(2))}
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={onPointerUp}
      onPointerCancel={onPointerUp}
      title={`Waterfall contrast (γ): ${contrast.toFixed(2)} — drag DOWN to suppress noise floor, UP to lift weak signal`}
      className="absolute left-0 top-0 bottom-0 z-10 w-10 cursor-ns-resize touch-none select-none bg-neutral-950/60"
    >
      {/* γ header so it's discoverable that this column is contrast, not dB */}
      <div
        className="absolute left-0 right-0 top-0 flex items-center justify-center"
        style={{ height: 14, fontFamily: 'var(--mono)', fontSize: 10, color: ACCENT, opacity: 0.85 }}
      >
        γ
      </div>

      {/* Tick marks + labels — same layout as DbScale, recoloured */}
      {ticks.map((t) => (
        <div
          key={t.g}
          className="absolute left-0 right-0 flex items-center gap-1"
          style={{ top: `${t.topPct}%`, transform: 'translateY(-50%)' }}
        >
          <div className="h-px w-1.5" style={{ background: ACCENT, opacity: 0.7 }} />
          <div
            className="font-mono text-[9px] leading-none"
            style={{ color: ACCENT, opacity: 0.85 }}
          >
            {formatTick(t.g)}
          </div>
        </div>
      ))}

      {/* Current-value indicator: a short solid bar at the active γ */}
      <div
        className="absolute left-0 right-0 flex items-center"
        style={{ top: `${currentTopPct}%`, transform: 'translateY(-50%)' }}
      >
        <div className="h-0.5 w-full" style={{ background: ACCENT }} />
      </div>
    </div>
  );
}
