// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Analog S-Meter tile — header (RX/TX tabs + gear), animated dial face,
// gear-flyout config, and footer readout strip. Live data comes from
// useTxStore: rxDbm drives the S scale, fwdWatts drives PO, swr drives SWR.
//
// The needle is driven by a requestAnimationFrame loop that:
//   1. samples raw rxDbm/fwdWatts/swr each frame,
//   2. normalises against the active scale,
//   3. pushes through a moving-average prefilter (cfg.avg samples), then
//   4. through an attack/decay RC ballistic, then
//   5. updates a peak-hold ghost that decays slowly (~5%/s).
//
// We render at rAF rate but only re-render the React tree when the needle
// or peak-hold position changes by ≥ 0.001 of the dial — keeps idle CPU
// flat without making the animation chunky.

import { useEffect, useMemo, useRef, useState } from 'react';
import { useTxStore } from '../../state/tx-store';
import { AnalogMeterFace } from './AnalogMeterFace';
import { AnalogMeterConfig } from './AnalogMeterConfig';
import { AnalogMeterZeusOverlay } from './AnalogMeterZeusOverlay';
import {
  S_SCALE,
  PO_SCALE,
  SWR_SCALE,
  ballistics,
  dbmToS,
  makeAverager,
  sToDbm,
  type Averager,
  type ScaleId,
} from './analogMeterShared';
import { useAnalogMeterStore } from './analogMeterStore';

type Mode = 'rx' | 'tx';

function GearIcon() {
  return (
    <svg
      viewBox="0 0 16 16"
      width={15}
      height={15}
      fill="none"
      stroke="currentColor"
      strokeWidth={1.4}
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      <circle cx="8" cy="8" r="2.2" />
      <path d="M8 1.5v2 M8 12.5v2 M1.5 8h2 M12.5 8h2 M3.3 3.3l1.4 1.4 M11.3 11.3l1.4 1.4 M3.3 12.7l1.4-1.4 M11.3 4.7l1.4-1.4" />
    </svg>
  );
}

interface TileHeaderProps {
  mode: Mode;
  onModeChange: (m: Mode) => void;
  configOpen: boolean;
  onGearClick: () => void;
  onClose?: () => void;
}

function TileHeader({ mode, onModeChange, configOpen, onGearClick, onClose }: TileHeaderProps) {
  return (
    <div className="am-header workspace-tile-header">
      <div className="am-h-left">
        <span className="am-status-dot" />
        <span className="am-h-title">S-METER</span>
      </div>

      <div className="am-h-mode">
        <button
          type="button"
          className={`am-mode-tab ${mode === 'rx' ? 'on' : ''}`}
          onClick={() => onModeChange('rx')}
        >
          RX
        </button>
        <button
          type="button"
          className={`am-mode-tab ${mode === 'tx' ? 'on' : ''}`}
          onClick={() => onModeChange('tx')}
        >
          TX
        </button>
      </div>

      <button
        type="button"
        className={`am-h-gear ${configOpen ? 'on' : ''}`}
        onClick={onGearClick}
        aria-label="Configure meter"
        title="Configure meter"
      >
        <GearIcon />
      </button>

      {onClose && (
        <button
          type="button"
          className="am-h-close workspace-tile-close"
          onClick={onClose}
          aria-label="Close panel"
          title="Close"
        >
          ×
        </button>
      )}
    </div>
  );
}

interface ReadoutStripProps {
  enabled: { s: boolean; po: boolean; swr: boolean };
  values: { s: number; po: number; swr: number };
  showDbm: boolean;
  dbm: number;
  swrAlarm: number;
  activeScaleId: ScaleId;
}

function ReadoutStrip({ enabled, values, showDbm, dbm, swrAlarm, activeScaleId }: ReadoutStripProps) {
  const items: { key: ScaleId; label: string; value: string; active: boolean; danger?: boolean }[] = [];
  if (enabled.s) {
    items.push({
      key: 's',
      label: showDbm ? 'S / dBm' : 'S',
      value: showDbm
        ? `${S_SCALE.fmt(values.s)}  ·  ${Math.round(dbm)} dBm`
        : S_SCALE.fmt(values.s),
      active: activeScaleId === 's',
    });
  }
  if (enabled.po) {
    items.push({
      key: 'po',
      label: 'PO',
      value: PO_SCALE.fmt(values.po),
      active: activeScaleId === 'po',
    });
  }
  if (enabled.swr) {
    items.push({
      key: 'swr',
      label: 'SWR',
      value: SWR_SCALE.fmt(values.swr),
      active: activeScaleId === 'swr',
      danger: values.swr >= swrAlarm,
    });
  }

  if (items.length === 0) {
    return (
      <div className="am-readout-strip">
        <div className="am-ro empty">No scales enabled — open settings ⚙</div>
      </div>
    );
  }

  return (
    <div className="am-readout-strip">
      {items.map((it) => (
        <div key={it.key} className={`am-ro ${it.active ? 'active' : ''} ${it.danger ? 'danger' : ''}`}>
          <div className="am-ro-label">{it.label}</div>
          <div className="am-ro-value">{it.value}</div>
        </div>
      ))}
    </div>
  );
}

export interface AnalogMeterPanelProps {
  /** When provided, a close button is rendered in the tile header. The
   *  layout system injects this for headerless panels. */
  onClose?: () => void;
}

export function AnalogMeterPanel({ onClose }: AnalogMeterPanelProps = {}) {
  const cfg = useAnalogMeterStore();
  const moxOn = useTxStore((s) => s.moxOn);
  const tunOn = useTxStore((s) => s.tunOn);
  const transmitting = moxOn || tunOn;

  const [manualMode, setManualMode] = useState<Mode>('rx');
  const mode: Mode = cfg.followMox ? (transmitting ? 'tx' : 'rx') : manualMode;
  const onModeChange = (m: Mode) => {
    setManualMode(m);
    if (cfg.followMox) cfg.setFollowMox(false);
  };

  const [configOpen, setConfigOpen] = useState(false);

  // Resolve which scale the single physical needle reads — RX prefers S,
  // TX prefers PO if enabled, else SWR. If the operator turned everything
  // off, we still need an `active` to drive the needle math; default to S.
  const enabled = { s: cfg.scaleS, po: cfg.scalePo, swr: cfg.scaleSwr };
  const activeScaleId: ScaleId = useMemo(() => {
    if (mode === 'rx') {
      if (enabled.s) return 's';
      if (enabled.po) return 'po';
      if (enabled.swr) return 'swr';
      return 's';
    }
    if (enabled.po) return 'po';
    if (enabled.swr) return 'swr';
    if (enabled.s) return 's';
    return 'po';
  }, [mode, enabled.s, enabled.po, enabled.swr]);

  // Filtered S-tick subset rendered onto the dial (operator-selectable).
  const customSScale = useMemo(() => {
    return {
      ...S_SCALE,
      ticks: S_SCALE.ticks.filter((t) => cfg.sTicks.includes(t.v)),
    };
  }, [cfg.sTicks]);

  // PO arc respects the operator's full-scale watts setting.
  const dynamicPoScale = useMemo(() => {
    const max = Math.max(10, cfg.poMax);
    return {
      ...PO_SCALE,
      n: (w: number) => Math.min(1, Math.max(0, w) / max),
      fmt: (w: number) => `${w < 10 ? w.toFixed(1) : Math.round(w)} W`,
      fromN: (n: number) => Math.max(0, Math.min(1, n)) * max,
    };
  }, [cfg.poMax]);

  const activeScale = useMemo(() => {
    if (activeScaleId === 's') return customSScale;
    if (activeScaleId === 'po') return dynamicPoScale;
    return SWR_SCALE;
  }, [activeScaleId, customSScale, dynamicPoScale]);

  // Ballistics state. Stored in a ref so the rAF loop can mutate without
  // triggering renders; `tick` is the only state we touch from the loop.
  const stateRef = useRef({
    needleN: 0,
    peakN: 0,
    last: typeof performance !== 'undefined' ? performance.now() : Date.now(),
    rxDbm: -160,
    fwdW: 0,
    swr: 1,
  });
  const avgRef = useRef<Averager>(makeAverager(cfg.avg));
  useEffect(() => {
    avgRef.current.resize(cfg.avg);
  }, [cfg.avg]);

  // Reset peak hold when toggled off.
  useEffect(() => {
    if (!cfg.peakHold) stateRef.current.peakN = 0;
  }, [cfg.peakHold]);

  // [needleN, peakN] published to the face. We update at rAF rate but only
  // call setRender when either crosses a 0.001-of-dial threshold.
  const [render, setRender] = useState({ needleN: 0, peakN: 0 });
  const lastPublishedRef = useRef({ needleN: -1, peakN: -1 });

  useEffect(() => {
    let raf = 0;
    const loop = (now: number) => {
      const s = stateRef.current;
      const dt = Math.min(0.1, (now - s.last) / 1000);
      s.last = now;

      // Pull the latest live readings without subscribing — getState() avoids
      // re-rendering the panel on every store change.
      const tx = useTxStore.getState();
      s.rxDbm = tx.rxDbm;
      s.fwdW = tx.fwdWatts;
      s.swr = tx.swr;

      let raw: number;
      if (activeScaleId === 's') {
        raw = dbmToS(s.rxDbm);
      } else if (activeScaleId === 'po') {
        raw = s.fwdW;
      } else {
        raw = s.swr;
      }
      const targetN = Math.max(0, Math.min(1, activeScale.n(raw)));
      const avgedN = avgRef.current.push(targetN);
      s.needleN = ballistics(s.needleN, avgedN, dt, cfg.attack, cfg.decay);

      if (cfg.peakHold) {
        if (s.needleN > s.peakN) s.peakN = s.needleN;
        else s.peakN = Math.max(s.needleN, s.peakN - dt * 0.05);
      } else {
        s.peakN = 0;
      }

      const last = lastPublishedRef.current;
      if (Math.abs(s.needleN - last.needleN) > 0.001 || Math.abs(s.peakN - last.peakN) > 0.001) {
        lastPublishedRef.current = { needleN: s.needleN, peakN: s.peakN };
        setRender({ needleN: s.needleN, peakN: s.peakN });
      }

      raf = requestAnimationFrame(loop);
    };
    raf = requestAnimationFrame(loop);
    return () => cancelAnimationFrame(raf);
  }, [activeScaleId, activeScale, cfg.attack, cfg.decay, cfg.peakHold]);

  // The face needs the operator-filtered S-arc; we override SCALES.s for the
  // duration of this render via a wrapper that hands AnalogMeterFace the
  // same object shape.
  const scalesForFace = useMemo(
    () => ({ s: customSScale, po: dynamicPoScale, swr: SWR_SCALE }),
    [customSScale, dynamicPoScale],
  );

  // Convert needle position back to scale value for the readout.
  const needleVal = useMemo(
    () => activeScale.fromN(render.needleN),
    [render.needleN, activeScale],
  );

  // Readout values: active scale shows the ballistic-filtered needle reading,
  // others show the raw live values so the footer mirrors the radio's state.
  const rawRxDbm = useTxStore((s) => s.rxDbm);
  const rawFwdW = useTxStore((s) => s.fwdWatts);
  const rawSwr = useTxStore((s) => s.swr);
  const readoutValues = {
    s: activeScaleId === 's' ? needleVal : dbmToS(rawRxDbm),
    po: activeScaleId === 'po' ? needleVal : rawFwdW,
    swr: activeScaleId === 'swr' ? needleVal : rawSwr,
  };
  const dbm = sToDbm(readoutValues.s);

  return (
    <div className="am-tile" data-mode={mode}>
      <TileHeader
        mode={mode}
        onModeChange={onModeChange}
        configOpen={configOpen}
        onGearClick={() => setConfigOpen((o) => !o)}
        onClose={onClose}
      />

      <AnalogMeterConfig open={configOpen} onClose={() => setConfigOpen(false)} />

      <div className="am-face-stack">
        <AnalogMeterFace
          enabledScales={enabled}
          activeScaleId={activeScaleId}
          needleN={render.needleN}
          peakN={cfg.peakHold ? render.peakN : null}
          scales={scalesForFace}
        />
        <AnalogMeterZeusOverlay
          sValue={readoutValues.s}
          active={cfg.zeusMode && enabled.s && activeScaleId === 's'}
        />
      </div>

      <ReadoutStrip
        enabled={enabled}
        values={readoutValues}
        showDbm={cfg.showDbm}
        dbm={dbm}
        swrAlarm={cfg.swrAlarm}
        activeScaleId={activeScaleId}
      />
    </div>
  );
}
