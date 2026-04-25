// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Filter preset selector modal — shows all available filter presets for the
// current mode with star icons to mark/unmark favorites (up to 3).

import { useCallback, useEffect, useState } from 'react';
import { useConnectionStore } from '../../state/connection-store';
import {
  setFilter,
  getFilterPresets,
  getFavoriteFilterSlots,
  setFavoriteFilterSlots,
  type FilterPresetDto,
} from '../../api/client';
import { formatFilterWidth } from './filterPresets';
import './FilterPresetSelector.css';

type FilterPresetSelectorProps = {
  isOpen: boolean;
  onClose: () => void;
};

export function FilterPresetSelector({ isOpen, onClose }: FilterPresetSelectorProps) {
  const mode = useConnectionStore((s) => s.mode);
  const applyState = useConnectionStore((s) => s.applyState);
  const [presets, setPresets] = useState<FilterPresetDto[]>([]);
  const [favorites, setFavorites] = useState<string[]>([]);
  const [loading, setLoading] = useState(false);

  // Load presets and favorites when modal opens or mode changes
  useEffect(() => {
    if (!isOpen) return;
    let cancelled = false;
    setLoading(true);

    Promise.all([
      getFilterPresets(mode),
      getFavoriteFilterSlots(mode),
    ])
      .then(([presetList, favList]) => {
        if (!cancelled) {
          setPresets(presetList);
          setFavorites(favList);
        }
      })
      .catch(() => {
        // Fallback to empty on error
        if (!cancelled) {
          setPresets([]);
          setFavorites(['F6', 'F5', 'F4']);
        }
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => { cancelled = true; };
  }, [isOpen, mode]);

  const selectPreset = useCallback((preset: FilterPresetDto) => {
    useConnectionStore.setState({
      filterLowHz: preset.lowHz,
      filterHighHz: preset.highHz,
      filterPresetName: preset.slotName,
    });
    setFilter(preset.lowHz, preset.highHz, preset.slotName)
      .then(applyState)
      .catch(() => {});
    onClose();
  }, [applyState, onClose]);

  const toggleFavorite = useCallback((slotName: string) => {
    const newFavorites = favorites.includes(slotName)
      ? favorites.filter((s) => s !== slotName)
      : favorites.length < 3
        ? [...favorites, slotName]
        : favorites; // Already at max

    if (newFavorites.length === favorites.length && !favorites.includes(slotName)) {
      // Already at max, can't add more
      return;
    }

    setFavorites(newFavorites);
    setFavoriteFilterSlots(mode, newFavorites)
      .then(applyState)
      .catch(() => {
        // Revert on error
        setFavorites(favorites);
      });
  }, [mode, favorites, applyState]);

  if (!isOpen) return null;

  return (
    <div className="filter-preset-selector__backdrop" onClick={onClose}>
      <div className="filter-preset-selector" onClick={(e) => e.stopPropagation()}>
        <div className="filter-preset-selector__header">
          <h2 className="filter-preset-selector__title">Filter Presets — {mode}</h2>
          <button
            type="button"
            className="filter-preset-selector__close"
            onClick={onClose}
            aria-label="Close"
          >
            ×
          </button>
        </div>

        {loading ? (
          <div className="filter-preset-selector__loading">Loading...</div>
        ) : (
          <>
            <div className="filter-preset-selector__hint">
              ★ Mark up to 3 favorites (shown in main filter panel)
            </div>
            <div className="filter-preset-selector__grid">
              {presets.map((preset) => {
                const isFavorite = favorites.includes(preset.slotName);
                const width = formatFilterWidth(preset.lowHz, preset.highHz);
                return (
                  <div key={preset.slotName} className="filter-preset-selector__item">
                    <button
                      type="button"
                      className="filter-preset-selector__star"
                      onClick={() => toggleFavorite(preset.slotName)}
                      title={isFavorite ? 'Remove from favorites' : 'Add to favorites'}
                      disabled={!isFavorite && favorites.length >= 3}
                    >
                      {isFavorite ? '★' : '☆'}
                    </button>
                    <button
                      type="button"
                      className="filter-preset-selector__preset"
                      onClick={() => selectPreset(preset)}
                      title={`${preset.slotName}: ${preset.lowHz >= 0 ? '+' : ''}${preset.lowHz} / ${preset.highHz >= 0 ? '+' : ''}${preset.highHz} Hz`}
                    >
                      <span className="filter-preset-selector__label">{preset.label}</span>
                      <span className="filter-preset-selector__width">{width}</span>
                    </button>
                  </div>
                );
              })}
            </div>
          </>
        )}
      </div>
    </div>
  );
}
