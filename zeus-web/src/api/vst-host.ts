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
// VST host REST client. Wave 6a backend lives at /api/plughost/*. The host
// exposes 8 chain slots and a master enable; plugin GUIs are out-of-process
// native windows that the operator's window manager paints — Zeus only
// brokers show/hide.

export const VST_HOST_SLOT_COUNT = 8;

export type VstHostPluginInfo = {
  name: string;
  vendor: string;
  version: string;
  path: string;
};

export type VstHostSlotState = {
  index: number;
  plugin: VstHostPluginInfo | null;
  bypass: boolean;
  parameterCount: number;
};

export type VstHostState = {
  masterEnabled: boolean;
  isRunning: boolean;
  slots: VstHostSlotState[];
  customSearchPaths: string[];
};

// Bit flags from Zeus.PluginHost.Chain.ParameterFlags. Hidden / ReadOnly
// parameters aren't surfaced to operators — see VstHostSlotParameters.
export const PARAM_FLAG_READ_ONLY = 0x01;
export const PARAM_FLAG_AUTOMATABLE = 0x02;
export const PARAM_FLAG_HIDDEN = 0x04;
export const PARAM_FLAG_LIST = 0x08;

export type VstHostParameter = {
  id: number;
  name: string;
  units: string;
  defaultValue: number;
  currentValue: number;
  stepCount: number;
  flags: number;
};

export type VstHostSlotDetail = {
  index: number;
  plugin: VstHostPluginInfo | null;
  bypass: boolean;
  parameters: VstHostParameter[];
};

export type VstHostCatalogEntry = {
  filePath: string;
  bundlePath: string | null;
  displayName: string;
  format: string;
  platform: string;
  bitness: string;
};

export type VstHostEditorOutcome = {
  index: number;
  width: number;
  height: number;
};

export class VstHostApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = 'VstHostApiError';
  }
}

async function jsonFetch<T>(
  input: RequestInfo,
  init: RequestInit | undefined,
  parse: (raw: unknown) => T,
): Promise<T> {
  const res = await fetch(input, init);
  if (!res.ok) {
    // Server returns ProblemDetails ({detail,title}) on 409/500 and
    // {error} on 400. Surface whichever string we can find so the UI
    // can drop it inline next to the offending control.
    let message = `${res.status} ${res.statusText}`;
    try {
      const body = (await res.json()) as unknown;
      if (body && typeof body === 'object') {
        const o = body as { error?: unknown; detail?: unknown; title?: unknown };
        if (typeof o.error === 'string') message = o.error;
        else if (typeof o.detail === 'string') message = o.detail;
        else if (typeof o.title === 'string') message = o.title;
      }
    } catch {
      /* non-JSON body — keep status text */
    }
    throw new VstHostApiError(res.status, message);
  }
  // 204 has no JSON body. Treat the empty case as `{}` so callers that
  // don't care about the payload still get a usable parse result.
  if (res.status === 204) return parse({});
  const raw = (await res.json()) as unknown;
  return parse(raw);
}

function asNumber(v: unknown, fallback = 0): number {
  return typeof v === 'number' && Number.isFinite(v) ? v : fallback;
}
function asInt(v: unknown, fallback = 0): number {
  return typeof v === 'number' && Number.isInteger(v) ? v : fallback;
}
function asString(v: unknown, fallback = ''): string {
  return typeof v === 'string' ? v : fallback;
}
function asBool(v: unknown, fallback = false): boolean {
  return typeof v === 'boolean' ? v : fallback;
}

function parsePluginInfo(raw: unknown): VstHostPluginInfo | null {
  if (!raw || typeof raw !== 'object') return null;
  const o = raw as Record<string, unknown>;
  // Path is required for "Unload" round-trips but the bare slot snapshot
  // (state endpoint) omits it — fall back to empty string and let callers
  // re-fetch the slot detail when they need the path.
  return {
    name: asString(o.name),
    vendor: asString(o.vendor),
    version: asString(o.version),
    path: asString(o.path),
  };
}

function parseSlot(raw: unknown): VstHostSlotState {
  const o = (raw ?? {}) as Record<string, unknown>;
  return {
    index: asInt(o.index),
    plugin: parsePluginInfo(o.plugin),
    bypass: asBool(o.bypass),
    parameterCount: asInt(o.parameterCount),
  };
}

export function parseVstHostState(raw: unknown): VstHostState {
  const o = (raw ?? {}) as Record<string, unknown>;
  const slotsRaw = Array.isArray(o.slots) ? o.slots : [];
  const slots = slotsRaw.map(parseSlot);
  // Pad/normalise to VST_HOST_SLOT_COUNT so the UI can render a fixed grid
  // even if the server (somehow) returned fewer entries.
  while (slots.length < VST_HOST_SLOT_COUNT) {
    slots.push({
      index: slots.length,
      plugin: null,
      bypass: false,
      parameterCount: 0,
    });
  }
  return {
    masterEnabled: asBool(o.masterEnabled),
    isRunning: asBool(o.isRunning),
    slots: slots.slice(0, VST_HOST_SLOT_COUNT),
    customSearchPaths: Array.isArray(o.customSearchPaths)
      ? (o.customSearchPaths as unknown[]).filter(
          (s): s is string => typeof s === 'string',
        )
      : [],
  };
}

function parseParameter(raw: unknown): VstHostParameter {
  const o = (raw ?? {}) as Record<string, unknown>;
  return {
    id: asInt(o.id),
    name: asString(o.name),
    units: asString(o.units),
    defaultValue: asNumber(o.defaultValue),
    currentValue: asNumber(o.currentValue),
    stepCount: asInt(o.stepCount),
    flags: asInt(o.flags),
  };
}

export function parseSlotDetail(raw: unknown): VstHostSlotDetail {
  const o = (raw ?? {}) as Record<string, unknown>;
  const params = Array.isArray(o.parameters) ? o.parameters : [];
  return {
    index: asInt(o.index),
    plugin: parsePluginInfo(o.plugin),
    bypass: asBool(o.bypass),
    parameters: params.map(parseParameter),
  };
}

function parseCatalogEntry(raw: unknown): VstHostCatalogEntry {
  const o = (raw ?? {}) as Record<string, unknown>;
  return {
    filePath: asString(o.filePath),
    bundlePath: typeof o.bundlePath === 'string' ? o.bundlePath : null,
    displayName: asString(o.displayName),
    format: asString(o.format),
    platform: asString(o.platform),
    bitness: asString(o.bitness),
  };
}

export function parseCatalog(raw: unknown): VstHostCatalogEntry[] {
  const o = (raw ?? {}) as Record<string, unknown>;
  const list = Array.isArray(o.plugins) ? o.plugins : [];
  return list.map(parseCatalogEntry);
}

// ---------------------------------------------------------------- REST

export function fetchVstHostState(
  signal?: AbortSignal,
): Promise<VstHostState> {
  return jsonFetch('/api/plughost/state', { signal }, parseVstHostState);
}

export function fetchVstHostCatalog(
  rescan: boolean,
  signal?: AbortSignal,
): Promise<VstHostCatalogEntry[]> {
  const url = rescan
    ? '/api/plughost/catalog?rescan=true'
    : '/api/plughost/catalog';
  return jsonFetch(url, { signal }, parseCatalog);
}

export function fetchVstHostSearchPaths(
  signal?: AbortSignal,
): Promise<string[]> {
  return jsonFetch('/api/plughost/searchPaths', { signal }, (raw) => {
    const o = (raw ?? {}) as Record<string, unknown>;
    return Array.isArray(o.paths)
      ? (o.paths as unknown[]).filter((s): s is string => typeof s === 'string')
      : [];
  });
}

export function addVstHostSearchPath(
  path: string,
  signal?: AbortSignal,
): Promise<{ added: boolean; paths: string[] }> {
  return jsonFetch(
    '/api/plughost/searchPaths',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ path }),
      signal,
    },
    (raw) => {
      const o = (raw ?? {}) as Record<string, unknown>;
      return {
        added: asBool(o.added),
        paths: Array.isArray(o.paths)
          ? (o.paths as unknown[]).filter(
              (s): s is string => typeof s === 'string',
            )
          : [],
      };
    },
  );
}

export function removeVstHostSearchPath(
  path: string,
  signal?: AbortSignal,
): Promise<{ removed: boolean; paths: string[] }> {
  const url = `/api/plughost/searchPaths?path=${encodeURIComponent(path)}`;
  return jsonFetch(
    url,
    { method: 'DELETE', signal },
    (raw) => {
      const o = (raw ?? {}) as Record<string, unknown>;
      return {
        removed: asBool(o.removed),
        paths: Array.isArray(o.paths)
          ? (o.paths as unknown[]).filter(
              (s): s is string => typeof s === 'string',
            )
          : [],
      };
    },
  );
}

export function setVstHostMaster(
  enabled: boolean,
  signal?: AbortSignal,
): Promise<VstHostState> {
  return jsonFetch(
    '/api/plughost/master',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ enabled }),
      signal,
    },
    parseVstHostState,
  );
}

export function fetchVstHostSlot(
  index: number,
  signal?: AbortSignal,
): Promise<VstHostSlotDetail> {
  return jsonFetch(
    `/api/plughost/slots/${index}`,
    { signal },
    parseSlotDetail,
  );
}

export function loadVstHostSlot(
  index: number,
  path: string,
  signal?: AbortSignal,
): Promise<{ index: number; plugin: VstHostPluginInfo | null }> {
  return jsonFetch(
    `/api/plughost/slots/${index}/load`,
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ path }),
      signal,
    },
    (raw) => {
      const o = (raw ?? {}) as Record<string, unknown>;
      return {
        index: asInt(o.index),
        plugin: parsePluginInfo(o.plugin),
      };
    },
  );
}

export function unloadVstHostSlot(
  index: number,
  signal?: AbortSignal,
): Promise<{ index: number }> {
  return jsonFetch(
    `/api/plughost/slots/${index}/unload`,
    { method: 'POST', signal },
    (raw) => {
      const o = (raw ?? {}) as Record<string, unknown>;
      return { index: asInt(o.index) };
    },
  );
}

export function setVstHostSlotBypass(
  index: number,
  bypass: boolean,
  signal?: AbortSignal,
): Promise<{ index: number; bypass: boolean }> {
  return jsonFetch(
    `/api/plughost/slots/${index}/bypass`,
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ bypass }),
      signal,
    },
    (raw) => {
      const o = (raw ?? {}) as Record<string, unknown>;
      return {
        index: asInt(o.index),
        bypass: asBool(o.bypass),
      };
    },
  );
}

export function fetchVstHostSlotParameters(
  index: number,
  signal?: AbortSignal,
): Promise<VstHostParameter[]> {
  return jsonFetch(
    `/api/plughost/slots/${index}/parameters`,
    { signal },
    (raw) => {
      const o = (raw ?? {}) as Record<string, unknown>;
      const list = Array.isArray(o.parameters) ? o.parameters : [];
      return list.map(parseParameter);
    },
  );
}

export function setVstHostSlotParameter(
  index: number,
  paramId: number,
  value: number,
  signal?: AbortSignal,
): Promise<{ index: number; paramId: number; value: number }> {
  return jsonFetch(
    `/api/plughost/slots/${index}/parameters/${paramId}`,
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ value }),
      signal,
    },
    (raw) => {
      const o = (raw ?? {}) as Record<string, unknown>;
      return {
        index: asInt(o.index),
        paramId: asInt(o.paramId),
        value: asNumber(o.value),
      };
    },
  );
}

export function showVstHostSlotEditor(
  index: number,
  signal?: AbortSignal,
): Promise<VstHostEditorOutcome> {
  return jsonFetch(
    `/api/plughost/slots/${index}/editor/show`,
    { method: 'POST', signal },
    (raw) => {
      const o = (raw ?? {}) as Record<string, unknown>;
      return {
        index: asInt(o.index),
        width: asInt(o.width),
        height: asInt(o.height),
      };
    },
  );
}

export function hideVstHostSlotEditor(
  index: number,
  signal?: AbortSignal,
): Promise<{ index: number; closed: boolean }> {
  return jsonFetch(
    `/api/plughost/slots/${index}/editor/hide`,
    { method: 'POST', signal },
    (raw) => {
      const o = (raw ?? {}) as Record<string, unknown>;
      return {
        index: asInt(o.index),
        closed: asBool(o.closed),
      };
    },
  );
}
