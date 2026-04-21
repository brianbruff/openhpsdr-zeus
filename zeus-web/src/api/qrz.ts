import { ApiError } from './client';

export type QrzStation = {
  callsign: string;
  name: string | null;
  firstName: string | null;
  country: string | null;
  state: string | null;
  city: string | null;
  grid: string | null;
  lat: number | null;
  lon: number | null;
  dxcc: number | null;
  cqZone: number | null;
  ituZone: number | null;
  imageUrl: string | null;
};

export type QrzStatus = {
  connected: boolean;
  hasXmlSubscription: boolean;
  home: QrzStation | null;
  error: string | null;
  hasApiKey: boolean;
};

function toNum(v: unknown): number | null {
  return typeof v === 'number' && Number.isFinite(v) ? v : null;
}

function toStr(v: unknown): string | null {
  return typeof v === 'string' && v.length > 0 ? v : null;
}

function normalizeStation(raw: unknown): QrzStation {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    callsign: typeof r.callsign === 'string' ? r.callsign : '',
    name: toStr(r.name),
    firstName: toStr(r.firstName),
    country: toStr(r.country),
    state: toStr(r.state),
    city: toStr(r.city),
    grid: toStr(r.grid),
    lat: toNum(r.lat),
    lon: toNum(r.lon),
    dxcc: toNum(r.dxcc),
    cqZone: toNum(r.cqZone),
    ituZone: toNum(r.ituZone),
    imageUrl: toStr(r.imageUrl),
  };
}

function normalizeStatus(raw: unknown): QrzStatus {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    connected: Boolean(r.connected),
    hasXmlSubscription: Boolean(r.hasXmlSubscription),
    home: r.home ? normalizeStation(r.home) : null,
    error: toStr(r.error),
    hasApiKey: Boolean(r.hasApiKey),
  };
}

async function jsonFetch<T>(input: RequestInfo, init: RequestInit | undefined, parse: (raw: unknown) => T): Promise<T> {
  const res = await fetch(input, init);
  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`;
    try {
      const body = (await res.json()) as unknown;
      if (body && typeof body === 'object' && 'error' in body && typeof (body as { error: unknown }).error === 'string') {
        message = (body as { error: string }).error;
      }
    } catch {
      /* non-JSON */
    }
    throw new ApiError(res.status, message);
  }
  return parse((await res.json()) as unknown);
}

export function qrzStatus(signal?: AbortSignal): Promise<QrzStatus> {
  return jsonFetch('/api/qrz/status', { signal }, normalizeStatus);
}

export function qrzLogin(username: string, password: string, signal?: AbortSignal): Promise<QrzStatus> {
  return jsonFetch(
    '/api/qrz/login',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ username, password }),
      signal,
    },
    normalizeStatus,
  );
}

export function qrzLookup(callsign: string, signal?: AbortSignal): Promise<QrzStation> {
  return jsonFetch(
    '/api/qrz/lookup',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ callsign }),
      signal,
    },
    normalizeStation,
  );
}

export function qrzLogout(signal?: AbortSignal): Promise<QrzStatus> {
  return jsonFetch('/api/qrz/logout', { method: 'POST', signal }, normalizeStatus);
}

export function qrzSetApiKey(apiKey: string | null, signal?: AbortSignal): Promise<QrzStatus> {
  return jsonFetch(
    '/api/qrz/apikey',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ apiKey }),
      signal,
    },
    normalizeStatus,
  );
}
