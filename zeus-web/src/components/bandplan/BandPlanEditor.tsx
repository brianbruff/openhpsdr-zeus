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

import { useCallback, useEffect, useState } from 'react';
import { useBandPlanStore } from '../../state/bandPlan';
import { fetchPlan, type BandAllocation, type BandSegment, type ModeRestriction } from '../../api/bands';

const ALLOCATIONS: BandAllocation[] = ['Amateur', 'SWL', 'Broadcast', 'Reserved', 'Unknown'];
const RESTRICTIONS: ModeRestriction[] = ['Any', 'CwOnly', 'PhoneOnly', 'DigitalOnly'];

const RESTRICTION_LABELS: Record<ModeRestriction, string> = {
  Any: 'Any',
  CwOnly: 'CW only',
  PhoneOnly: 'Phone only',
  DigitalOnly: 'Digital only',
};

type EditRow = BandSegment & { _dirty?: boolean };

// Formats Hz as MHz with 4 decimal places.
function fmtMhz(hz: number): string {
  return (hz / 1_000_000).toFixed(4);
}

// Parses a MHz string back to Hz. Returns NaN on failure.
function parseMhz(s: string): number {
  const v = parseFloat(s.trim());
  return isNaN(v) ? NaN : Math.round(v * 1_000_000);
}

type InlineEditProps = {
  segment: EditRow;
  onChange: (updated: EditRow) => void;
  onDelete: () => void;
  isNew?: boolean;
};

function SegmentRow({ segment, onChange, onDelete, isNew = false }: InlineEditProps) {
  const [lowStr, setLowStr] = useState(fmtMhz(segment.lowHz));
  const [highStr, setHighStr] = useState(fmtMhz(segment.highHz));

  const col: React.CSSProperties = {
    padding: '3px 5px',
    borderBottom: '1px solid var(--panel-border)',
    verticalAlign: 'middle',
  };

  const inputStyle: React.CSSProperties = {
    width: '100%',
    background: isNew ? 'rgba(255,160,40,0.06)' : 'rgba(255,255,255,0.04)',
    border: '1px solid var(--panel-border)',
    borderRadius: 3,
    color: 'var(--fg-1)',
    padding: '2px 4px',
    fontSize: 11,
  };

  const flushLow = () => {
    const hz = parseMhz(lowStr);
    if (!isNaN(hz)) onChange({ ...segment, lowHz: hz });
    else setLowStr(fmtMhz(segment.lowHz));
  };
  const flushHigh = () => {
    const hz = parseMhz(highStr);
    if (!isNaN(hz)) onChange({ ...segment, highHz: hz });
    else setHighStr(fmtMhz(segment.highHz));
  };

  // Keep local string in sync when parent resets the segment.
  useEffect(() => { setLowStr(fmtMhz(segment.lowHz)); }, [segment.lowHz]);
  useEffect(() => { setHighStr(fmtMhz(segment.highHz)); }, [segment.highHz]);

  return (
    <tr>
      <td style={{ ...col, color: 'var(--fg-3)', fontSize: 10, whiteSpace: 'nowrap' }}>
        {isNew ? <span style={{ color: 'var(--accent)', fontWeight: 700 }}>NEW</span> : segment.regionId}
      </td>
      <td style={col}>
        <input
          style={inputStyle}
          value={lowStr}
          onChange={(e) => setLowStr(e.target.value)}
          onBlur={flushLow}
          onKeyDown={(e) => e.key === 'Enter' && flushLow()}
          aria-label="Low MHz"
        />
      </td>
      <td style={col}>
        <input
          style={inputStyle}
          value={highStr}
          onChange={(e) => setHighStr(e.target.value)}
          onBlur={flushHigh}
          onKeyDown={(e) => e.key === 'Enter' && flushHigh()}
          aria-label="High MHz"
        />
      </td>
      <td style={col}>
        <input
          style={inputStyle}
          value={segment.label}
          onChange={(e) => onChange({ ...segment, label: e.target.value })}
          aria-label="Label"
        />
      </td>
      <td style={col}>
        <select
          style={{ ...inputStyle, cursor: 'pointer' }}
          value={segment.allocation}
          onChange={(e) => onChange({ ...segment, allocation: e.target.value as BandAllocation })}
        >
          {ALLOCATIONS.map((a) => <option key={a} value={a}>{a}</option>)}
        </select>
      </td>
      <td style={col}>
        <select
          style={{ ...inputStyle, cursor: 'pointer' }}
          value={segment.modeRestriction}
          onChange={(e) => onChange({ ...segment, modeRestriction: e.target.value as ModeRestriction })}
        >
          {RESTRICTIONS.map((r) => (
            <option key={r} value={r}>{RESTRICTION_LABELS[r]}</option>
          ))}
        </select>
      </td>
      <td style={{ ...col, textAlign: 'right' }}>
        <button
          type="button"
          title="Delete row"
          onClick={onDelete}
          style={{
            background: 'transparent',
            border: 'none',
            color: 'var(--fg-3)',
            cursor: 'pointer',
            fontSize: 14,
            padding: '0 4px',
          }}
          onMouseEnter={(e) => { e.currentTarget.style.color = '#ff6060'; }}
          onMouseLeave={(e) => { e.currentTarget.style.color = 'var(--fg-3)'; }}
        >
          ×
        </button>
      </td>
    </tr>
  );
}

export function BandPlanEditor() {
  const {
    regions,
    currentRegionId,
    inflight,
    error,
    changeRegion,
    saveOverride,
    resetOverride,
  } = useBandPlanStore();

  // The region being edited in this panel (may differ from the active radio region).
  const [editRegionId, setEditRegionId] = useState(currentRegionId);
  const [rows, setRows] = useState<EditRow[]>([]);
  const [dirty, setDirty] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [resetting, setResetting] = useState(false);

  // Load the resolved plan for the selected edit region.
  const loadRegion = useCallback(async (regionId: string) => {
    try {
      const plan = await fetchPlan(regionId);
      setRows(plan.segments.map((s) => ({ ...s })));
      setDirty(false);
      setSaveError(null);
    } catch (e) {
      setSaveError(e instanceof Error ? e.message : String(e));
    }
  }, []);

  // When the store's current region changes (e.g. on mount or after WS event),
  // re-sync the edit panel.
  useEffect(() => {
    if (!editRegionId && currentRegionId) setEditRegionId(currentRegionId);
  }, [currentRegionId, editRegionId]);

  useEffect(() => {
    if (editRegionId) void loadRegion(editRegionId);
  }, [editRegionId, loadRegion]);

  const handleRegionDropdown = (id: string) => {
    if (dirty) {
      if (!confirm('You have unsaved changes. Switch region and discard them?')) return;
    }
    setEditRegionId(id);
  };

  const handleSetActive = async () => {
    try {
      await changeRegion(editRegionId);
    } catch (e) {
      setSaveError(e instanceof Error ? e.message : String(e));
    }
  };

  const handleRowChange = (idx: number, updated: EditRow) => {
    setRows((prev) => prev.map((r, i) => (i === idx ? { ...updated, _dirty: true } : r)));
    setDirty(true);
  };

  const handleDeleteRow = (idx: number) => {
    setRows((prev) => prev.filter((_, i) => i !== idx));
    setDirty(true);
  };

  const handleAddRow = () => {
    const last = rows[rows.length - 1];
    const newLow = last ? last.highHz + 1 : 7_000_000;
    setRows((prev) => [
      ...prev,
      {
        regionId: editRegionId,
        lowHz: newLow,
        highHz: newLow + 99_999,
        label: 'New segment',
        allocation: 'Amateur',
        modeRestriction: 'Any',
        maxPowerW: null,
        notes: null,
        _dirty: true,
      },
    ]);
    setDirty(true);
  };

  const handleSave = async () => {
    setSaving(true);
    setSaveError(null);
    try {
      await saveOverride(editRegionId, rows);
      setDirty(false);
    } catch (e) {
      setSaveError(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  };

  const handleReset = async () => {
    if (!confirm(`Reset "${editRegionId}" to shipped defaults? Any operator edits will be lost.`)) return;
    setResetting(true);
    setSaveError(null);
    try {
      await resetOverride(editRegionId);
      await loadRegion(editRegionId);
    } catch (e) {
      setSaveError(e instanceof Error ? e.message : String(e));
    } finally {
      setResetting(false);
    }
  };

  const isCurrentActive = editRegionId === currentRegionId;
  const editRegion = regions.find((r) => r.id === editRegionId);

  const thStyle: React.CSSProperties = {
    padding: '4px 5px',
    fontSize: 10,
    fontWeight: 700,
    letterSpacing: '0.1em',
    textTransform: 'uppercase',
    color: 'var(--fg-3)',
    borderBottom: '1px solid var(--panel-border)',
    textAlign: 'left',
    whiteSpace: 'nowrap',
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 14, height: '100%' }}>
      {/* Region selector row */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
        <label style={{ fontSize: 11, color: 'var(--fg-2)', whiteSpace: 'nowrap' }}>Region:</label>
        <select
          value={editRegionId}
          onChange={(e) => handleRegionDropdown(e.target.value)}
          style={{
            background: 'var(--bg-2)',
            border: '1px solid var(--panel-border)',
            borderRadius: 4,
            color: 'var(--fg-1)',
            padding: '4px 8px',
            fontSize: 12,
            cursor: 'pointer',
          }}
        >
          {regions.map((r) => (
            <option key={r.id} value={r.id}>
              {r.displayName}
              {r.parentId ? ` (overrides ${r.parentId})` : ''}
            </option>
          ))}
        </select>

        {editRegion?.parentId && (
          <span style={{ fontSize: 10, color: 'var(--fg-3)' }}>
            Inherits from {editRegion.parentId} — rows from inherited regions are shown but not saved to this level.
          </span>
        )}

        <div style={{ marginLeft: 'auto', display: 'flex', gap: 6 }}>
          {!isCurrentActive && (
            <button
              type="button"
              className="btn sm"
              onClick={() => void handleSetActive()}
              disabled={inflight}
              title="Set this as the active region for the radio"
            >
              SET ACTIVE
            </button>
          )}
          {isCurrentActive && (
            <span style={{ fontSize: 10, color: 'var(--accent)', fontWeight: 700, padding: '0 6px' }}>
              ● ACTIVE
            </span>
          )}
        </div>
      </div>

      {/* Disclaimer */}
      <p style={{ fontSize: 10, color: 'var(--fg-3)', margin: 0, lineHeight: 1.5 }}>
        ⚠ These defaults are best-effort. You are responsible for operating within your licence.
        Changes are stored locally; "Reset" restores the shipped file.
      </p>

      {/* Error display */}
      {(saveError ?? error) && (
        <div style={{
          background: 'rgba(255,80,80,0.1)', border: '1px solid rgba(255,80,80,0.3)',
          borderRadius: 4, padding: '6px 10px', fontSize: 11, color: '#ff8080',
        }}>
          {saveError ?? error}
        </div>
      )}

      {/* Segment table */}
      <div style={{ flex: 1, overflow: 'auto', minHeight: 0 }}>
        <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 11 }}>
          <thead>
            <tr>
              <th style={thStyle}>Source</th>
              <th style={thStyle}>Low (MHz)</th>
              <th style={thStyle}>High (MHz)</th>
              <th style={{ ...thStyle, width: '30%' }}>Label</th>
              <th style={thStyle}>Allocation</th>
              <th style={thStyle}>Mode</th>
              <th style={{ ...thStyle, width: 28 }}></th>
            </tr>
          </thead>
          <tbody>
            {rows.map((seg, idx) => (
              <SegmentRow
                key={`${seg.regionId}-${seg.lowHz}-${idx}`}
                segment={seg}
                onChange={(updated) => handleRowChange(idx, updated)}
                onDelete={() => handleDeleteRow(idx)}
                isNew={seg._dirty && seg.label === 'New segment'}
              />
            ))}
          </tbody>
        </table>
      </div>

      {/* Actions row */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexShrink: 0 }}>
        <button
          type="button"
          className="btn sm"
          onClick={handleAddRow}
          style={{ marginRight: 'auto' }}
        >
          + ADD ROW
        </button>
        <button
          type="button"
          className="btn sm"
          onClick={() => void handleReset()}
          disabled={resetting || saving}
          title="Restore shipped defaults for this region"
        >
          {resetting ? 'RESETTING…' : 'RESET TO DEFAULTS'}
        </button>
        <button
          type="button"
          className="btn sm active"
          onClick={() => void handleSave()}
          disabled={saving || !dirty}
        >
          {saving ? 'SAVING…' : 'SAVE CHANGES'}
        </button>
      </div>
    </div>
  );
}
