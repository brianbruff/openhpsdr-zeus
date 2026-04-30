// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

import { describe, expect, it } from 'vitest';
import {
  DEFAULT_WIDGET_SPAN,
  defaultWidgetForReading,
  EMPTY_METERS_CONFIG,
  newWidgetUid,
  parseMetersPanelConfig,
  placeWidgetInGrid,
  type MetersPanelConfig,
  type MetersWidgetInstance,
} from '../metersConfig';
import { MeterReadingId } from '../meterCatalog';

function makeSampleConfig(): MetersPanelConfig {
  return {
    schemaVersion: 1,
    title: 'My Stack',
    widgets: [
      {
        uid: 'a',
        reading: MeterReadingId.RxSignalPk,
        kind: 'hbar',
        settings: { peakHold: true, label: 'Signal' },
      },
      {
        uid: 'b',
        reading: MeterReadingId.TxFwdWatts,
        kind: 'dial',
        settings: { min: 0, max: 5 },
      },
      {
        uid: 'c',
        reading: MeterReadingId.TxSwr,
        kind: 'digital',
        settings: {},
      },
    ],
  };
}

describe('metersConfig', () => {
  it('defaults to schemaVersion 1 with no widgets', () => {
    expect(EMPTY_METERS_CONFIG).toEqual({ schemaVersion: 1, widgets: [] });
  });

  it('round-trips a populated config through JSON 5+ times unchanged', () => {
    const original = makeSampleConfig();
    let blob: unknown = original;
    for (let i = 0; i < 6; i++) {
      blob = parseMetersPanelConfig(JSON.parse(JSON.stringify(blob)));
    }
    expect(blob).toEqual(original);
  });

  it('drops widgets that reference unknown readings', () => {
    const dirty = {
      schemaVersion: 1,
      widgets: [
        {
          uid: 'ok',
          reading: MeterReadingId.TxSwr,
          kind: 'digital',
          settings: {},
        },
        // bogus
        {
          uid: 'nope',
          reading: 'rx.does-not-exist',
          kind: 'hbar',
          settings: {},
        },
      ],
    };
    const parsed = parseMetersPanelConfig(dirty);
    expect(parsed.widgets.length).toBe(1);
    expect(parsed.widgets[0]?.uid).toBe('ok');
  });

  it('returns EMPTY_METERS_CONFIG for non-object / missing input', () => {
    expect(parseMetersPanelConfig(null)).toEqual(EMPTY_METERS_CONFIG);
    expect(parseMetersPanelConfig(undefined)).toEqual(EMPTY_METERS_CONFIG);
    expect(parseMetersPanelConfig(42)).toEqual(EMPTY_METERS_CONFIG);
    expect(parseMetersPanelConfig('hello')).toEqual(EMPTY_METERS_CONFIG);
  });

  it('returns EMPTY_METERS_CONFIG when schemaVersion mismatches', () => {
    const future = { schemaVersion: 99, widgets: [] };
    expect(parseMetersPanelConfig(future)).toEqual(EMPTY_METERS_CONFIG);
  });

  it('drops widgets with invalid kind', () => {
    const dirty = {
      schemaVersion: 1,
      widgets: [
        {
          uid: 'bad-kind',
          reading: MeterReadingId.TxSwr,
          kind: 'spinner',
          settings: {},
        },
      ],
    };
    const parsed = parseMetersPanelConfig(dirty);
    expect(parsed.widgets).toHaveLength(0);
  });

  it('newWidgetUid returns a non-empty string each call', () => {
    const a = newWidgetUid();
    const b = newWidgetUid();
    expect(a.length).toBeGreaterThan(0);
    expect(b.length).toBeGreaterThan(0);
    expect(a).not.toBe(b);
  });

  it('defaultWidgetForReading uses the catalog defaultKind', () => {
    const w = defaultWidgetForReading(MeterReadingId.TxFwdWatts);
    expect(w.kind).toBe('dial');
    expect(w.reading).toBe(MeterReadingId.TxFwdWatts);
    expect(w.settings).toEqual({});
  });

  it('preserves an operator title across round-trip', () => {
    const cfg = makeSampleConfig();
    const json = JSON.stringify(cfg);
    const back = parseMetersPanelConfig(JSON.parse(json));
    expect(back.title).toBe('My Stack');
  });

  it('round-trips a widget layout (x/y/w/h) through JSON', () => {
    const cfg: MetersPanelConfig = {
      schemaVersion: 1,
      widgets: [
        {
          uid: 'p1',
          reading: MeterReadingId.TxFwdWatts,
          kind: 'dial',
          settings: {},
          layout: { x: 4, y: 2, w: 3, h: 4 },
        },
      ],
    };
    const back = parseMetersPanelConfig(JSON.parse(JSON.stringify(cfg)));
    expect(back.widgets[0]?.layout).toEqual({ x: 4, y: 2, w: 3, h: 4 });
  });

  it('placeWidgetInGrid assigns next-row layout to a widget that lacks one', () => {
    const existing: MetersWidgetInstance[] = [
      {
        uid: 'a',
        reading: MeterReadingId.RxSignalPk,
        kind: 'hbar',
        settings: {},
        layout: { x: 0, y: 0, w: 6, h: 2 },
      },
      {
        uid: 'b',
        reading: MeterReadingId.TxSwr,
        kind: 'digital',
        settings: {},
        layout: { x: 6, y: 0, w: 3, h: 2 },
      },
    ];
    const fresh = defaultWidgetForReading(MeterReadingId.TxFwdWatts);
    expect(fresh.layout).toBeUndefined();
    const placed = placeWidgetInGrid(fresh, existing);
    expect(placed.layout?.y).toBe(2); // next free row below the existing pair
    expect(placed.layout?.x).toBe(0);
    expect(placed.layout?.w).toBe(DEFAULT_WIDGET_SPAN.dial.w);
    expect(placed.layout?.h).toBe(DEFAULT_WIDGET_SPAN.dial.h);
  });

  it('placeWidgetInGrid is a no-op for a widget that already has a layout', () => {
    const widget: MetersWidgetInstance = {
      uid: 'has-layout',
      reading: MeterReadingId.TxAlcGr,
      kind: 'hbar',
      settings: {},
      layout: { x: 1, y: 2, w: 3, h: 4 },
    };
    expect(placeWidgetInGrid(widget, [])).toBe(widget);
  });
});
