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
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { useState } from 'react';
import { PANELS, type PanelCategory } from './panels';

interface AddPanelModalProps {
  existingPanels: Set<string>;
  onAdd: (panelId: string) => void;
  onClose: () => void;
}

export function AddPanelModal({ existingPanels, onAdd, onClose }: AddPanelModalProps) {
  const [searchTerm, setSearchTerm] = useState('');
  const [selectedCategory, setSelectedCategory] = useState<PanelCategory | 'all'>('all');

  const categories: Array<PanelCategory | 'all'> = [
    'all',
    'spectrum',
    'vfo',
    'meters',
    'dsp',
    'log',
    'tools',
    'controls',
  ];

  const availablePanels = Object.values(PANELS).filter((panel) => {
    // Filter out panels that already exist in the layout
    if (existingPanels.has(panel.id)) return false;

    // Filter by category
    if (selectedCategory !== 'all' && panel.category !== selectedCategory) return false;

    // Filter by search term
    if (searchTerm) {
      const term = searchTerm.toLowerCase();
      return (
        panel.name.toLowerCase().includes(term) ||
        panel.tags.some((tag) => tag.toLowerCase().includes(term))
      );
    }

    return true;
  });

  return (
    <div
      className="modal-backdrop"
      style={{
        position: 'fixed',
        inset: 0,
        background: 'rgba(0, 0, 0, 0.7)',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        zIndex: 10000,
      }}
      onClick={onClose}
    >
      <div
        className="modal-content"
        style={{
          background: 'var(--bg-1)',
          border: '1px solid var(--line)',
          borderRadius: 'var(--r-md)',
          padding: 24,
          maxWidth: 600,
          width: '90%',
          maxHeight: '80vh',
          overflow: 'auto',
        }}
        onClick={(e) => e.stopPropagation()}
      >
        <div style={{ marginBottom: 16 }}>
          <h2 style={{ margin: 0, fontSize: 18, fontWeight: 600 }}>Add Panel</h2>
        </div>

        <div style={{ marginBottom: 16 }}>
          <input
            type="text"
            placeholder="Search panels..."
            value={searchTerm}
            onChange={(e) => setSearchTerm(e.target.value)}
            style={{
              width: '100%',
              padding: '8px 12px',
              background: 'var(--bg-0)',
              border: '1px solid var(--line)',
              borderRadius: 'var(--r-sm)',
              color: 'var(--fg-0)',
              fontSize: 14,
            }}
          />
        </div>

        <div style={{ display: 'flex', gap: 8, marginBottom: 16, flexWrap: 'wrap' }}>
          {categories.map((cat) => (
            <button
              key={cat}
              type="button"
              onClick={() => setSelectedCategory(cat)}
              className={`btn sm ${selectedCategory === cat ? 'active' : ''}`}
              style={{ textTransform: 'capitalize' }}
            >
              {cat}
            </button>
          ))}
        </div>

        <div style={{ display: 'grid', gap: 8, marginBottom: 16 }}>
          {availablePanels.length === 0 ? (
            <div style={{ textAlign: 'center', padding: 24, color: 'var(--fg-2)' }}>
              {existingPanels.size === Object.keys(PANELS).length
                ? 'All panels are already in the layout'
                : 'No panels found'}
            </div>
          ) : (
            availablePanels.map((panel) => (
              <button
                key={panel.id}
                type="button"
                onClick={() => {
                  onAdd(panel.id);
                  onClose();
                }}
                style={{
                  padding: '12px 16px',
                  background: 'var(--bg-0)',
                  border: '1px solid var(--line)',
                  borderRadius: 'var(--r-sm)',
                  color: 'var(--fg-0)',
                  textAlign: 'left',
                  cursor: 'pointer',
                  transition: 'all 0.15s',
                }}
                onMouseEnter={(e) => {
                  e.currentTarget.style.background = 'var(--bg-2)';
                  e.currentTarget.style.borderColor = 'var(--accent)';
                }}
                onMouseLeave={(e) => {
                  e.currentTarget.style.background = 'var(--bg-0)';
                  e.currentTarget.style.borderColor = 'var(--line)';
                }}
              >
                <div style={{ fontWeight: 600, marginBottom: 4 }}>{panel.name}</div>
                <div style={{ fontSize: 11, color: 'var(--fg-2)' }}>
                  {panel.tags.join(' · ')}
                </div>
              </button>
            ))
          )}
        </div>

        <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8 }}>
          <button type="button" onClick={onClose} className="btn">
            Cancel
          </button>
        </div>
      </div>
    </div>
  );
}
