// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Per-instance Meters-tile configuration. The blob lives in the FlexLayout
// `TabNode.config` field, round-trips with `Model.toJson()` via the existing
// /api/ui/layout PUT path, and survives full-browser restarts. No new
// storage layer.
//
// Mutating a panel's config goes through `Actions.updateNodeAttributes`,
// which fires `onModelChange` on the FlexWorkspace, which writes the layout
// JSON back to `useLayoutStore` and triggers the debounced server PUT.

import { useCallback, useMemo } from 'react';
import { Actions, type Model, type TabNode } from 'flexlayout-react';
import { MeterReadingId, METER_CATALOG } from './meterCatalog';

/** Operator-overridable rendering knobs. All fields optional — defaults come
 *  from `METER_CATALOG[reading]` at render time. */
export interface WidgetSettings {
  /** Axis-min override. */
  min?: number;
  /** Axis-max override. */
  max?: number;
  /** Whether to render the peak-hold tick. Defaults to true for level/dBFS
   *  readings and false for digital-style readings. */
  peakHold?: boolean;
  /** Operator-friendly label override. */
  label?: string;
}

export type MetersWidgetKind = 'hbar' | 'vbar' | 'dial' | 'sparkline' | 'digital';

export const METERS_WIDGET_KINDS: ReadonlyArray<MetersWidgetKind> = [
  'hbar',
  'vbar',
  'dial',
  'sparkline',
  'digital',
];

/** A single configured widget inside a Meters tile. */
export interface MetersWidgetInstance {
  /** Stable per-widget id. Survives re-orders so React keys stay aligned and
   *  Settings-drawer "selected widget" tracking doesn't lose its referent. */
  uid: string;
  /** What to read. */
  reading: MeterReadingId;
  /** How to render. */
  kind: MetersWidgetKind;
  /** Operator overrides on top of catalog defaults. */
  settings: WidgetSettings;
}

/** Top-level config blob for one Meters tile instance. */
export interface MetersPanelConfig {
  /** Bumped whenever the schema gains a non-additive field. v2+ migrations
   *  must check this before reading legacy fields; unknown versions reset
   *  to `EMPTY_METERS_CONFIG`. */
  schemaVersion: 1;
  widgets: MetersWidgetInstance[];
  /** Optional operator-named instance, shown in the panel header. Falls back
   *  to "Meters" when absent. */
  title?: string;
}

export const EMPTY_METERS_CONFIG: MetersPanelConfig = {
  schemaVersion: 1,
  widgets: [],
};

/** Best-effort parse + validation of the opaque `node.getConfig()` blob.
 *  Anything malformed falls through to the empty config — never throws,
 *  never crashes the panel. */
export function parseMetersPanelConfig(raw: unknown): MetersPanelConfig {
  if (!raw || typeof raw !== 'object') return EMPTY_METERS_CONFIG;
  const obj = raw as Partial<MetersPanelConfig>;
  if (obj.schemaVersion !== 1) return EMPTY_METERS_CONFIG;
  const widgets = Array.isArray(obj.widgets) ? obj.widgets : [];
  // Filter out entries whose `reading` is no longer in the catalog (e.g. a
  // future schema removed an ID). A Meters tile that lost a widget is far
  // less surprising than one that crashes the whole panel.
  const validWidgets: MetersWidgetInstance[] = [];
  for (const w of widgets) {
    if (!w || typeof w !== 'object') continue;
    const widget = w as Partial<MetersWidgetInstance>;
    if (typeof widget.uid !== 'string') continue;
    if (typeof widget.reading !== 'string') continue;
    if (!(widget.reading in METER_CATALOG)) continue;
    if (
      widget.kind !== 'hbar' &&
      widget.kind !== 'vbar' &&
      widget.kind !== 'dial' &&
      widget.kind !== 'sparkline' &&
      widget.kind !== 'digital'
    )
      continue;
    validWidgets.push({
      uid: widget.uid,
      reading: widget.reading as MeterReadingId,
      kind: widget.kind,
      settings:
        widget.settings && typeof widget.settings === 'object'
          ? { ...widget.settings }
          : {},
    });
  }
  return {
    schemaVersion: 1,
    widgets: validWidgets,
    title: typeof obj.title === 'string' ? obj.title : undefined,
  };
}

/**
 * React hook bound to a flexlayout TabNode that exposes the parsed
 * MetersPanelConfig + an updater. Updates are written through
 * `Actions.updateNodeAttributes`, which the FlexWorkspace's `onModelChange`
 * captures and persists.
 */
export function useMetersPanelConfig(node: TabNode): {
  config: MetersPanelConfig;
  setConfig: (next: MetersPanelConfig) => void;
} {
  const raw = node.getConfig();
  const config = useMemo(() => parseMetersPanelConfig(raw), [raw]);
  const setConfig = useCallback(
    (next: MetersPanelConfig) => {
      const model: Model | undefined = node.getModel();
      if (!model) return;
      model.doAction(
        Actions.updateNodeAttributes(node.getId(), { config: next }),
      );
    },
    [node],
  );
  return { config, setConfig };
}

/** Generate a stable, locally-unique widget UID. Uses crypto.randomUUID()
 *  when available; falls back to a Math.random suffix in old contexts. */
export function newWidgetUid(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }
  return `w-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

/** Default widget instance for a freshly-added catalog reading. */
export function defaultWidgetForReading(
  id: MeterReadingId,
): MetersWidgetInstance {
  const def = METER_CATALOG[id];
  return {
    uid: newWidgetUid(),
    reading: id,
    kind: def.defaultKind,
    settings: {},
  };
}
