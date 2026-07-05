// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

import type {
  PluginDto,
  RegistryCatalog,
  RegistryPluginEntry,
  RegistryPluginVersion,
} from './api/plugins';

const SEEN_PLUGIN_UPDATES_KEY = 'zeus.plugin-updates.seen';

export type PluginUpdate = {
  id: string;
  name: string;
  installedVersion: string;
  latestVersion: string;
};

export function compareSemver(a: string, b: string): number {
  const ap = a.split('.').map((x) => parseInt(x, 10) || 0);
  const bp = b.split('.').map((x) => parseInt(x, 10) || 0);
  const n = Math.max(ap.length, bp.length);
  for (let i = 0; i < n; i++) {
    const av = ap[i] ?? 0;
    const bv = bp[i] ?? 0;
    if (av !== bv) return av - bv;
  }
  return 0;
}

export function latestRegistryVersion(
  entry: RegistryPluginEntry,
): RegistryPluginVersion | null {
  if (entry.versions.length === 0) return null;
  // The registry is curator-controlled — schema guarantees `versions`
  // are sorted newest-first when published, but we don't rely on that.
  // Pick the highest SemVer triple to be safe.
  const sorted = [...entry.versions].sort((a, b) =>
    compareSemver(b.version, a.version),
  );
  return sorted[0] ?? null;
}

export function findPluginUpdates(
  installed: PluginDto[],
  catalog: RegistryCatalog | null,
): PluginUpdate[] {
  if (!catalog) return [];

  const registryById = new Map(
    catalog.plugins.map((entry) => [entry.id, entry]),
  );
  const updates: PluginUpdate[] = [];

  for (const plugin of installed) {
    if (plugin.scanned || plugin.version.trim() === '') continue;

    const entry = registryById.get(plugin.id);
    if (!entry) continue;

    const latest = latestRegistryVersion(entry);
    if (!latest || latest.version.trim() === '') continue;
    if (compareSemver(latest.version, plugin.version) <= 0) continue;

    updates.push({
      id: plugin.id,
      name: plugin.name || entry.name || plugin.id,
      installedVersion: plugin.version,
      latestVersion: latest.version,
    });
  }

  return updates;
}

export function filterUnseen(
  updates: PluginUpdate[],
  seen: Record<string, string>,
): PluginUpdate[] {
  return updates.filter((update) => {
    const seenVersion = seen[update.id];
    if (!seenVersion) return true;
    return compareSemver(update.latestVersion, seenVersion) > 0;
  });
}

function stringRecord(value: unknown): Record<string, string> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return {};
  return Object.fromEntries(
    Object.entries(value).filter((entry): entry is [string, string] =>
      typeof entry[1] === 'string',
    ),
  );
}

export function loadSeenPluginVersions(): Record<string, string> {
  try {
    if (typeof localStorage === 'undefined') return {};
    const raw = localStorage.getItem(SEEN_PLUGIN_UPDATES_KEY);
    if (!raw) return {};
    return stringRecord(JSON.parse(raw));
  } catch {
    return {};
  }
}

export function recordSeenPluginVersions(updates: PluginUpdate[]): void {
  if (updates.length === 0) return;

  try {
    if (typeof localStorage === 'undefined') return;
    const seen = loadSeenPluginVersions();
    for (const update of updates) {
      seen[update.id] = update.latestVersion;
    }
    localStorage.setItem(SEEN_PLUGIN_UPDATES_KEY, JSON.stringify(seen));
  } catch {
    // localStorage may be disabled or quota-limited in browser/webview hosts.
  }
}
