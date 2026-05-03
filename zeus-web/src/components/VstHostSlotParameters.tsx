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
// VST host slot parameter list. Renders the operator-visible subset of a
// loaded plugin's parameters (Hidden + ReadOnly are filtered out per spec).
// Slider drag is throttled to ~30 Hz so we don't hammer the backend on
// every input event.

import { useEffect, useMemo, useRef } from 'react';

import {
  PARAM_FLAG_HIDDEN,
  PARAM_FLAG_READ_ONLY,
  type VstHostParameter,
} from '../api/vst-host';
import { useVstHostStore } from '../state/vst-host-store';

const PARAM_THROTTLE_MS = 1000 / 30; // ≤30 Hz parameter writes during drag.

type Props = {
  slotIndex: number;
};

export function VstHostSlotParameters({ slotIndex }: Props) {
  const params = useVstHostStore(
    (s) => s.slotParameters.get(slotIndex) ?? null,
  );
  const setSlotParameter = useVstHostStore((s) => s.setSlotParameter);
  const refreshSlotParameters = useVstHostStore(
    (s) => s.refreshSlotParameters,
  );

  // Lazy-load on first expand. Re-fetch is cheap (parameter list only).
  useEffect(() => {
    if (params === null) {
      void refreshSlotParameters(slotIndex);
    }
  }, [params, refreshSlotParameters, slotIndex]);

  const visible = useMemo<VstHostParameter[]>(() => {
    if (!params) return [];
    return params.filter(
      (p) =>
        (p.flags & PARAM_FLAG_HIDDEN) === 0 &&
        (p.flags & PARAM_FLAG_READ_ONLY) === 0,
    );
  }, [params]);

  // Per-row throttle of POSTs. Latest pending value is flushed when the
  // throttle window closes, mirroring the typical "send every 33 ms during
  // drag" pattern used elsewhere in Zeus (see DriveSlider).
  const pendingRef = useRef(new Map<number, number>());
  const lastSentAtRef = useRef(new Map<number, number>());
  const flushTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(
    () => () => {
      if (flushTimerRef.current) clearTimeout(flushTimerRef.current);
    },
    [],
  );

  const handleSlider = (paramId: number, value: number) => {
    const now = performance.now();
    const last = lastSentAtRef.current.get(paramId) ?? 0;
    pendingRef.current.set(paramId, value);
    if (now - last >= PARAM_THROTTLE_MS) {
      lastSentAtRef.current.set(paramId, now);
      pendingRef.current.delete(paramId);
      void setSlotParameter(slotIndex, paramId, value);
    } else {
      // Schedule a tail flush so the final drag position lands.
      if (!flushTimerRef.current) {
        flushTimerRef.current = setTimeout(() => {
          flushTimerRef.current = null;
          const pending = pendingRef.current;
          pendingRef.current = new Map();
          for (const [pid, v] of pending) {
            lastSentAtRef.current.set(pid, performance.now());
            void setSlotParameter(slotIndex, pid, v);
          }
        }, PARAM_THROTTLE_MS);
      }
    }
  };

  if (params === null) {
    return (
      <div style={{ padding: 8, fontSize: 11, color: 'var(--fg-2)' }}>
        Loading parameters…
      </div>
    );
  }

  if (visible.length === 0) {
    return (
      <div style={{ padding: 8, fontSize: 11, color: 'var(--fg-2)' }}>
        Plugin exposes no operator-visible parameters. Use the plugin's
        native editor instead.
      </div>
    );
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
      {visible.map((p) => (
        <ParamRow
          key={p.id}
          param={p}
          onChange={(v) => handleSlider(p.id, v)}
        />
      ))}
    </div>
  );
}

function ParamRow({
  param,
  onChange,
}: {
  param: VstHostParameter;
  onChange: (v: number) => void;
}) {
  // Stepped parameters (kIsList / stepCount > 0) use the appropriate step
  // so drags snap to the discrete enum values the plugin exposes.
  const step = param.stepCount > 0 ? 1 / param.stepCount : 0.001;
  const valueText =
    param.stepCount > 0
      ? `${Math.round(param.currentValue * param.stepCount)}/${param.stepCount}`
      : param.currentValue.toFixed(3);

  return (
    <div
      style={{
        display: 'grid',
        gridTemplateColumns: '160px 1fr 60px',
        alignItems: 'center',
        gap: 8,
        fontSize: 11,
      }}
    >
      <div
        style={{
          color: 'var(--fg-1)',
          overflow: 'hidden',
          textOverflow: 'ellipsis',
          whiteSpace: 'nowrap',
        }}
        title={param.units ? `${param.name} (${param.units})` : param.name}
      >
        {param.name}
        {param.units ? (
          <span style={{ color: 'var(--fg-3)', marginLeft: 4 }}>
            {param.units}
          </span>
        ) : null}
      </div>
      <input
        type="range"
        min={0}
        max={1}
        step={step}
        value={param.currentValue}
        onChange={(e) => {
          const v = Number(e.target.value);
          if (Number.isFinite(v)) onChange(Math.max(0, Math.min(1, v)));
        }}
        style={{ width: '100%' }}
      />
      <div
        style={{
          textAlign: 'right',
          color: 'var(--fg-2)',
          fontVariantNumeric: 'tabular-nums',
        }}
      >
        {valueText}
      </div>
    </div>
  );
}
