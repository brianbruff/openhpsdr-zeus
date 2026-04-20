import { useCallback } from 'react';
import { setBandwidth, setMode, type RxMode } from '../api/client';
import { useConnectionStore } from '../state/connection-store';

type ModeEntry = { value: RxMode; label: string };

const MODES: readonly ModeEntry[] = [
  { value: 'LSB', label: 'LSB' },
  { value: 'USB', label: 'USB' },
  { value: 'CWL', label: 'CWL' },
  { value: 'CWU', label: 'CWU' },
  { value: 'AM', label: 'AM' },
  { value: 'SAM', label: 'SAM' },
  { value: 'DSB', label: 'DSB' },
  { value: 'FM', label: 'FM' },
  { value: 'DIGL', label: 'DIGL' },
  { value: 'DIGU', label: 'DIGU' },
];

type Preset = { label: string; low: number; high: number };

// Per docs/prd/08-display-sync-and-sideband.md §4: sideband-aware presets.
// Upper-sideband modes are strictly positive; lower-sideband are negative;
// double-sideband/AM/FM are symmetric around zero; CW is a narrow symmetric
// pair around the tone pitch (tone handling is server-side).
function presetsFor(mode: RxMode): readonly Preset[] {
  switch (mode) {
    case 'USB':
    case 'DIGU':
      return [
        { label: 'Narrow 2.7k', low: 150, high: 2850 },
        { label: 'Wide 3.0k', low: 150, high: 3200 },
      ];
    case 'LSB':
    case 'DIGL':
      return [
        { label: 'Narrow 2.7k', low: -2850, high: -150 },
        { label: 'Wide 3.0k', low: -3200, high: -150 },
      ];
    case 'AM':
    case 'SAM':
    case 'DSB':
    case 'FM':
      return [
        { label: 'AM 6.6k', low: -3300, high: 3300 },
        { label: 'Wide 8.0k', low: -4000, high: 4000 },
      ];
    case 'CWL':
    case 'CWU':
      return [
        { label: 'CW 250', low: -125, high: 125 },
        { label: 'CW 500', low: -250, high: 250 },
      ];
  }
}

function isActive(p: Preset, low: number, high: number): boolean {
  return p.low === low && p.high === high;
}

export function ModeBandwidth() {
  const mode = useConnectionStore((s) => s.mode);
  const low = useConnectionStore((s) => s.filterLowHz);
  const high = useConnectionStore((s) => s.filterHighHz);
  const applyState = useConnectionStore((s) => s.applyState);

  const selectMode = useCallback(
    (m: RxMode) => {
      if (m === mode) return;
      useConnectionStore.setState({ mode: m });
      setMode(m)
        .then(applyState)
        .catch(() => {
          /* next state poll will reconcile */
        });
    },
    [mode, applyState],
  );

  const selectPreset = useCallback(
    (p: Preset) => {
      if (p.low === low && p.high === high) return;
      useConnectionStore.setState({ filterLowHz: p.low, filterHighHz: p.high });
      setBandwidth(p.low, p.high)
        .then(applyState)
        .catch(() => {
          /* next state poll will reconcile */
        });
    },
    [low, high, applyState],
  );

  const presets = presetsFor(mode);

  return (
    <>
      <div className="ctrl-group">
        <div className="label-xs ctrl-lbl">MODE</div>
        <div className="btn-row wrap" style={{ width: 236 }}>
          {MODES.map((m) => (
            <button
              key={m.value}
              type="button"
              onClick={() => selectMode(m.value)}
              className={`btn sm ${mode === m.value ? 'active' : ''}`}
            >
              {m.label}
            </button>
          ))}
        </div>
      </div>
      <div className="ctrl-group" style={{ minWidth: 220 }}>
        <div className="label-xs ctrl-lbl">BANDWIDTH</div>
        <div className="btn-row" style={{ alignItems: 'center' }}>
          {presets.map((p) => (
            <button
              key={p.label}
              type="button"
              onClick={() => selectPreset(p)}
              className={`btn sm ${isActive(p, low, high) ? 'active' : ''}`}
            >
              {p.label}
            </button>
          ))}
          <span className="label-xs mono" style={{ marginLeft: 6, color: 'var(--fg-3)', whiteSpace: 'nowrap' }}>
            [{Math.min(Math.abs(low), Math.abs(high))}…{Math.max(Math.abs(low), Math.abs(high))} Hz]
          </span>
        </div>
      </div>
    </>
  );
}
