// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

import { useState, useCallback, useEffect } from 'react';
import { loadFavorites, saveFavorites, FILTER_MAX_FAVORITES } from './filterPresets';
import type { RxMode } from '../../api/client';

// Module-level listener set so FilterPanel and FilterRibbon stay in sync
// without needing a Zustand store for this lightweight UI preference.
const _listeners = new Set<() => void>();
function _notifyAll() { _listeners.forEach((fn) => fn()); }

export function useFavoriteFilters(mode: RxMode) {
  const [favorites, setLocal] = useState<string[]>(() => loadFavorites(mode));

  useEffect(() => { setLocal(loadFavorites(mode)); }, [mode]);

  useEffect(() => {
    const handler = () => setLocal(loadFavorites(mode));
    _listeners.add(handler);
    return () => { _listeners.delete(handler); };
  }, [mode]);

  const toggleFavorite = useCallback((slotName: string) => {
    const current = loadFavorites(mode);
    let next: string[];
    if (current.includes(slotName)) {
      next = current.filter((s) => s !== slotName);
    } else if (current.length < FILTER_MAX_FAVORITES) {
      next = [...current, slotName];
    } else {
      return;
    }
    saveFavorites(mode, next);
    setLocal(next);
    _notifyAll();
  }, [mode]);

  return { favorites, toggleFavorite, atMax: favorites.length >= FILTER_MAX_FAVORITES };
}
