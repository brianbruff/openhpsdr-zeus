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
// Zeus is an independent reimplementation in .NET — not a fork.
//
// Right-click popover that exposes per-mode tunables for the NR ribbon
// (issue #79). Mirrors Thetis's setup form NR2/NR4 sections in a single
// floating panel anchored to the NR button. NR1 (ANR) doesn't expose any
// operator-tunable knobs in this iteration — the popover renders an info
// stub for that mode so the right-click affordance is consistent across
// all four NR states.

import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import {
  NR2_POST2_DEFAULTS,
  NR4_DEFAULTS,
  setNr2Post2,
  setNr4,
  type NrConfigDto,
  type RadioStateDto,
} from '../../api/client';
import { useConnectionStore } from '../../state/connection-store';

export type NrPopoverMode = 'Anr' | 'Emnr' | 'Sbnr';

export type NrSettingsPopoverProps = {
  mode: NrPopoverMode;
  // Anchor element — popover positions itself adjacent to this DOMRect.
  anchor: HTMLElement;
  onClose: () => void;
};

// Returns viewport-clamped { left, top } for a popover near `anchor`.
// We position below-and-right by default and flip up/left when that
// would overflow the viewport. Inline `position: fixed` so the
// popover doesn't get clipped by overflow:hidden ancestors.
function placePopover(
  anchor: HTMLElement,
  popover: HTMLElement,
): { left: number; top: number } {
  const a = anchor.getBoundingClientRect();
  const pw = popover.offsetWidth;
  const ph = popover.offsetHeight;
  const margin = 6;
  let left = a.left;
  let top = a.bottom + margin;
  if (left + pw + margin > window.innerWidth) {
    left = Math.max(margin, window.innerWidth - pw - margin);
  }
  if (top + ph + margin > window.innerHeight) {
    top = Math.max(margin, a.top - ph - margin);
  }
  return { left, top };
}

export function NrSettingsPopover({ mode, anchor, onClose }: NrSettingsPopoverProps) {
  const popoverRef = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState<{ left: number; top: number } | null>(null);

  // Reposition on mount + on resize. useLayoutEffect avoids a flash at the
  // wrong location on first paint.
  useLayoutEffect(() => {
    if (popoverRef.current) {
      setPos(placePopover(anchor, popoverRef.current));
    }
    function onResize() {
      if (popoverRef.current) {
        setPos(placePopover(anchor, popoverRef.current));
      }
    }
    window.addEventListener('resize', onResize);
    return () => window.removeEventListener('resize', onResize);
  }, [anchor]);

  // Close on Escape (capture phase so we beat any inner onKeyDown).
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') {
        e.preventDefault();
        onClose();
      }
    }
    window.addEventListener('keydown', onKey, { capture: true });
    return () => window.removeEventListener('keydown', onKey, { capture: true });
  }, [onClose]);

  return (
    <>
      {/* Transparent backdrop captures outside-click. */}
      <div className="nr-popover-backdrop" onMouseDown={onClose} />
      <div
        ref={popoverRef}
        className="nr-popover"
        style={pos != null ? { left: pos.left, top: pos.top } : { visibility: 'hidden' }}
        // Prevent backdrop's mousedown from firing when clicking inside.
        onMouseDown={(e) => e.stopPropagation()}
        onContextMenu={(e) => e.preventDefault()}
        role="dialog"
        aria-label={`NR ${mode} settings`}
      >
        {mode === 'Anr' && <AnrPanel onClose={onClose} />}
        {mode === 'Emnr' && <Nr2Panel onClose={onClose} />}
        {mode === 'Sbnr' && <Nr4Panel onClose={onClose} />}
      </div>
    </>
  );
}

// ---------- NR1 (ANR) — no exposed tunables in this iteration. ----------

function AnrPanel({ onClose }: { onClose: () => void }) {
  return (
    <>
      <h3 className="nr-popover__title">NR1 — ANR</h3>
      <p className="nr-popover__hint">
        NR1 (time-domain LMS) has no operator-tunable knobs in Zeus today.
        Defaults match Thetis: 64 taps, 16-sample delay, gain 1e-4, leakage 0.1.
      </p>
      <div className="nr-popover__buttons">
        <button type="button" className="nr-popover__button" onClick={onClose}>
          Close
        </button>
      </div>
    </>
  );
}

// ---------- NR2 (EMNR) post2 comfort-noise tunables. ----------

function Nr2Panel({ onClose }: { onClose: () => void }) {
  const nr = useConnectionStore((s) => s.nr);
  const applyState = useConnectionStore((s) => s.applyState);

  const [run, setRun] = useState<boolean>(nr.emnrPost2Run ?? NR2_POST2_DEFAULTS.run);
  const [factor, setFactor] = useState<number>(nr.emnrPost2Factor ?? NR2_POST2_DEFAULTS.factor);
  const [nlevel, setNlevel] = useState<number>(nr.emnrPost2Nlevel ?? NR2_POST2_DEFAULTS.nlevel);
  const [rate, setRate] = useState<number>(nr.emnrPost2Rate ?? NR2_POST2_DEFAULTS.rate);
  const [taper, setTaper] = useState<number>(nr.emnrPost2Taper ?? NR2_POST2_DEFAULTS.taper);

  function commit() {
    setNr2Post2({
      post2Run: run,
      post2Factor: factor,
      post2Nlevel: nlevel,
      post2Rate: rate,
      post2Taper: Math.round(taper),
    })
      .then((s: RadioStateDto) => applyState(s))
      .catch(() => {
        /* state poll will reconcile */
      });
    onClose();
  }

  return (
    <form
      onSubmit={(e) => {
        e.preventDefault();
        commit();
      }}
    >
      <h3 className="nr-popover__title">NR2 — EMNR post2</h3>

      <div className="nr-popover__row">
        <label className="nr-popover__label" htmlFor="nr2-run">Run</label>
        <input
          id="nr2-run"
          type="checkbox"
          className="nr-popover__checkbox"
          checked={run}
          onChange={(e) => setRun(e.target.checked)}
        />
      </div>

      <NumericRow id="nr2-factor" label="Factor" value={factor} step={0.01} min={0} max={1} onChange={setFactor} />
      <NumericRow id="nr2-nlevel" label="Nlevel" value={nlevel} step={0.01} min={0} max={1} onChange={setNlevel} />
      <NumericRow id="nr2-rate" label="Rate" value={rate} step={0.1} min={0} onChange={setRate} />
      <NumericRow id="nr2-taper" label="Taper (bins)" value={taper} step={1} min={0} onChange={setTaper} />

      <p className="nr-popover__hint">
        Comfort-noise injection masking residual EMNR warble. Defaults: factor 0.15,
        nlevel 0.15, rate 5.0, taper 12. See emnr.c:981–1056.
      </p>

      <div className="nr-popover__buttons">
        <button type="button" className="nr-popover__button" onClick={onClose}>
          Cancel
        </button>
        <button type="submit" className="nr-popover__button nr-popover__button--primary">
          Save
        </button>
      </div>
    </form>
  );
}

// ---------- NR4 (SBNR) tunables. ----------

function Nr4Panel({ onClose }: { onClose: () => void }) {
  const nr = useConnectionStore((s) => s.nr);
  const applyState = useConnectionStore((s) => s.applyState);

  const [reduction, setReduction] = useState<number>(nr.nr4ReductionAmount ?? NR4_DEFAULTS.reductionAmount);
  const [smoothing, setSmoothing] = useState<number>(nr.nr4SmoothingFactor ?? NR4_DEFAULTS.smoothingFactor);
  const [whitening, setWhitening] = useState<number>(nr.nr4WhiteningFactor ?? NR4_DEFAULTS.whiteningFactor);
  const [noiseRescale, setNoiseRescale] = useState<number>(nr.nr4NoiseRescale ?? NR4_DEFAULTS.noiseRescale);
  const [postThr, setPostThr] = useState<number>(nr.nr4PostFilterThreshold ?? NR4_DEFAULTS.postFilterThreshold);
  const [scalingType, setScalingType] = useState<number>(nr.nr4NoiseScalingType ?? NR4_DEFAULTS.noiseScalingType);
  const [position, setPosition] = useState<number>(nr.nr4Position ?? NR4_DEFAULTS.position);

  function commit() {
    setNr4({
      reductionAmount: reduction,
      smoothingFactor: smoothing,
      whiteningFactor: whitening,
      noiseRescale: noiseRescale,
      postFilterThreshold: postThr,
      noiseScalingType: Math.round(scalingType),
      position: Math.round(position),
    })
      .then((s: RadioStateDto) => applyState(s))
      .catch(() => {
        /* state poll will reconcile */
      });
    onClose();
  }

  return (
    <form
      onSubmit={(e) => {
        e.preventDefault();
        commit();
      }}
    >
      <h3 className="nr-popover__title">NR4 — SBNR</h3>

      <NumericRow id="nr4-reduction" label="Reduction" value={reduction} step={0.5} min={0} max={40} onChange={setReduction} />
      <NumericRow id="nr4-smoothing" label="Smoothing" value={smoothing} step={0.05} min={0} max={1} onChange={setSmoothing} />
      <NumericRow id="nr4-whitening" label="Whitening" value={whitening} step={0.05} min={0} max={1} onChange={setWhitening} />
      <NumericRow id="nr4-rescale" label="Noise Rescale" value={noiseRescale} step={0.5} min={0} max={10} onChange={setNoiseRescale} />
      <NumericRow id="nr4-postthr" label="Post Filter Thr" value={postThr} step={0.5} onChange={setPostThr} />

      <div className="nr-popover__row">
        <label className="nr-popover__label" htmlFor="nr4-scaling">Noise Scaling</label>
        <select
          id="nr4-scaling"
          className="nr-popover__select"
          value={scalingType}
          onChange={(e) => setScalingType(Number(e.target.value))}
        >
          <option value={0}>0 — None</option>
          <option value={1}>1 — Type 1</option>
          <option value={2}>2 — Type 2</option>
        </select>
      </div>

      <div className="nr-popover__row">
        <label className="nr-popover__label" htmlFor="nr4-position">Position</label>
        <select
          id="nr4-position"
          className="nr-popover__select"
          value={position}
          onChange={(e) => setPosition(Number(e.target.value))}
        >
          <option value={0}>0 — Pre-AGC</option>
          <option value={1}>1 — Post-AGC</option>
        </select>
      </div>

      <p className="nr-popover__hint">
        libspecbleach (sbnr.c). Defaults: reduction 10, others 0, noise rescale 2,
        position 1. Requires Phase 1 libwdsp rebuild — issue #79.
      </p>

      <div className="nr-popover__buttons">
        <button type="button" className="nr-popover__button" onClick={onClose}>
          Cancel
        </button>
        <button type="submit" className="nr-popover__button nr-popover__button--primary">
          Save
        </button>
      </div>
    </form>
  );
}

// ---------- Shared numeric input row. ----------

type NumericRowProps = {
  id: string;
  label: string;
  value: number;
  step: number;
  min?: number;
  max?: number;
  onChange: (v: number) => void;
};

function NumericRow({ id, label, value, step, min, max, onChange }: NumericRowProps) {
  return (
    <div className="nr-popover__row">
      <label className="nr-popover__label" htmlFor={id}>{label}</label>
      <input
        id={id}
        type="number"
        className="nr-popover__input"
        value={value}
        step={step}
        min={min}
        max={max}
        onChange={(e) => {
          const v = Number(e.target.value);
          if (!Number.isNaN(v)) onChange(v);
        }}
      />
    </div>
  );
}

// Helper for the "no tunables" stub above. Kept exported for any future
// caller that wants to render the current persisted values without the
// popover wrapper.
export function nrConfigHasOverrides(nr: NrConfigDto, mode: NrPopoverMode): boolean {
  switch (mode) {
    case 'Anr':
      return false;
    case 'Emnr':
      return (
        nr.emnrPost2Run != null
        || nr.emnrPost2Factor != null
        || nr.emnrPost2Nlevel != null
        || nr.emnrPost2Rate != null
        || nr.emnrPost2Taper != null
      );
    case 'Sbnr':
      return (
        nr.nr4ReductionAmount != null
        || nr.nr4SmoothingFactor != null
        || nr.nr4WhiteningFactor != null
        || nr.nr4NoiseRescale != null
        || nr.nr4PostFilterThreshold != null
        || nr.nr4NoiseScalingType != null
        || nr.nr4Position != null
      );
  }
}
