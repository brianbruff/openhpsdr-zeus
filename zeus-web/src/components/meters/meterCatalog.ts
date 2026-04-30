// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// This file is part of the configurable Meters Panel feature. The catalog
// here is the SINGLE source of truth for "what meter readings exist": both
// the Library drawer (operator-facing list of available meters) and the
// runtime selector hook (`useMeterReading`) read from it. Adding a new
// reading is one row in METER_CATALOG plus one branch in `useMeterReading`.
//
// Color discipline (CLAUDE.md, plan §4.6): the only raw hex permitted is
// amber #FFA028, and only for RX signal-strength bar fills + peak-hold
// ticks. Every other widget surface must come from a token in tokens.css.

/** Stable ID per Thetis-supported reading, RX + TX. */
export enum MeterReadingId {
  // RX (RxMetersV2Frame 0x19 + RxMeterFrame 0x14)
  RxSignalPk = 'rx.signal.pk',
  RxSignalAv = 'rx.signal.av',
  RxAdcPk = 'rx.adc.pk',
  RxAdcAv = 'rx.adc.av',
  RxAgcGain = 'rx.agc.gain',
  RxAgcEnvPk = 'rx.agc.env.pk',
  RxAgcEnvAv = 'rx.agc.env.av',
  // TX (TxMetersV2Frame 0x16 — already on wire)
  TxFwdWatts = 'tx.fwd.watts',
  TxRefWatts = 'tx.ref.watts',
  TxSwr = 'tx.swr',
  TxMicPk = 'tx.mic.pk',
  TxMicAv = 'tx.mic.av',
  TxEqPk = 'tx.eq.pk',
  TxEqAv = 'tx.eq.av',
  TxLvlrPk = 'tx.lvlr.pk',
  TxLvlrAv = 'tx.lvlr.av',
  TxLvlrGr = 'tx.lvlr.gr',
  TxCfcPk = 'tx.cfc.pk',
  TxCfcAv = 'tx.cfc.av',
  TxCfcGr = 'tx.cfc.gr',
  TxCompPk = 'tx.comp.pk',
  TxCompAv = 'tx.comp.av',
  TxAlcPk = 'tx.alc.pk',
  TxAlcAv = 'tx.alc.av',
  TxAlcGr = 'tx.alc.gr',
  TxOutPk = 'tx.out.pk',
  TxOutAv = 'tx.out.av',
}

export type MeterUnit = 'dBm' | 'dBFS' | 'dB' | 'W' | 'ratio';

export type MeterCategory =
  | 'rx-signal'
  | 'rx-adc'
  | 'rx-agc'
  | 'tx-power'
  | 'tx-stage'
  | 'tx-protection';

/**
 * Color-token tag — the widget renderer maps this onto a tokens.css variable
 * (or, for `amber-signal`, the amber #FFA028 raw hex permitted only for RX
 * signal-strength fills + peak-hold ticks).
 */
export type MeterColorToken = 'amber-signal' | 'power' | 'tx' | 'accent';

/** Coarse "what kind of widget should I build by default?" recommendation. */
export type MeterDefaultKind = 'hbar' | 'dial' | 'digital' | 'sparkline' | 'vbar';

export interface MeterReadingDef {
  id: MeterReadingId;
  /** Operator-friendly long label, used in the Library drawer + tooltips. */
  label: string;
  /** Compact label for narrow tile widgets and chip-sized widgets. */
  short: string;
  category: MeterCategory;
  unit: MeterUnit;
  /** Default axis range for non-signal widgets. Signal-strength widgets fall
   * back to the SMeter S-unit scale at render time. */
  defaultMin: number;
  defaultMax: number;
  /** Threshold at which a level/protection widget switches to --tx red.
   *  null/undefined means "no danger zone". */
  dangerAt?: number;
  /** Soft-warn threshold (e.g. -6 dBFS for level meters). Widget renders
   *  --power yellow once value crosses this. */
  warnAt?: number;
  /** Color-token tag. Widget falls back to --accent if absent. */
  colorToken: MeterColorToken;
  /** What widget kind the Library drawer creates by default for this reading. */
  defaultKind: MeterDefaultKind;
}

// Convenience factories — keeps the table below readable.
const rxSignal = (
  id: MeterReadingId,
  label: string,
  short: string,
): MeterReadingDef => ({
  id,
  label,
  short,
  category: 'rx-signal',
  unit: 'dBm',
  defaultMin: -127,
  defaultMax: -13,
  colorToken: 'amber-signal',
  defaultKind: 'hbar',
});

const rxAdc = (
  id: MeterReadingId,
  label: string,
  short: string,
): MeterReadingDef => ({
  id,
  label,
  short,
  category: 'rx-adc',
  unit: 'dBFS',
  defaultMin: -100,
  defaultMax: 0,
  warnAt: -12,
  dangerAt: -3,
  colorToken: 'accent',
  defaultKind: 'hbar',
});

const rxAgcEnv = (
  id: MeterReadingId,
  label: string,
  short: string,
): MeterReadingDef => ({
  id,
  label,
  short,
  category: 'rx-agc',
  unit: 'dBm',
  defaultMin: -140,
  defaultMax: 0,
  colorToken: 'accent',
  defaultKind: 'hbar',
});

const txStageLevel = (
  id: MeterReadingId,
  label: string,
  short: string,
): MeterReadingDef => ({
  id,
  label,
  short,
  category: 'tx-stage',
  unit: 'dBFS',
  defaultMin: -30,
  defaultMax: 12,
  warnAt: -6,
  dangerAt: 0,
  colorToken: 'accent',
  defaultKind: 'hbar',
});

const txStageGr = (
  id: MeterReadingId,
  label: string,
  short: string,
): MeterReadingDef => ({
  id,
  label,
  short,
  category: 'tx-protection',
  unit: 'dB',
  defaultMin: 0,
  defaultMax: 25,
  warnAt: 3,
  dangerAt: 10,
  colorToken: 'tx',
  defaultKind: 'hbar',
});

/** Single source of truth: every reading the Meters Panel can render. */
export const METER_CATALOG: Record<MeterReadingId, MeterReadingDef> = {
  // ---- RX ----
  [MeterReadingId.RxSignalPk]: rxSignal(
    MeterReadingId.RxSignalPk,
    'RX Signal (Pk)',
    'S Pk',
  ),
  [MeterReadingId.RxSignalAv]: rxSignal(
    MeterReadingId.RxSignalAv,
    'RX Signal (Avg)',
    'S Av',
  ),
  [MeterReadingId.RxAdcPk]: rxAdc(MeterReadingId.RxAdcPk, 'ADC Input (Pk)', 'ADC Pk'),
  [MeterReadingId.RxAdcAv]: rxAdc(MeterReadingId.RxAdcAv, 'ADC Input (Avg)', 'ADC Av'),
  [MeterReadingId.RxAgcGain]: {
    id: MeterReadingId.RxAgcGain,
    label: 'AGC Gain',
    short: 'AGC',
    category: 'rx-agc',
    unit: 'dB',
    // Signed swing: −80 (deep cut) … +60 (deep boost). Centre at zero.
    defaultMin: -40,
    defaultMax: 60,
    colorToken: 'accent',
    defaultKind: 'hbar',
  },
  [MeterReadingId.RxAgcEnvPk]: rxAgcEnv(
    MeterReadingId.RxAgcEnvPk,
    'AGC Envelope (Pk)',
    'AGC Pk',
  ),
  [MeterReadingId.RxAgcEnvAv]: rxAgcEnv(
    MeterReadingId.RxAgcEnvAv,
    'AGC Envelope (Avg)',
    'AGC Av',
  ),
  // ---- TX power / SWR ----
  [MeterReadingId.TxFwdWatts]: {
    id: MeterReadingId.TxFwdWatts,
    label: 'TX Forward Power',
    short: 'FWD W',
    category: 'tx-power',
    unit: 'W',
    defaultMin: 0,
    defaultMax: 5,
    warnAt: 4.5,
    dangerAt: 5,
    colorToken: 'power',
    defaultKind: 'dial',
  },
  [MeterReadingId.TxRefWatts]: {
    id: MeterReadingId.TxRefWatts,
    label: 'TX Reverse Power',
    short: 'REF W',
    category: 'tx-power',
    unit: 'W',
    defaultMin: 0,
    defaultMax: 1,
    warnAt: 0.25,
    dangerAt: 0.5,
    colorToken: 'tx',
    defaultKind: 'hbar',
  },
  [MeterReadingId.TxSwr]: {
    id: MeterReadingId.TxSwr,
    label: 'SWR',
    short: 'SWR',
    category: 'tx-power',
    unit: 'ratio',
    defaultMin: 1,
    defaultMax: 3,
    warnAt: 1.5,
    dangerAt: 2,
    colorToken: 'tx',
    defaultKind: 'digital',
  },
  // ---- TX stage levels ----
  [MeterReadingId.TxMicPk]: txStageLevel(
    MeterReadingId.TxMicPk,
    'Mic (Pk)',
    'MIC Pk',
  ),
  [MeterReadingId.TxMicAv]: txStageLevel(
    MeterReadingId.TxMicAv,
    'Mic (Avg)',
    'MIC Av',
  ),
  [MeterReadingId.TxEqPk]: txStageLevel(
    MeterReadingId.TxEqPk,
    'EQ Output (Pk)',
    'EQ Pk',
  ),
  [MeterReadingId.TxEqAv]: txStageLevel(
    MeterReadingId.TxEqAv,
    'EQ Output (Avg)',
    'EQ Av',
  ),
  [MeterReadingId.TxLvlrPk]: txStageLevel(
    MeterReadingId.TxLvlrPk,
    'Leveler (Pk)',
    'LVLR Pk',
  ),
  [MeterReadingId.TxLvlrAv]: txStageLevel(
    MeterReadingId.TxLvlrAv,
    'Leveler (Avg)',
    'LVLR Av',
  ),
  [MeterReadingId.TxLvlrGr]: txStageGr(
    MeterReadingId.TxLvlrGr,
    'Leveler Gain Reduction',
    'LVLR GR',
  ),
  [MeterReadingId.TxCfcPk]: txStageLevel(
    MeterReadingId.TxCfcPk,
    'CFC (Pk)',
    'CFC Pk',
  ),
  [MeterReadingId.TxCfcAv]: txStageLevel(
    MeterReadingId.TxCfcAv,
    'CFC (Avg)',
    'CFC Av',
  ),
  [MeterReadingId.TxCfcGr]: txStageGr(
    MeterReadingId.TxCfcGr,
    'CFC Gain Reduction',
    'CFC GR',
  ),
  [MeterReadingId.TxCompPk]: txStageLevel(
    MeterReadingId.TxCompPk,
    'Compressor (Pk)',
    'COMP Pk',
  ),
  [MeterReadingId.TxCompAv]: txStageLevel(
    MeterReadingId.TxCompAv,
    'Compressor (Avg)',
    'COMP Av',
  ),
  [MeterReadingId.TxAlcPk]: txStageLevel(
    MeterReadingId.TxAlcPk,
    'ALC (Pk)',
    'ALC Pk',
  ),
  [MeterReadingId.TxAlcAv]: txStageLevel(
    MeterReadingId.TxAlcAv,
    'ALC (Avg)',
    'ALC Av',
  ),
  [MeterReadingId.TxAlcGr]: txStageGr(
    MeterReadingId.TxAlcGr,
    'ALC Gain Reduction',
    'ALC GR',
  ),
  [MeterReadingId.TxOutPk]: txStageLevel(
    MeterReadingId.TxOutPk,
    'Final Output (Pk)',
    'OUT Pk',
  ),
  [MeterReadingId.TxOutAv]: txStageLevel(
    MeterReadingId.TxOutAv,
    'Final Output (Avg)',
    'OUT Av',
  ),
};

/** Library-drawer filter chips, in display order. */
export type MeterFilter = 'all' | 'rx' | 'tx' | 'power' | 'stage' | 'agc';

export const METER_FILTERS: ReadonlyArray<MeterFilter> = [
  'all',
  'rx',
  'tx',
  'power',
  'stage',
  'agc',
];

/** Whether a catalog entry matches the given Library-drawer filter chip. */
export function meterMatchesFilter(
  def: MeterReadingDef,
  filter: MeterFilter,
): boolean {
  switch (filter) {
    case 'all':
      return true;
    case 'rx':
      return (
        def.category === 'rx-signal' ||
        def.category === 'rx-adc' ||
        def.category === 'rx-agc'
      );
    case 'tx':
      return (
        def.category === 'tx-power' ||
        def.category === 'tx-stage' ||
        def.category === 'tx-protection'
      );
    case 'power':
      return def.category === 'tx-power';
    case 'stage':
      return def.category === 'tx-stage' || def.category === 'tx-protection';
    case 'agc':
      return def.category === 'rx-agc';
  }
}

/** Ordered list of all readings (matches enum declaration order). */
export const METER_READINGS: ReadonlyArray<MeterReadingDef> = Object.values(
  METER_CATALOG,
);
