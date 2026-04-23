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
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { warnOnce } from '../util/logger';

export type ConnectionStatus =
  | 'Disconnected'
  | 'Connecting'
  | 'Connected'
  | 'Error';

export type RxMode =
  | 'LSB'
  | 'USB'
  | 'CWL'
  | 'CWU'
  | 'AM'
  | 'FM'
  | 'SAM'
  | 'DSB'
  | 'DIGL'
  | 'DIGU';

export type NrMode = 'Off' | 'Anr' | 'Emnr';
export type NbMode = 'Off' | 'Nb1' | 'Nb2';

export type NrConfigDto = {
  nrMode: NrMode;
  anfEnabled: boolean;
  snbEnabled: boolean;
  nbpNotchesEnabled: boolean;
  nbMode: NbMode;
  nbThreshold: number;
};

export const NR_CONFIG_DEFAULT: NrConfigDto = {
  nrMode: 'Off',
  anfEnabled: false,
  snbEnabled: false,
  nbpNotchesEnabled: false,
  nbMode: 'Off',
  nbThreshold: 20,
};

// Integer 1..8. Backend accepts up to 16 (SyntheticDspEngine.MaxZoomLevel)
// but anything past 8 doesn't visibly narrow the span further at current
// pan widths — capping the slider here keeps the control honest.
export type ZoomLevel = number;
export const ZOOM_MIN: ZoomLevel = 1;
export const ZOOM_MAX: ZoomLevel = 8;

export type RadioStateDto = {
  status: ConnectionStatus;
  endpoint: string | null;
  vfoHz: number;
  mode: RxMode;
  filterLowHz: number;
  filterHighHz: number;
  sampleRate: number;
  agcTopDb: number;
  attenDb: number;
  autoAttEnabled: boolean;
  attOffsetDb: number;
  adcOverloadWarning: boolean;
  nr: NrConfigDto;
  zoomLevel: ZoomLevel;
};

export type RadioInfoDto = {
  macAddress: string;
  ipAddress: string;
  boardId: string;
  firmwareVersion: string;
  busy: boolean;
  details: Record<string, string> | null;
};

export type ConnectRequest = {
  endpoint: string;
  sampleRate: number;
  preampOn?: boolean;
  // Server accepts 0..3 (→ 0/10/20/30 dB attenuation).
  atten?: number;
};

// System.Text.Json can serialize enums as either numbers (default) or strings
// (with JsonStringEnumConverter). Accept both so the client stays robust to
// server config drift.
const STATUS_ORDER: readonly ConnectionStatus[] = [
  'Disconnected',
  'Connecting',
  'Connected',
  'Error',
];

const MODE_ORDER: readonly RxMode[] = [
  'LSB',
  'USB',
  'CWL',
  'CWU',
  'AM',
  'FM',
  'SAM',
  'DSB',
  'DIGL',
  'DIGU',
];

const NR_MODE_ORDER: readonly NrMode[] = ['Off', 'Anr', 'Emnr'];
const NB_MODE_ORDER: readonly NbMode[] = ['Off', 'Nb1', 'Nb2'];

export function normalizeStatus(v: unknown): ConnectionStatus {
  if (typeof v === 'string') {
    return (STATUS_ORDER as readonly string[]).includes(v)
      ? (v as ConnectionStatus)
      : 'Error';
  }
  if (typeof v === 'number' && Number.isInteger(v)) {
    return STATUS_ORDER[v] ?? 'Error';
  }
  return 'Error';
}

export function normalizeMode(v: unknown): RxMode {
  if (typeof v === 'string') {
    return (MODE_ORDER as readonly string[]).includes(v)
      ? (v as RxMode)
      : 'USB';
  }
  if (typeof v === 'number' && Number.isInteger(v)) {
    return MODE_ORDER[v] ?? 'USB';
  }
  return 'USB';
}

export function normalizeNrMode(v: unknown): NrMode {
  if (typeof v === 'string') {
    return (NR_MODE_ORDER as readonly string[]).includes(v)
      ? (v as NrMode)
      : 'Off';
  }
  if (typeof v === 'number' && Number.isInteger(v)) {
    return NR_MODE_ORDER[v] ?? 'Off';
  }
  return 'Off';
}

export function normalizeNbMode(v: unknown): NbMode {
  if (typeof v === 'string') {
    return (NB_MODE_ORDER as readonly string[]).includes(v)
      ? (v as NbMode)
      : 'Off';
  }
  if (typeof v === 'number' && Number.isInteger(v)) {
    return NB_MODE_ORDER[v] ?? 'Off';
  }
  return 'Off';
}

export function normalizeNr(raw: unknown): NrConfigDto {
  if (!raw || typeof raw !== 'object') return { ...NR_CONFIG_DEFAULT };
  const r = raw as Record<string, unknown>;
  return {
    nrMode: normalizeNrMode(r.nrMode),
    anfEnabled: Boolean(r.anfEnabled),
    snbEnabled: Boolean(r.snbEnabled),
    nbpNotchesEnabled: Boolean(r.nbpNotchesEnabled),
    nbMode: normalizeNbMode(r.nbMode),
    nbThreshold:
      typeof r.nbThreshold === 'number'
        ? r.nbThreshold
        : NR_CONFIG_DEFAULT.nbThreshold,
  };
}

export function normalizeState(raw: unknown): RadioStateDto {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    status: normalizeStatus(r.status),
    endpoint: typeof r.endpoint === 'string' ? r.endpoint : null,
    vfoHz: typeof r.vfoHz === 'number' ? r.vfoHz : 0,
    mode: normalizeMode(r.mode),
    filterLowHz: typeof r.filterLowHz === 'number' ? r.filterLowHz : 0,
    filterHighHz: typeof r.filterHighHz === 'number' ? r.filterHighHz : 0,
    sampleRate: typeof r.sampleRate === 'number' ? r.sampleRate : 0,
    // Default 80 matches WdspDspEngine.ApplyAgcDefaults and the Thetis
    // AGC_MEDIUM preset. Missing from older servers — tolerate absence.
    agcTopDb: typeof r.agcTopDb === 'number' ? r.agcTopDb : 80,
    // Attenuator value in dB, range 0..31 (HpsdrAtten.MaxDb). 4-button UI
    // sends 0/10/20/30 today; #23 will unlock the full fine-grained range.
    attenDb: typeof r.attenDb === 'number' ? r.attenDb : 0,
    // Auto-ATT control loop (server default ON); offset added to attenDb on
    // the hardware. adcOverloadWarning is OR'd across both ADCs with a small
    // hysteresis — flips back false on its own when the loop backs off.
    autoAttEnabled: typeof r.autoAttEnabled === 'boolean' ? r.autoAttEnabled : true,
    attOffsetDb: typeof r.attOffsetDb === 'number' ? r.attOffsetDb : 0,
    adcOverloadWarning:
      typeof r.adcOverloadWarning === 'boolean' ? r.adcOverloadWarning : false,
    // StateDto.Nr is nullable on the server (older clients) — fall back to
    // the engine's declared defaults so the UI has something to render.
    nr: normalizeNr(r.nr),
    zoomLevel: normalizeZoomLevel(r.zoomLevel),
  };
}

function normalizeZoomLevel(v: unknown): ZoomLevel {
  if (typeof v === 'number' && Number.isInteger(v) && v >= ZOOM_MIN && v <= ZOOM_MAX) {
    return v;
  }
  return ZOOM_MIN;
}

function normalizeRadios(raw: unknown): RadioInfoDto[] {
  if (!Array.isArray(raw)) return [];
  return raw.map((entry) => {
    const r = (entry ?? {}) as Record<string, unknown>;
    const details = r.details;
    return {
      macAddress: typeof r.macAddress === 'string' ? r.macAddress : '',
      ipAddress: typeof r.ipAddress === 'string' ? r.ipAddress : '',
      boardId: typeof r.boardId === 'string' ? r.boardId : '',
      firmwareVersion:
        typeof r.firmwareVersion === 'string' ? r.firmwareVersion : '',
      busy: Boolean(r.busy),
      details:
        details && typeof details === 'object'
          ? (details as Record<string, string>)
          : null,
    };
  });
}

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

async function jsonFetch<T>(
  input: RequestInfo,
  init: RequestInit | undefined,
  parse: (raw: unknown) => T,
): Promise<T> {
  const res = await fetch(input, init);
  if (!res.ok) {
    // Server returns { error: "..." } on 400; fall back to status text otherwise.
    let message = `${res.status} ${res.statusText}`;
    try {
      const body = (await res.json()) as unknown;
      if (
        body &&
        typeof body === 'object' &&
        'error' in body &&
        typeof (body as { error: unknown }).error === 'string'
      ) {
        message = (body as { error: string }).error;
      }
    } catch {
      /* non-JSON body — keep status text */
    }
    throw new ApiError(res.status, message);
  }
  const raw = (await res.json()) as unknown;
  return parse(raw);
}

export function fetchState(signal?: AbortSignal): Promise<RadioStateDto> {
  return jsonFetch('/api/state', { signal }, normalizeState);
}

export function fetchRadios(signal?: AbortSignal): Promise<RadioInfoDto[]> {
  return jsonFetch('/api/radios', { signal }, normalizeRadios);
}

export function connect(
  req: ConnectRequest,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/connect',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(req),
      signal,
    },
    normalizeState,
  );
}

export function connectP2(
  req: ConnectRequest,
  signal?: AbortSignal,
): Promise<unknown> {
  return jsonFetch(
    '/api/connect/p2',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(req),
      signal,
    },
    (raw) => raw,
  );
}

export function disconnect(signal?: AbortSignal): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/disconnect',
    { method: 'POST', signal },
    normalizeState,
  );
}

export function disconnectP2(signal?: AbortSignal): Promise<unknown> {
  return jsonFetch(
    '/api/disconnect/p2',
    { method: 'POST', signal },
    (raw) => raw,
  );
}

export function setVfo(
  hz: number,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/vfo',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ hz }),
      signal,
    },
    normalizeState,
  );
}

export function setMode(
  mode: RxMode,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  // Server's System.Text.Json has no JsonStringEnumConverter — it expects
  // enum values as numeric ordinals on the write path. Normalizer handles
  // both forms on the read path, so the wire is asymmetric today.
  const modeIndex = MODE_ORDER.indexOf(mode);
  return jsonFetch(
    '/api/mode',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ mode: modeIndex }),
      signal,
    },
    normalizeState,
  );
}

export function setBandwidth(
  low: number,
  high: number,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/bandwidth',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ low, high }),
      signal,
    },
    normalizeState,
  );
}

export type SampleRate = 48_000 | 96_000 | 192_000 | 384_000;

export function setSampleRate(
  rate: SampleRate,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/sampleRate',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ rate }),
      signal,
    },
    normalizeState,
  );
}

export function setPreamp(
  on: boolean,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/preamp',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ on }),
      signal,
    },
    normalizeState,
  );
}

export function setAgcTop(
  topDb: number,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/agcGain',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ topDb }),
      signal,
    },
    normalizeState,
  );
}

export function setAttenuator(
  db: number,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/attenuator',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ db }),
      signal,
    },
    normalizeState,
  );
}

export function setAutoAtt(
  enabled: boolean,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/auto-att',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ enabled }),
      signal,
    },
    normalizeState,
  );
}

export function setZoom(
  level: ZoomLevel,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/rx/zoom',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ level }),
      signal,
    },
    normalizeState,
  );
}

export function setNr(
  nr: NrConfigDto,
  signal?: AbortSignal,
): Promise<RadioStateDto> {
  return jsonFetch(
    '/api/rx/nr',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      // Server registers JsonStringEnumConverter, so NrMode/NbMode travel as
      // PascalCase strings ("Off"/"Anr"/"Emnr", "Off"/"Nb1"/"Nb2"). Unknown
      // values get a 400, which ApiError surfaces to the caller.
      body: JSON.stringify({ nr }),
      signal,
    },
    normalizeState,
  );
}

// MOX endpoint returns {moxOn} — not a full StateDto — because MOX is
// transient and deliberately absent from the persisted state snapshot.
// 409 while disconnected surfaces as ApiError with the server's message.
export function setMox(
  on: boolean,
  signal?: AbortSignal,
): Promise<{ moxOn: boolean }> {
  return jsonFetch(
    '/api/tx/mox',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ on }),
      signal,
    },
    (raw) => ({ moxOn: Boolean((raw as { moxOn?: unknown }).moxOn) }),
  );
}

// Drive endpoint returns {drivePercent} — same pattern as MOX; drive is
// transient TX state that isn't part of the persisted radio snapshot.
export function setDrive(
  percent: number,
  signal?: AbortSignal,
): Promise<{ drivePercent: number }> {
  return jsonFetch(
    '/api/tx/drive',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ percent: Math.round(percent) }),
      signal,
    },
    (raw) => {
      const v = (raw as { drivePercent?: unknown }).drivePercent;
      return { drivePercent: typeof v === 'number' ? v : 0 };
    },
  );
}

// Tune-drive endpoint: POST /api/tx/tune-drive { percent }. Returns
// { tunePercent }. Backend picks this in place of drivePercent while TUN is
// keyed; same PA-gain calibration applies.
export function setTuneDrive(
  percent: number,
  signal?: AbortSignal,
): Promise<{ tunePercent: number }> {
  return jsonFetch(
    '/api/tx/tune-drive',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ percent: Math.round(percent) }),
      signal,
    },
    (raw) => {
      const v = (raw as { tunePercent?: unknown }).tunePercent;
      return { tunePercent: typeof v === 'number' ? v : 0 };
    },
  );
}

// TUN endpoint: POST /api/tx/tun { on }. Returns { tunOn }. Keys a single-tone
// carrier via WDSP SetTXAPostGen* and is mutually exclusive with MOX on the
// server. Same 404-tolerant pattern as setMicGain because the backend handler
// lands after this UI.
export async function setTun(
  on: boolean,
  signal?: AbortSignal,
): Promise<{ tunOn: boolean }> {
  try {
    return await jsonFetch(
      '/api/tx/tun',
      {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ on }),
        signal,
      },
      (raw) => ({ tunOn: Boolean((raw as { tunOn?: unknown }).tunOn) }),
    );
  } catch (err) {
    if (err instanceof ApiError && err.status === 404) {
      warnOnce('tx-tun-404', 'POST /api/tx/tun not implemented yet — treating as accepted');
      return { tunOn: on };
    }
    throw err;
  }
}

// Per-band memory: last-used (hz, mode) persisted server-side in LiteDB.
// Shared across any browser hitting the same backend — localStorage would
// trap the state in one device.
export type BandMemoryEntry = {
  band: string;
  hz: number;
  mode: RxMode;
};

function normalizeBandMemoryEntry(raw: unknown): BandMemoryEntry | null {
  if (!raw || typeof raw !== 'object') return null;
  const r = raw as Record<string, unknown>;
  const band = typeof r.band === 'string' ? r.band : null;
  const hz = typeof r.hz === 'number' ? r.hz : null;
  if (!band || hz === null) return null;
  return { band, hz, mode: normalizeMode(r.mode) };
}

export function fetchBandMemory(
  signal?: AbortSignal,
): Promise<BandMemoryEntry[]> {
  return jsonFetch('/api/bands/memory', { signal }, (raw) => {
    if (!Array.isArray(raw)) return [];
    const out: BandMemoryEntry[] = [];
    for (const entry of raw) {
      const n = normalizeBandMemoryEntry(entry);
      if (n) out.push(n);
    }
    return out;
  });
}

export function saveBandMemory(
  band: string,
  hz: number,
  mode: RxMode,
  signal?: AbortSignal,
): Promise<BandMemoryEntry> {
  // Mode travels as a numeric ordinal, matching the setMode convention the
  // server already validates against. The server's JsonStringEnumConverter
  // accepts both strings and ordinals on the read path.
  const modeIndex = MODE_ORDER.indexOf(mode);
  return jsonFetch(
    `/api/bands/memory/${encodeURIComponent(band)}`,
    {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ hz, mode: modeIndex }),
      signal,
    },
    (raw) => {
      const n = normalizeBandMemoryEntry(raw);
      return n ?? { band, hz, mode };
    },
  );
}

// Leveler max-gain endpoint: POST /api/tx/leveler-max-gain { gain }. Returns
// { levelerMaxGainDb }. Backend clamps to [0, 15] and echoes the applied
// value; stateless across backend restart, so ConnectPanel re-POSTs the
// persisted value when the connection comes up. Same 404-tolerant pattern as
// setMicGain for the frontend-ahead-of-backend window.
export async function setLevelerMaxGain(
  gain: number,
  signal?: AbortSignal,
): Promise<{ levelerMaxGainDb: number }> {
  try {
    return await jsonFetch(
      '/api/tx/leveler-max-gain',
      {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ gain }),
        signal,
      },
      (raw) => {
        const v = (raw as { levelerMaxGainDb?: unknown }).levelerMaxGainDb;
        return { levelerMaxGainDb: typeof v === 'number' ? v : gain };
      },
    );
  } catch (err) {
    if (err instanceof ApiError && err.status === 404) {
      warnOnce(
        'tx-leveler-max-gain-404',
        'POST /api/tx/leveler-max-gain not implemented yet — treating as accepted',
      );
      return { levelerMaxGainDb: gain };
    }
    throw err;
  }
}

// Mic-gain endpoint: POST /api/mic-gain { db }. Returns { micGainDb }.
// Backend may not have landed the handler yet — a 404 is downgraded to a
// silent warnOnce so the console doesn't fill with noise during the
// frontend-ahead-of-backend window. Non-404 failures bubble up so the
// slider can roll back the optimistic update.
export async function setMicGain(
  db: number,
  signal?: AbortSignal,
): Promise<{ micGainDb: number }> {
  try {
    return await jsonFetch(
      '/api/mic-gain',
      {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ db: Math.round(db) }),
        signal,
      },
      (raw) => {
        const v = (raw as { micGainDb?: unknown }).micGainDb;
        return { micGainDb: typeof v === 'number' ? v : 0 };
      },
    );
  } catch (err) {
    if (err instanceof ApiError && err.status === 404) {
      warnOnce('mic-gain-404', 'POST /api/mic-gain not implemented yet — treating as accepted');
      return { micGainDb: Math.round(db) };
    }
    throw err;
  }
}
