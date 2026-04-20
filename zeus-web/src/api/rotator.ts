import { ApiError } from './client';

export type RotctldStatus = {
  enabled: boolean;
  connected: boolean;
  host: string;
  port: number;
  currentAz: number | null;
  targetAz: number | null;
  moving: boolean;
  error: string | null;
};

export type RotctldConfig = {
  enabled: boolean;
  host: string;
  port: number;
  pollingIntervalMs: number;
};

export type RotctldTestResult = { ok: boolean; error: string | null };

function toNum(v: unknown): number | null {
  return typeof v === 'number' && Number.isFinite(v) ? v : null;
}

function normalizeStatus(raw: unknown): RotctldStatus {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    enabled: Boolean(r.enabled),
    connected: Boolean(r.connected),
    host: typeof r.host === 'string' ? r.host : '127.0.0.1',
    port: typeof r.port === 'number' ? r.port : 4533,
    currentAz: toNum(r.currentAz),
    targetAz: toNum(r.targetAz),
    moving: Boolean(r.moving),
    error: typeof r.error === 'string' && r.error.length > 0 ? r.error : null,
  };
}

async function jsonFetch<T>(input: RequestInfo, init: RequestInit | undefined, parse: (raw: unknown) => T): Promise<T> {
  const res = await fetch(input, init);
  if (!res.ok && res.status !== 503) {
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
  // 503 carries a body with error set — let the caller inspect the normalized status.
  return parse((await res.json()) as unknown);
}

export function getRotatorStatus(signal?: AbortSignal): Promise<RotctldStatus> {
  return jsonFetch('/api/rotator/status', { signal }, normalizeStatus);
}

export function postRotatorConfig(cfg: RotctldConfig, signal?: AbortSignal): Promise<RotctldStatus> {
  return jsonFetch(
    '/api/rotator/config',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(cfg),
      signal,
    },
    normalizeStatus,
  );
}

export function setRotatorAz(azimuth: number, signal?: AbortSignal): Promise<RotctldStatus> {
  return jsonFetch(
    '/api/rotator/set',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ azimuth }),
      signal,
    },
    normalizeStatus,
  );
}

export function stopRotator(signal?: AbortSignal): Promise<RotctldStatus> {
  return jsonFetch('/api/rotator/stop', { method: 'POST', signal }, normalizeStatus);
}

export function testRotator(host: string, port: number, signal?: AbortSignal): Promise<RotctldTestResult> {
  return jsonFetch(
    '/api/rotator/test',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ host, port }),
      signal,
    },
    (raw) => {
      const r = (raw ?? {}) as Record<string, unknown>;
      return { ok: Boolean(r.ok), error: typeof r.error === 'string' && r.error ? r.error : null };
    },
  );
}
