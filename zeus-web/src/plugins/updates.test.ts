// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

import { afterEach, describe, expect, it } from 'vitest';

import type {
  PluginDto,
  RegistryCatalog,
  RegistryPluginEntry,
  RegistryPluginVersion,
} from './api/plugins';
import {
  compareSemver,
  filterUnseen,
  findPluginUpdates,
  latestRegistryVersion,
  loadSeenPluginVersions,
  recordSeenPluginVersions,
  type PluginUpdate,
} from './updates';

const SEEN_KEY = 'zeus.plugin-updates.seen';
const originalGlobalLocalStorage = Object.getOwnPropertyDescriptor(
  globalThis,
  'localStorage',
);
const originalWindowLocalStorage =
  typeof window !== 'undefined'
    ? Object.getOwnPropertyDescriptor(window, 'localStorage')
    : undefined;

function restoreLocalStorage() {
  if (originalGlobalLocalStorage) {
    Object.defineProperty(globalThis, 'localStorage', originalGlobalLocalStorage);
  }
  if (typeof window !== 'undefined' && originalWindowLocalStorage) {
    Object.defineProperty(window, 'localStorage', originalWindowLocalStorage);
  }
}

function installLocalStorageShim(): Storage {
  const store = new Map<string, string>();
  const shim: Storage = {
    get length() {
      return store.size;
    },
    clear() {
      store.clear();
    },
    getItem(key) {
      return store.has(key) ? (store.get(key) as string) : null;
    },
    key(index) {
      return Array.from(store.keys())[index] ?? null;
    },
    removeItem(key) {
      store.delete(key);
    },
    setItem(key, value) {
      store.set(key, String(value));
    },
  };

  Object.defineProperty(globalThis, 'localStorage', {
    configurable: true,
    value: shim,
  });
  if (typeof window !== 'undefined') {
    Object.defineProperty(window, 'localStorage', {
      configurable: true,
      value: shim,
    });
  }

  return shim;
}

function removeLocalStorage() {
  Object.defineProperty(globalThis, 'localStorage', {
    configurable: true,
    value: undefined,
  });
  if (typeof window !== 'undefined') {
    Object.defineProperty(window, 'localStorage', {
      configurable: true,
      value: undefined,
    });
  }
}

function plugin(patch: Partial<PluginDto> = {}): PluginDto {
  return {
    id: 'demo',
    scanned: false,
    name: 'Demo Plugin',
    version: '1.0.0',
    author: '',
    description: '',
    license: 'GPL',
    capabilities: [],
    ...patch,
  };
}

function version(versionText: string): RegistryPluginVersion {
  return {
    version: versionText,
    sdkAbi: 1,
    sdkMinVersion: '0.6.0',
    platforms: ['any'],
    downloadUrl: `https://example.com/${versionText}.zip`,
    sha256: 'a'.repeat(64),
  };
}

function entry(patch: Partial<RegistryPluginEntry> = {}): RegistryPluginEntry {
  return {
    id: 'demo',
    name: 'Demo Registry Plugin',
    description: '',
    author: '',
    license: 'GPL',
    homepage: null,
    categories: [],
    verified: false,
    subscription: null,
    versions: [version('1.1.0')],
    ...patch,
  };
}

function catalog(entries: RegistryPluginEntry[] = [entry()]): RegistryCatalog {
  return {
    schemaVersion: 1,
    generated: '2026-07-05T00:00:00Z',
    plugins: entries,
  };
}

afterEach(() => {
  restoreLocalStorage();
  localStorage.clear();
});

describe('compareSemver', () => {
  it('orders equal, older, and newer versions', () => {
    expect(compareSemver('1.2.3', '1.2.3')).toBe(0);
    expect(compareSemver('1.2.4', '1.2.3')).toBeGreaterThan(0);
    expect(compareSemver('1.2.2', '1.2.3')).toBeLessThan(0);
  });

  it('treats missing segments as zero', () => {
    expect(compareSemver('1.2', '1.2.0')).toBe(0);
    expect(compareSemver('1.2.1', '1.2')).toBeGreaterThan(0);
    expect(compareSemver('1.2', '1.2.1')).toBeLessThan(0);
  });

  it('tolerates malformed segments without throwing', () => {
    expect(() => compareSemver('bad.version', '0.0.1')).not.toThrow();
    expect(compareSemver('1.x.3', '1.0.2')).toBeGreaterThan(0);
    expect(compareSemver('bad.version', '0.0.1')).toBeLessThan(0);
  });
});

describe('latestRegistryVersion', () => {
  it('returns the highest registry version even when unsorted', () => {
    expect(
      latestRegistryVersion(
        entry({ versions: [version('1.0.0'), version('1.3.0'), version('1.2.9')] }),
      )?.version,
    ).toBe('1.3.0');
  });

  it('returns null for entries with no versions', () => {
    expect(latestRegistryVersion(entry({ versions: [] }))).toBeNull();
  });
});

describe('findPluginUpdates', () => {
  it('returns installed registry plugins with newer versions available', () => {
    expect(findPluginUpdates([plugin()], catalog())).toEqual([
      {
        id: 'demo',
        name: 'Demo Plugin',
        installedVersion: '1.0.0',
        latestVersion: '1.1.0',
      },
    ]);
  });

  it('skips equal versions', () => {
    expect(
      findPluginUpdates([plugin({ version: '1.1.0' })], catalog()),
    ).toEqual([]);
  });

  it('skips installed versions newer than the registry', () => {
    expect(
      findPluginUpdates([plugin({ version: '1.2.0' })], catalog()),
    ).toEqual([]);
  });

  it('excludes operator-scanned plugins', () => {
    expect(
      findPluginUpdates([plugin({ scanned: true })], catalog()),
    ).toEqual([]);
  });

  it('skips installed plugins with no registry entry', () => {
    expect(
      findPluginUpdates([plugin({ id: 'missing' })], catalog()),
    ).toEqual([]);
  });

  it('skips registry entries with an empty versions array', () => {
    expect(
      findPluginUpdates([plugin()], catalog([entry({ versions: [] })])),
    ).toEqual([]);
  });

  it('skips empty installed or latest version strings', () => {
    expect(
      findPluginUpdates([plugin({ version: '' })], catalog()),
    ).toEqual([]);
    expect(
      findPluginUpdates([plugin()], catalog([entry({ versions: [version('')] })])),
    ).toEqual([]);
  });

  it('returns no updates when the catalog is null', () => {
    expect(findPluginUpdates([plugin()], null)).toEqual([]);
  });
});

describe('filterUnseen', () => {
  const updates: PluginUpdate[] = [
    {
      id: 'demo',
      name: 'Demo Plugin',
      installedVersion: '1.0.0',
      latestVersion: '1.2.0',
    },
  ];

  it('keeps unseen updates', () => {
    expect(filterUnseen(updates, {})).toEqual(updates);
  });

  it('suppresses updates already seen at the same version', () => {
    expect(filterUnseen(updates, { demo: '1.2.0' })).toEqual([]);
  });

  it('fires again when the seen version is older', () => {
    expect(filterUnseen(updates, { demo: '1.1.0' })).toEqual(updates);
  });
});

describe('seen plugin update persistence', () => {
  it('round-trips seen versions through localStorage', () => {
    const storage = installLocalStorageShim();
    recordSeenPluginVersions([
      {
        id: 'demo',
        name: 'Demo Plugin',
        installedVersion: '1.0.0',
        latestVersion: '1.2.0',
      },
      {
        id: 'meter',
        name: 'Meter Plugin',
        installedVersion: '0.2.0',
        latestVersion: '0.3.0',
      },
    ]);

    expect(JSON.parse(storage.getItem(SEEN_KEY) ?? '{}')).toEqual({
      demo: '1.2.0',
      meter: '0.3.0',
    });
    expect(loadSeenPluginVersions()).toEqual({
      demo: '1.2.0',
      meter: '0.3.0',
    });
  });

  it('merges newly recorded versions with existing seen versions', () => {
    const storage = installLocalStorageShim();
    storage.setItem(SEEN_KEY, JSON.stringify({ demo: '1.1.0' }));
    recordSeenPluginVersions([
      {
        id: 'meter',
        name: 'Meter Plugin',
        installedVersion: '0.2.0',
        latestVersion: '0.3.0',
      },
    ]);

    expect(loadSeenPluginVersions()).toEqual({
      demo: '1.1.0',
      meter: '0.3.0',
    });
  });

  it('handles absent localStorage without throwing', () => {
    removeLocalStorage();

    expect(loadSeenPluginVersions()).toEqual({});
    expect(() =>
      recordSeenPluginVersions([
        {
          id: 'demo',
          name: 'Demo Plugin',
          installedVersion: '1.0.0',
          latestVersion: '1.2.0',
        },
      ]),
    ).not.toThrow();
  });
});
