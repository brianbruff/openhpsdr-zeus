// SPDX-License-Identifier: GPL-2.0-or-later
//
// Thetis default filter preset tables from console.cs:5182–5585.
// Reference: docs/proposals/research/thetis-filter-ux.md §2.
// Numbers are signed Hz, VFO-relative. CW uses default cw_pitch=600.
// DIGL/DIGU use default offset=0.

import type { RxMode } from '../../api/client';

export type FilterPresetSlot = {
  slotName: string;
  label: string;
  lowHz: number;
  highHz: number;
  isVar: boolean;
};

const CW_PITCH = 600;

const LSB: readonly FilterPresetSlot[] = [
  { slotName: 'F1',   label: '5.0k',  lowHz: -5100, highHz: -100,  isVar: false },
  { slotName: 'F2',   label: '4.4k',  lowHz: -4500, highHz: -100,  isVar: false },
  { slotName: 'F3',   label: '3.8k',  lowHz: -3900, highHz: -100,  isVar: false },
  { slotName: 'F4',   label: '3.3k',  lowHz: -3400, highHz: -100,  isVar: false },
  { slotName: 'F5',   label: '2.9k',  lowHz: -3000, highHz: -100,  isVar: false },
  { slotName: 'F6',   label: '2.7k',  lowHz: -2800, highHz: -100,  isVar: false },
  { slotName: 'F7',   label: '2.4k',  lowHz: -2500, highHz: -100,  isVar: false },
  { slotName: 'F8',   label: '2.1k',  lowHz: -2200, highHz: -100,  isVar: false },
  { slotName: 'F9',   label: '1.8k',  lowHz: -1900, highHz: -100,  isVar: false },
  { slotName: 'F10',  label: '1.0k',  lowHz: -1100, highHz: -100,  isVar: false },
  { slotName: 'VAR1', label: 'Var 1', lowHz: -2800, highHz: -100,  isVar: true  },
  { slotName: 'VAR2', label: 'Var 2', lowHz: -2800, highHz: -100,  isVar: true  },
];

const USB: readonly FilterPresetSlot[] = [
  { slotName: 'F1',   label: '5.0k',  lowHz:  100, highHz: 5100,  isVar: false },
  { slotName: 'F2',   label: '4.4k',  lowHz:  100, highHz: 4500,  isVar: false },
  { slotName: 'F3',   label: '3.8k',  lowHz:  100, highHz: 3900,  isVar: false },
  { slotName: 'F4',   label: '3.3k',  lowHz:  100, highHz: 3400,  isVar: false },
  { slotName: 'F5',   label: '2.9k',  lowHz:  100, highHz: 3000,  isVar: false },
  { slotName: 'F6',   label: '2.7k',  lowHz:  100, highHz: 2800,  isVar: false },
  { slotName: 'F7',   label: '2.4k',  lowHz:  100, highHz: 2500,  isVar: false },
  { slotName: 'F8',   label: '2.1k',  lowHz:  100, highHz: 2200,  isVar: false },
  { slotName: 'F9',   label: '1.8k',  lowHz:  100, highHz: 1900,  isVar: false },
  { slotName: 'F10',  label: '1.0k',  lowHz:  100, highHz: 1100,  isVar: false },
  { slotName: 'VAR1', label: 'Var 1', lowHz:  100, highHz: 2800,  isVar: true  },
  { slotName: 'VAR2', label: 'Var 2', lowHz:  100, highHz: 2800,  isVar: true  },
];

const CWL: readonly FilterPresetSlot[] = [
  { slotName: 'F1',   label: '1.0k',  lowHz: -(CW_PITCH + 500), highHz: -(CW_PITCH - 500), isVar: false },
  { slotName: 'F2',   label: '800',   lowHz: -(CW_PITCH + 400), highHz: -(CW_PITCH - 400), isVar: false },
  { slotName: 'F3',   label: '600',   lowHz: -(CW_PITCH + 300), highHz: -(CW_PITCH - 300), isVar: false },
  { slotName: 'F4',   label: '500',   lowHz: -(CW_PITCH + 250), highHz: -(CW_PITCH - 250), isVar: false },
  { slotName: 'F5',   label: '400',   lowHz: -(CW_PITCH + 200), highHz: -(CW_PITCH - 200), isVar: false },
  { slotName: 'F6',   label: '250',   lowHz: -(CW_PITCH + 125), highHz: -(CW_PITCH - 125), isVar: false },
  { slotName: 'F7',   label: '150',   lowHz: -(CW_PITCH +  75), highHz: -(CW_PITCH -  75), isVar: false },
  { slotName: 'F8',   label: '100',   lowHz: -(CW_PITCH +  50), highHz: -(CW_PITCH -  50), isVar: false },
  { slotName: 'F9',   label: '50',    lowHz: -(CW_PITCH +  25), highHz: -(CW_PITCH -  25), isVar: false },
  { slotName: 'F10',  label: '25',    lowHz: -(CW_PITCH +  13), highHz: -(CW_PITCH -  13), isVar: false },
  { slotName: 'VAR1', label: 'Var 1', lowHz: -(CW_PITCH + 250), highHz: -(CW_PITCH - 250), isVar: true  },
  { slotName: 'VAR2', label: 'Var 2', lowHz: -(CW_PITCH + 250), highHz: -(CW_PITCH - 250), isVar: true  },
];

const CWU: readonly FilterPresetSlot[] = [
  { slotName: 'F1',   label: '1.0k',  lowHz: CW_PITCH - 500, highHz: CW_PITCH + 500, isVar: false },
  { slotName: 'F2',   label: '800',   lowHz: CW_PITCH - 400, highHz: CW_PITCH + 400, isVar: false },
  { slotName: 'F3',   label: '600',   lowHz: CW_PITCH - 300, highHz: CW_PITCH + 300, isVar: false },
  { slotName: 'F4',   label: '500',   lowHz: CW_PITCH - 250, highHz: CW_PITCH + 250, isVar: false },
  { slotName: 'F5',   label: '400',   lowHz: CW_PITCH - 200, highHz: CW_PITCH + 200, isVar: false },
  { slotName: 'F6',   label: '250',   lowHz: CW_PITCH - 125, highHz: CW_PITCH + 125, isVar: false },
  { slotName: 'F7',   label: '150',   lowHz: CW_PITCH -  75, highHz: CW_PITCH +  75, isVar: false },
  { slotName: 'F8',   label: '100',   lowHz: CW_PITCH -  50, highHz: CW_PITCH +  50, isVar: false },
  { slotName: 'F9',   label: '50',    lowHz: CW_PITCH -  25, highHz: CW_PITCH +  25, isVar: false },
  { slotName: 'F10',  label: '25',    lowHz: CW_PITCH -  13, highHz: CW_PITCH +  13, isVar: false },
  { slotName: 'VAR1', label: 'Var 1', lowHz: CW_PITCH - 250, highHz: CW_PITCH + 250, isVar: true  },
  { slotName: 'VAR2', label: 'Var 2', lowHz: CW_PITCH - 250, highHz: CW_PITCH + 250, isVar: true  },
];

const AM: readonly FilterPresetSlot[] = [
  { slotName: 'F1',   label: '20k',   lowHz: -10000, highHz: 10000, isVar: false },
  { slotName: 'F2',   label: '18k',   lowHz:  -9000, highHz:  9000, isVar: false },
  { slotName: 'F3',   label: '16k',   lowHz:  -8000, highHz:  8000, isVar: false },
  { slotName: 'F4',   label: '12k',   lowHz:  -6000, highHz:  6000, isVar: false },
  { slotName: 'F5',   label: '10k',   lowHz:  -5000, highHz:  5000, isVar: false },
  { slotName: 'F6',   label: '9.0k',  lowHz:  -4500, highHz:  4500, isVar: false },
  { slotName: 'F7',   label: '8.0k',  lowHz:  -4000, highHz:  4000, isVar: false },
  { slotName: 'F8',   label: '7.0k',  lowHz:  -3500, highHz:  3500, isVar: false },
  { slotName: 'F9',   label: '6.0k',  lowHz:  -3000, highHz:  3000, isVar: false },
  { slotName: 'F10',  label: '5.0k',  lowHz:  -2500, highHz:  2500, isVar: false },
  { slotName: 'VAR1', label: 'Var 1', lowHz:  -3000, highHz:  3000, isVar: true  },
  { slotName: 'VAR2', label: 'Var 2', lowHz:  -3000, highHz:  3000, isVar: true  },
];

const DSB: readonly FilterPresetSlot[] = [
  { slotName: 'F1',   label: '16k',   lowHz:  -8000, highHz:  8000, isVar: false },
  { slotName: 'F2',   label: '12k',   lowHz:  -6000, highHz:  6000, isVar: false },
  { slotName: 'F3',   label: '10k',   lowHz:  -5000, highHz:  5000, isVar: false },
  { slotName: 'F4',   label: '8.0k',  lowHz:  -4000, highHz:  4000, isVar: false },
  { slotName: 'F5',   label: '6.6k',  lowHz:  -3300, highHz:  3300, isVar: false },
  { slotName: 'F6',   label: '5.2k',  lowHz:  -2600, highHz:  2600, isVar: false },
  { slotName: 'F7',   label: '4.0k',  lowHz:  -2000, highHz:  2000, isVar: false },
  { slotName: 'F8',   label: '3.1k',  lowHz:  -1550, highHz:  1550, isVar: false },
  { slotName: 'F9',   label: '2.9k',  lowHz:  -1450, highHz:  1450, isVar: false },
  { slotName: 'F10',  label: '2.4k',  lowHz:  -1200, highHz:  1200, isVar: false },
  { slotName: 'VAR1', label: 'Var 1', lowHz:  -3300, highHz:  3300, isVar: true  },
  { slotName: 'VAR2', label: 'Var 2', lowHz:  -3300, highHz:  3300, isVar: true  },
];

// DIGL/DIGU centered on offset=0 (default). Symmetric because offset defaults to 0.
const DIGL: readonly FilterPresetSlot[] = [
  { slotName: 'F1',   label: '3.0k',  lowHz:  -1500, highHz:  1500, isVar: false },
  { slotName: 'F2',   label: '2.5k',  lowHz:  -1250, highHz:  1250, isVar: false },
  { slotName: 'F3',   label: '2.0k',  lowHz:  -1000, highHz:  1000, isVar: false },
  { slotName: 'F4',   label: '1.5k',  lowHz:   -750, highHz:   750, isVar: false },
  { slotName: 'F5',   label: '1.0k',  lowHz:   -500, highHz:   500, isVar: false },
  { slotName: 'F6',   label: '800',   lowHz:   -400, highHz:   400, isVar: false },
  { slotName: 'F7',   label: '600',   lowHz:   -300, highHz:   300, isVar: false },
  { slotName: 'F8',   label: '300',   lowHz:   -150, highHz:   150, isVar: false },
  { slotName: 'F9',   label: '150',   lowHz:    -75, highHz:    75, isVar: false },
  { slotName: 'F10',  label: '75',    lowHz:    -38, highHz:    38, isVar: false },
  { slotName: 'VAR1', label: 'Var 1', lowHz:   -400, highHz:   400, isVar: true  },
  { slotName: 'VAR2', label: 'Var 2', lowHz:   -400, highHz:   400, isVar: true  },
];

// FM has no presets in Thetis.
const FM: readonly FilterPresetSlot[] = [];

const PRESET_MAP: Record<RxMode, readonly FilterPresetSlot[]> = {
  LSB:  LSB,
  USB:  USB,
  CWL:  CWL,
  CWU:  CWU,
  AM:   AM,
  SAM:  AM,  // SAM uses identical table to AM
  DSB:  DSB,
  DIGL: DIGL,
  DIGU: DIGL, // DIGU uses identical half-widths to DIGL
  FM:   FM,
};

export function getPresetsForMode(mode: RxMode): readonly FilterPresetSlot[] {
  return PRESET_MAP[mode] ?? USB;
}

export function formatFilterWidth(lowHz: number, highHz: number): string {
  const width = Math.abs(highHz - lowHz);
  if (width >= 1000) {
    const khz = width / 1000;
    return `${khz % 1 === 0 ? khz.toFixed(0) : khz.toFixed(1)} kHz`;
  }
  return `${width} Hz`;
}

// Format a passband width for the ribbon's PASSBAND readout.
// Always 2-decimal kHz (e.g. "2.70 kHz") to match mockup precision.
export function formatRibbonWidth(lowHz: number, highHz: number): string {
  const width = Math.abs(highHz - lowHz);
  return `${(width / 1000).toFixed(2)} kHz`;
}

// Format an absolute Hz frequency as "MM.kkk.hhh" (MHz.kHz-3.Hz-3). Matches
// the mockup's LOW CUT / HIGH CUT columns (e.g. "14.254.650").
export function formatAbsFreq(hz: number): string {
  const abs = Math.abs(Math.round(hz));
  const mhz = Math.floor(abs / 1_000_000);
  const khzPart = Math.floor((abs - mhz * 1_000_000) / 1000);
  const hzPart = abs - mhz * 1_000_000 - khzPart * 1000;
  const sign = hz < 0 ? '-' : '';
  return `${sign}${mhz}.${String(khzPart).padStart(3, '0')}.${String(hzPart).padStart(3, '0')}`;
}

// Ribbon's six-preset subset. PRD §3.2 "a 3×2 grid of chip buttons". We map
// each ribbon width to the nearest real F-slot for the active mode so the
// chip is a second view of the compact panel's row, not a parallel preset
// system. When no exact F-slot match exists we pick the closest by width.
const RIBBON_SLOT_NAMES_BY_MODE: Record<RxMode, readonly string[]> = {
  // SSB: a spread of tight-to-AM-wide SSB widths.
  USB: ['F10', 'F7', 'F6', 'F5', 'F3', 'F1'],
  LSB: ['F10', 'F7', 'F6', 'F5', 'F3', 'F1'],
  // CW: narrow-to-wide CW widths.
  CWU: ['F10', 'F9', 'F8', 'F7', 'F6', 'F4'],
  CWL: ['F10', 'F9', 'F8', 'F7', 'F6', 'F4'],
  // AM/SAM: wide-audio widths.
  AM:  ['F10', 'F9', 'F7', 'F5', 'F3', 'F1'],
  SAM: ['F10', 'F9', 'F7', 'F5', 'F3', 'F1'],
  DSB: ['F10', 'F7', 'F5', 'F3', 'F2', 'F1'],
  DIGL: ['F10', 'F8', 'F5', 'F3', 'F2', 'F1'],
  DIGU: ['F10', 'F8', 'F5', 'F3', 'F2', 'F1'],
  FM:  [],
};

// Return the 6 ribbon-chip slots for the active mode. Falls back gracefully
// when the mode has no table (FM returns [] — caller hides the grid).
export function getRibbonPresetsForMode(mode: RxMode): readonly FilterPresetSlot[] {
  const names = RIBBON_SLOT_NAMES_BY_MODE[mode] ?? RIBBON_SLOT_NAMES_BY_MODE.USB;
  const table = getPresetsForMode(mode);
  return names.flatMap((n) => {
    const hit = table.find((s) => s.slotName === n);
    return hit ? [hit] : [];
  });
}

// Per-mode nudge step for edge adjustments (keyboard arrow keys in the ribbon,
// eventually the compact-panel Lo/Hi pairs in Phase 5). PRD §3.2.1 /
// §3.4 open question — defaults are: SSB 10 Hz, CW 10 Hz, AM/SAM/DSB 100 Hz,
// DIGL/DIGU 50 Hz.
export function nudgeStepHz(mode: RxMode): number {
  switch (mode) {
    case 'USB': case 'LSB': case 'CWU': case 'CWL': return 10;
    case 'DIGL': case 'DIGU': return 50;
    case 'AM': case 'SAM': case 'DSB': case 'FM': return 100;
    default: return 10;
  }
}
