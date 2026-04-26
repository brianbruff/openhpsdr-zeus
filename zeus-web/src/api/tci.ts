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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

export type TciSettings = {
  enabled: boolean;
  port: number;
  bindAddress: string;
  portBound: boolean;
  portError: string | null;
  clientCount: number;
  restartRequired: boolean;
};

export type TciSettingsSetRequest = {
  enabled?: boolean;
  port?: number;
  bindAddress?: string;
};

function normalizeSettings(raw: unknown): TciSettings {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    enabled: Boolean(r.enabled),
    port: typeof r.port === 'number' ? r.port : 40001,
    bindAddress: typeof r.bindAddress === 'string' ? r.bindAddress : '127.0.0.1',
    portBound: Boolean(r.portBound),
    portError: typeof r.portError === 'string' && r.portError.length > 0 ? r.portError : null,
    clientCount: typeof r.clientCount === 'number' ? r.clientCount : 0,
    restartRequired: Boolean(r.restartRequired),
  };
}

export async function getTciSettings(): Promise<TciSettings> {
  const res = await fetch('/api/tci/settings');
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
  return normalizeSettings(await res.json());
}

export async function setTciSettings(req: TciSettingsSetRequest): Promise<TciSettings> {
  const res = await fetch('/api/tci/settings', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(req),
  });
  if (!res.ok) {
    let msg = `${res.status} ${res.statusText}`;
    try {
      const body = (await res.json()) as unknown;
      if (body && typeof body === 'object' && 'error' in body && typeof (body as { error: unknown }).error === 'string')
        msg = (body as { error: string }).error;
    } catch { /* ignore */ }
    throw new Error(msg);
  }
  // PUT returns the persisted entry (no runtime status), re-fetch for full status.
  return getTciSettings();
}
