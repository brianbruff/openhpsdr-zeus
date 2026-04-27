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
// Inline NR settings section (issue #79). Renders the per-mode tunables for
// NR2 (EMNR post2) and NR4 (SBNR) directly in the DSP layout — the floating
// right-click popover variant proved unreliable to surface on disabled
// buttons across browsers, so settings live as a normal inline panel
// matching Thetis's Setup-form pattern.

import { useCallback, useEffect, useRef, useState } from 'react';
import { Activity, BarChart3, RotateCcw, Timer, Waves } from 'lucide-react';
import {
  NR2_POST2_DEFAULTS,
  setNr2Post2,
  type Nr2Post2PatchBody,
  type RadioStateDto,
} from '../../api/client';
import { useConnectionStore } from '../../state/connection-store';

export type NrSettingsMode = 'Anr' | 'Emnr' | 'Sbnr';

export type NrSettingsSectionProps = {
  mode: NrSettingsMode;
};

export function NrSettingsSection({ mode }: NrSettingsSectionProps) {
  // NR4 (Sbnr) panel is intentionally not rendered here — bundled libwdsp
  // doesn't export the SetRXASBNR* symbols (Phase 1 of issue #79), so any
  // adjustment is silently inert. Nr4Panel is preserved below; re-enable
  // its case in this switch once Phase 1 binaries land.
  if (mode === 'Sbnr') return null;
  return (
    <div className="nr-settings" role="region" aria-label={`NR ${mode} settings`}>
      <h3 className="nr-settings__title">
        {mode === 'Anr' && 'NR1 — ANR'}
        {mode === 'Emnr' && 'NR2 — EMNR POST2'}
      </h3>
      {mode === 'Anr' && <AnrPanel />}
      {mode === 'Emnr' && <Nr2Panel />}
    </div>
  );
}

// ---------- NR1 (ANR) — no exposed tunables in this iteration. ----------

function AnrPanel() {
  return (
    <p className="nr-settings__hint">
      NR1 (time-domain LMS) has no operator-tunable knobs in Zeus today.
      Defaults match Thetis: 64 taps, 16-sample delay, gain 1e-4, leakage 0.1.
    </p>
  );
}

// ---------- NR2 (EMNR) post2 comfort-noise tunables. ----------

const PERSIST_DEBOUNCE_MS = 120;

type RowAccent = 'red' | 'green' | 'orange' | 'purple';

function Nr2Panel() {
  const nr = useConnectionStore((s) => s.nr);
  const applyState = useConnectionStore((s) => s.applyState);

  const [run, setRun] = useState<boolean>(nr.emnrPost2Run ?? NR2_POST2_DEFAULTS.run);
  const [factor, setFactor] = useState<number>(nr.emnrPost2Factor ?? NR2_POST2_DEFAULTS.factor);
  const [nlevel, setNlevel] = useState<number>(nr.emnrPost2Nlevel ?? NR2_POST2_DEFAULTS.nlevel);
  const [rate, setRate] = useState<number>(nr.emnrPost2Rate ?? NR2_POST2_DEFAULTS.rate);
  const [taper, setTaper] = useState<number>(nr.emnrPost2Taper ?? NR2_POST2_DEFAULTS.taper);

  const inflight = useRef<AbortController | null>(null);
  const debounce = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(
    () => () => {
      inflight.current?.abort();
      if (debounce.current != null) clearTimeout(debounce.current);
    },
    [],
  );

  const persist = useCallback(
    (body: Nr2Post2PatchBody) => {
      if (debounce.current != null) clearTimeout(debounce.current);
      debounce.current = setTimeout(() => {
        inflight.current?.abort();
        const ac = new AbortController();
        inflight.current = ac;
        setNr2Post2(body, ac.signal)
          .then((s: RadioStateDto) => {
            if (!ac.signal.aborted) applyState(s);
          })
          .catch(() => {
            /* state poll will reconcile */
          });
      }, PERSIST_DEBOUNCE_MS);
    },
    [applyState],
  );

  const onRunChange = (v: boolean) => {
    setRun(v);
    persist({ post2Run: v });
  };
  const onFactorChange = (v: number) => {
    setFactor(v);
    persist({ post2Factor: v });
  };
  const onNlevelChange = (v: number) => {
    setNlevel(v);
    persist({ post2Nlevel: v });
  };
  const onRateChange = (v: number) => {
    setRate(v);
    persist({ post2Rate: v });
  };
  const onTaperChange = (v: number) => {
    const r = Math.round(v);
    setTaper(r);
    persist({ post2Taper: r });
  };

  const resetDefaults = () => {
    setRun(NR2_POST2_DEFAULTS.run);
    setFactor(NR2_POST2_DEFAULTS.factor);
    setNlevel(NR2_POST2_DEFAULTS.nlevel);
    setRate(NR2_POST2_DEFAULTS.rate);
    setTaper(NR2_POST2_DEFAULTS.taper);
    persist({
      post2Run: NR2_POST2_DEFAULTS.run,
      post2Factor: NR2_POST2_DEFAULTS.factor,
      post2Nlevel: NR2_POST2_DEFAULTS.nlevel,
      post2Rate: NR2_POST2_DEFAULTS.rate,
      post2Taper: NR2_POST2_DEFAULTS.taper,
    });
  };

  return (
    <div>
      <div
        className="nr-settings__toggle-row"
        title="EMNR's post-stage comfort-noise injection (post2). Off = raw EMNR output. The NR cycle button is the master on/off; this is a sub-stage of NR2 only."
      >
        <label className="nr-settings__label" htmlFor="nr2-run">Post-Process</label>
        <Switch id="nr2-run" checked={run} onChange={onRunChange} />
      </div>

      <GaugeRow
        accent="red"
        icon={<Waves size={14} strokeWidth={2.25} />}
        label="Factor"
        value={factor}
        min={0}
        max={100}
        step={1}
        decimals={1}
        onChange={onFactorChange}
      />
      <GaugeRow
        accent="green"
        icon={<Activity size={14} strokeWidth={2.25} />}
        label="Nlevel"
        value={nlevel}
        min={0}
        max={100}
        step={1}
        decimals={1}
        onChange={onNlevelChange}
      />
      <GaugeRow
        accent="orange"
        icon={<Timer size={14} strokeWidth={2.25} />}
        label="Rate"
        value={rate}
        min={0}
        max={20}
        step={0.1}
        decimals={1}
        onChange={onRateChange}
      />
      <GaugeRow
        accent="purple"
        icon={<BarChart3 size={14} strokeWidth={2.25} />}
        label="Taper (bins)"
        value={taper}
        min={0}
        max={32}
        step={1}
        decimals={0}
        onChange={onTaperChange}
      />

      <p className="nr-settings__hint">
        Comfort-noise injection masking residual EMNR warble. Defaults: factor 15,
        nlevel 15, rate 5.0, taper 12 (Thetis NUD scale; WDSP /100 internally).
        See emnr.c:981–1056.
      </p>

      <div className="nr-settings__buttons">
        <button
          type="button"
          className="nr-settings__button nr-settings__button--primary"
          onClick={resetDefaults}
          title="Reset all needles to factory defaults"
        >
          <RotateCcw size={12} strokeWidth={2.5} />
          <span>Defaults</span>
        </button>
      </div>
    </div>
  );
}

// ---------- Gauge row ---------------------------------------------------

type GaugeRowProps = {
  accent: RowAccent;
  icon: React.ReactNode;
  label: string;
  value: number;
  min: number;
  max: number;
  step: number;
  decimals: number;
  onChange: (v: number) => void;
};

function GaugeRow({
  accent,
  icon,
  label,
  value,
  min,
  max,
  step,
  decimals,
  onChange,
}: GaugeRowProps) {
  const span = max - min || 1;
  const norm = Math.max(0, Math.min(1, (value - min) / span));
  const display = decimals === 0 ? String(Math.round(value)) : value.toFixed(decimals);

  const handleNorm = (n: number) => {
    const raw = min + n * span;
    const snapped = step > 0 ? Math.round(raw / step) * step : raw;
    const clamped = Math.max(min, Math.min(max, snapped));
    // Tame float noise (e.g. 0.150000000002).
    const out = decimals > 0 ? Number(clamped.toFixed(decimals)) : clamped;
    if (out !== value) onChange(out);
  };

  return (
    <div className={`nr-row nr-row--${accent}`}>
      <span className="nr-row__icon" aria-hidden>{icon}</span>
      <span className="nr-row__label">{label}</span>
      <Gauge norm={norm} accent={accent} onNormChange={handleNorm} />
      <span
        className="nr-row__value"
        role="status"
        aria-label={`${label} ${display}`}
      >
        {display}
      </span>
      <Bars norm={norm} accent={accent} />
    </div>
  );
}

// Mini circular gauge — 270° arc with a colored needle. Drag-to-set:
// pointer position relative to the gauge centre maps to the angle on the
// 135°→405° arc, then back to a normalised value. The dead-zone at the
// bottom (between 405° and 135° going through 90°) snaps to whichever
// end is nearer.
function Gauge({
  norm,
  accent,
  onNormChange,
}: {
  norm: number;
  accent: RowAccent;
  onNormChange: (n: number) => void;
}) {
  const startDeg = 135;
  const sweepDeg = 270;
  const angle = startDeg + sweepDeg * norm;
  const size = 34;
  const cx = size / 2;
  const cy = size / 2;
  const r = size / 2 - 4;
  const rad = (angle * Math.PI) / 180;
  const tipX = cx + Math.cos(rad) * (r - 1.5);
  const tipY = cy + Math.sin(rad) * (r - 1.5);

  const arc = describeArc(cx, cy, r, startDeg, startDeg + sweepDeg);
  const arcLive = describeArc(cx, cy, r, startDeg, angle);

  const setFromPoint = (clientX: number, clientY: number, rect: DOMRect) => {
    const dx = clientX - (rect.left + rect.width / 2);
    const dy = clientY - (rect.top + rect.height / 2);
    let theta = (Math.atan2(dy, dx) * 180) / Math.PI; // [-180, 180]
    if (theta < 0) theta += 360;                       // [0, 360]
    let shifted = (theta - startDeg + 360) % 360;      // [0, 360)
    if (shifted > sweepDeg) {
      // In the bottom dead-zone — snap to nearest end.
      shifted = shifted - sweepDeg < 360 - shifted ? sweepDeg : 0;
    }
    onNormChange(shifted / sweepDeg);
  };

  const onPointerDown = (e: React.PointerEvent<SVGSVGElement>) => {
    e.preventDefault();
    const target = e.currentTarget;
    const rect = target.getBoundingClientRect();
    target.setPointerCapture(e.pointerId);
    setFromPoint(e.clientX, e.clientY, rect);

    const onMove = (ev: PointerEvent) => {
      setFromPoint(ev.clientX, ev.clientY, rect);
    };
    const onUp = (ev: PointerEvent) => {
      try { target.releasePointerCapture(ev.pointerId); } catch { /* released already */ }
      target.removeEventListener('pointermove', onMove);
      target.removeEventListener('pointerup', onUp);
      target.removeEventListener('pointercancel', onUp);
    };
    target.addEventListener('pointermove', onMove);
    target.addEventListener('pointerup', onUp);
    target.addEventListener('pointercancel', onUp);
  };

  return (
    <svg
      className="nr-row__gauge"
      width={size}
      height={size}
      viewBox={`0 0 ${size} ${size}`}
      role="slider"
      aria-valuemin={0}
      aria-valuemax={1}
      aria-valuenow={Number(norm.toFixed(3))}
      tabIndex={0}
      onPointerDown={onPointerDown}
    >
      <path d={arc} className="nr-gauge__track" />
      <path d={arcLive} className={`nr-gauge__live nr-gauge__live--${accent}`} />
      <line
        x1={cx}
        y1={cy}
        x2={tipX}
        y2={tipY}
        className={`nr-gauge__needle nr-gauge__needle--${accent}`}
      />
      <circle cx={cx} cy={cy} r={1.8} className="nr-gauge__hub" />
    </svg>
  );
}

// Cell-bars style indicator — 4 bars whose count lit follows norm.
function Bars({ norm, accent }: { norm: number; accent: RowAccent }) {
  const lit = Math.max(0, Math.min(4, Math.ceil(norm * 4)));
  return (
    <svg
      className="nr-row__bars"
      width={26}
      height={18}
      viewBox="0 0 26 18"
      aria-hidden
    >
      {[0, 1, 2, 3].map((i) => {
        const h = 4 + i * 4;
        const x = i * 6.5;
        const y = 18 - h;
        const on = i < lit;
        return (
          <rect
            key={i}
            x={x}
            y={y}
            width={4}
            height={h}
            rx={0.6}
            className={
              on
                ? `nr-bars__bar nr-bars__bar--on nr-bars__bar--${accent}`
                : 'nr-bars__bar'
            }
          />
        );
      })}
    </svg>
  );
}

// Standard SVG arc-path helper (degrees, clockwise).
function describeArc(cx: number, cy: number, r: number, a0: number, a1: number): string {
  const p0 = polar(cx, cy, r, a0);
  const p1 = polar(cx, cy, r, a1);
  const large = a1 - a0 > 180 ? 1 : 0;
  return `M ${p0.x.toFixed(2)} ${p0.y.toFixed(2)} A ${r} ${r} 0 ${large} 1 ${p1.x.toFixed(2)} ${p1.y.toFixed(2)}`;
}
function polar(cx: number, cy: number, r: number, deg: number): { x: number; y: number } {
  const rad = (deg * Math.PI) / 180;
  return { x: cx + Math.cos(rad) * r, y: cy + Math.sin(rad) * r };
}

// ---------- iOS-style toggle switch ------------------------------------

function Switch({
  id,
  checked,
  onChange,
}: {
  id: string;
  checked: boolean;
  onChange: (v: boolean) => void;
}) {
  return (
    <button
      id={id}
      type="button"
      role="switch"
      aria-checked={checked}
      className={`nr-switch ${checked ? 'is-on' : ''}`}
      onClick={() => onChange(!checked)}
    >
      <span className="nr-switch__thumb" />
    </button>
  );
}
