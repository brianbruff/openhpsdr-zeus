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
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF), with contributions from (DH1KLM); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { useCallback, useEffect, useRef, useState } from 'react';
import { setZoom, ZOOM_MAX, ZOOM_MIN, type ZoomLevel } from '../api/client';
import { useConnectionStore } from '../state/connection-store';

/**
 * Vertical zoom slider pinned to the right edge of the panadapter on mobile.
 * Allows zooming without finger-dragging on the spectrum (which tunes frequency).
 * Hidden on desktop via CSS.
 */
export function MobileZoomSlider() {
  const serverZoom = useConnectionStore((s) => s.zoomLevel);
  const setLocalZoom = useConnectionStore((s) => s.setZoomLevel);
  const applyState = useConnectionStore((s) => s.applyState);
  const connected = useConnectionStore((s) => s.status === 'Connected');

  const [dragValue, setDragValue] = useState<ZoomLevel | null>(null);
  const value = dragValue ?? serverZoom;

  const inflightAbort = useRef<AbortController | null>(null);
  const latestSent = useRef<ZoomLevel>(serverZoom);

  const sendValue = useCallback(
    (v: ZoomLevel) => {
      if (v === latestSent.current) return;
      latestSent.current = v;
      setLocalZoom(v);
      inflightAbort.current?.abort();
      const ac = new AbortController();
      inflightAbort.current = ac;
      setZoom(v, ac.signal)
        .then((next) => {
          if (!ac.signal.aborted) applyState(next);
        })
        .catch(() => {
          /* next state poll will reconcile */
        });
    },
    [applyState, setLocalZoom],
  );

  useEffect(() => () => inflightAbort.current?.abort(), []);

  const commit = () => {
    if (dragValue !== null) sendValue(dragValue);
    setDragValue(null);
  };

  return (
    <div
      className="mobile-zoom-slider"
      style={{
        position: 'absolute',
        right: 0,
        top: 0,
        bottom: 0,
        width: 32,
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        zIndex: 10,
        pointerEvents: connected ? 'auto' : 'none',
        background: 'linear-gradient(90deg, transparent, rgba(255, 160, 40, 0.08))',
        borderLeft: '1px solid rgba(255, 160, 40, 0.15)',
      }}
    >
      <input
        type="range"
        min={ZOOM_MIN}
        max={ZOOM_MAX}
        step={1}
        value={value}
        disabled={!connected}
        onChange={(e) => setDragValue(Number(e.currentTarget.value) as ZoomLevel)}
        onMouseUp={commit}
        onTouchEnd={commit}
        onKeyUp={commit}
        style={{
          writingMode: 'vertical-lr',
          direction: 'rtl',
          height: '80%',
          cursor: 'pointer',
          accentColor: 'var(--accent)',
          opacity: connected ? 1 : 0.3,
        }}
        aria-label="Zoom level"
      />
      <span
        style={{
          position: 'absolute',
          bottom: 8,
          left: '50%',
          transform: 'translateX(-50%)',
          fontSize: 9,
          fontWeight: 700,
          color: 'var(--accent)',
          pointerEvents: 'none',
          textShadow: '0 0 4px rgba(255, 160, 40, 0.6)',
        }}
      >
        {value}×
      </span>
    </div>
  );
}
