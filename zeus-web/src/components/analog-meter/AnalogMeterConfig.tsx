// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Header gear → slide-down config flyout. Three sections: RX (S-meter
// ticks + dBm), TX (PO/SWR pills + full-scale + alarm), and Ballistics
// (attack/decay/avg/peak hold). All controls drive the persisted
// useAnalogMeterStore — operator changes survive a reload.

import { ALL_S_TICKS, useAnalogMeterStore } from './analogMeterStore';

interface SliderProps {
  label: string;
  value: number;
  min: number;
  max: number;
  step: number;
  unit: string;
  hint?: string;
  onChange: (v: number) => void;
}

function Slider({ label, value, min, max, step, unit, hint, onChange }: SliderProps) {
  const fixed = step < 1 ? 2 : 0;
  return (
    <div className="am-slider">
      <div className="am-slider-head">
        <span className="am-slider-lbl">{label}</span>
        <span className="am-slider-val">
          {value.toFixed(fixed)}
          <em>{unit}</em>
        </span>
      </div>
      <input
        type="range"
        min={min}
        max={max}
        step={step}
        value={value}
        onChange={(e) => onChange(parseFloat(e.target.value))}
      />
      {hint && <div className="am-slider-hint">{hint}</div>}
    </div>
  );
}

interface CheckRowProps {
  checked: boolean;
  onChange: (v: boolean) => void;
  children: React.ReactNode;
  sub?: string;
}

function CheckRow({ checked, onChange, children, sub }: CheckRowProps) {
  return (
    <label className="am-check">
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} />
      <span className="am-check-box" />
      <span className="am-check-body">
        <span className="am-check-lbl">{children}</span>
        {sub && <span className="am-check-sub">{sub}</span>}
      </span>
    </label>
  );
}

interface PillProps {
  on: boolean;
  onClick: () => void;
  children: React.ReactNode;
}

function Pill({ on, onClick, children }: PillProps) {
  return (
    <button type="button" className={`am-pill ${on ? 'on' : ''}`} onClick={onClick}>
      {children}
    </button>
  );
}

const labelForS = (v: number): string => (v <= 9 ? `S${v}` : `+${(v - 9) * 10}`);

interface AnalogMeterConfigProps {
  open: boolean;
  onClose: () => void;
}

export function AnalogMeterConfig({ open, onClose }: AnalogMeterConfigProps) {
  const cfg = useAnalogMeterStore();

  return (
    <div className={`am-config ${open ? 'open' : ''}`} aria-hidden={!open}>
      <div className="am-cf-grid">
        <section className="am-cf-sect">
          <header>
            <h4>RX · Receiver</h4>
            <CheckRow checked={cfg.scaleS} onChange={(v) => cfg.setScale('s', v)}>
              Show S-meter
            </CheckRow>
          </header>
          <div className="am-cf-body">
            <div className="am-cf-sublbl">S-units shown on dial</div>
            <div className="am-tickgrid">
              {ALL_S_TICKS.map((v) => (
                <button
                  type="button"
                  key={v}
                  className={`am-tick ${cfg.sTicks.includes(v) ? 'on' : ''}`}
                  onClick={() => cfg.toggleSTick(v)}
                  disabled={!cfg.scaleS}
                >
                  {labelForS(v)}
                </button>
              ))}
            </div>
            <CheckRow
              checked={cfg.showDbm}
              onChange={cfg.setShowDbm}
              sub="Append calibrated dBm to the S readout"
            >
              Show dBm
            </CheckRow>
            <CheckRow
              checked={cfg.zeusMode}
              onChange={cfg.setZeusMode}
              sub="Image fades in past S9, lightning crackles at S9+20"
            >
              Zeus mode
            </CheckRow>
          </div>
        </section>

        <section className="am-cf-sect">
          <header>
            <h4>TX · Transmitter</h4>
            <div className="am-cf-pillrow">
              <Pill on={cfg.scalePo} onClick={() => cfg.setScale('po', !cfg.scalePo)}>
                Power
              </Pill>
              <Pill on={cfg.scaleSwr} onClick={() => cfg.setScale('swr', !cfg.scaleSwr)}>
                SWR
              </Pill>
            </div>
          </header>
          <div className="am-cf-body">
            <Slider
              label="SWR alarm"
              value={cfg.swrAlarm}
              min={1.5}
              max={5}
              step={0.1}
              unit=":1"
              onChange={cfg.setSwrAlarm}
              hint="Readout turns red above this"
            />
            <div className="am-slider-hint">
              PA full-scale tracks the rated PA power from the PA Settings panel
              (max of 10 W or 120% of rated).
            </div>
          </div>
        </section>

        <section className="am-cf-sect">
          <header>
            <h4>Needle ballistics</h4>
            <button type="button" className="am-reset" onClick={cfg.resetBallistics}>
              Reset
            </button>
          </header>
          <div className="am-cf-body">
            <Slider
              label="Attack"
              value={cfg.attack}
              min={0.005}
              max={0.5}
              step={0.005}
              unit=" s"
              onChange={cfg.setAttack}
              hint="Time constant on rising signals"
            />
            <Slider
              label="Decay"
              value={cfg.decay}
              min={0.05}
              max={2}
              step={0.05}
              unit=" s"
              onChange={cfg.setDecay}
              hint="Time constant on falling signals"
            />
            <Slider
              label="Averaging"
              value={cfg.avg}
              min={1}
              max={32}
              step={1}
              unit=" smp"
              onChange={cfg.setAvg}
              hint="Moving-average window before ballistics"
            />
            <CheckRow
              checked={cfg.peakHold}
              onChange={cfg.setPeakHold}
              sub="Slow-decaying ghost needle marks recent peak"
            >
              Peak hold
            </CheckRow>
          </div>
        </section>
      </div>

      <div className="am-cf-foot">
        <span className="am-cf-foot-hint">Click ⚙ in the header to close.</span>
        <button type="button" className="am-done" onClick={onClose}>
          Done
        </button>
      </div>
    </div>
  );
}
