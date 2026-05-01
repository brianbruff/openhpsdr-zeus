// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Configurable Meters Panel (plan §3.6). One tile = one independent widget
// set, persisted in the FlexLayout TabNode.config blob (round-trips with
// the workspace JSON; no new storage layer).
//
// Layout:
//   ┌─────────────────────────────────────────────────────┐
//   │ ⚙ ◀  Title (dbl-click to rename)               ▶   │  ← 24 px header
//   ├─────────────────────────────────────────────────────┤
//   │ ┌──────────┐                       ┌──────────┐    │
//   │ │ LIBRARY  │       widget canvas   │ SETTINGS │    │
//   │ │ (overlay)│                       │ (overlay)│    │
//   │ └──────────┘                       └──────────┘    │
//   └─────────────────────────────────────────────────────┘
//
// The Library and Settings drawers are absolutely positioned over the
// widget canvas — they do not push content. CSS transitions on
// translateX use the existing --dur-fast / --dur-med tokens.

import { useCallback, useMemo, useState, type CSSProperties } from 'react';
import { Settings, ChevronLeft, ChevronRight, X, Trash2 } from 'lucide-react';
import {
  ResponsiveGridLayout,
  useContainerWidth,
  type Layout,
  type LayoutItem,
} from 'react-grid-layout';
import {
  METER_CATALOG,
  METER_FILTERS,
  METER_READINGS,
  meterMatchesFilter,
  type MeterFilter,
  type MeterReadingId,
} from '../../components/meters/meterCatalog';
import {
  DEFAULT_WIDGET_SPAN,
  defaultWidgetForReading,
  EMPTY_METERS_CONFIG,
  METERS_GRID_COLS,
  METERS_GRID_ROW_HEIGHT_PX,
  METERS_WIDGET_KINDS,
  parseMetersPanelConfig,
  placeWidgetInGrid,
  type MetersWidgetInstance,
  type MetersWidgetKind,
  type MetersPanelConfig,
} from '../../components/meters/metersConfig';
import { MeterWidget } from '../../components/meters/MeterWidget';

interface MetersPanelProps {
  /** Per-instance config blob. Provided by `PanelTile` from the workspace
   *  store; defaults to EMPTY_METERS_CONFIG so the panel still renders if
   *  it's mounted standalone (tests, design previews). */
  config?: MetersPanelConfig;
  /** Persistence hook. The workspace store wires this to
   *  `updateTileInstanceConfig(uid, next)`. No-op default keeps the panel
   *  usable in non-persistent contexts. */
  setConfig?: (next: MetersPanelConfig) => void;
  /** Optional title-rename callback. Reserved for future workspace-level
   *  rename UX (the v1 workspace doesn't use this — see all-panels plan §10
   *  Q3). MetersPanel still owns the in-header title editor. */
  renameTab?: (name: string) => void;
}

export function MetersPanel({ config, setConfig, renameTab }: MetersPanelProps) {
  const effectiveConfig = config ?? EMPTY_METERS_CONFIG;
  const effectiveSet = setConfig ?? noop;
  return (
    <MetersPanelInner
      config={effectiveConfig}
      setConfig={effectiveSet}
      renameTab={renameTab}
    />
  );
}

function noop() {}

interface MetersPanelInnerProps {
  config: MetersPanelConfig;
  setConfig: (next: MetersPanelConfig) => void;
  renameTab?: (name: string) => void;
}

export function MetersPanelInner({
  config,
  setConfig,
  renameTab,
}: MetersPanelInnerProps) {
  const [libraryOpen, setLibraryOpen] = useState(false);
  const [selectedUid, setSelectedUid] = useState<string | null>(null);
  const [filter, setFilter] = useState<MeterFilter>('all');
  const [search, setSearch] = useState('');
  const [editingTitle, setEditingTitle] = useState(false);
  const [titleDraft, setTitleDraft] = useState(config.title ?? '');

  const settingsOpen = selectedUid !== null;
  const selectedWidget = useMemo(
    () => config.widgets.find((w) => w.uid === selectedUid) ?? null,
    [config.widgets, selectedUid],
  );

  const title = config.title ?? 'Meters';

  const updateWidget = useCallback(
    (uid: string, patch: Partial<MetersWidgetInstance>) => {
      const next: MetersPanelConfig = {
        ...config,
        widgets: config.widgets.map((w) =>
          w.uid === uid ? { ...w, ...patch, settings: { ...w.settings, ...(patch.settings ?? {}) } } : w,
        ),
      };
      setConfig(next);
    },
    [config, setConfig],
  );

  const removeWidget = useCallback(
    (uid: string) => {
      const next: MetersPanelConfig = {
        ...config,
        widgets: config.widgets.filter((w) => w.uid !== uid),
      };
      setConfig(next);
      setSelectedUid(null);
    },
    [config, setConfig],
  );

  const addWidget = useCallback(
    (id: MeterReadingId) => {
      const fresh = defaultWidgetForReading(id);
      // Auto-place the new widget at the next free row using the kind's
      // default span; the grid will compact upward at render time.
      const placed = placeWidgetInGrid(fresh, config.widgets);
      const next: MetersPanelConfig = {
        ...config,
        widgets: [...config.widgets, placed],
      };
      setConfig(next);
      // Auto-select the new widget so the operator sees its config knobs
      // ready in the Settings drawer if they want to tweak it.
      setSelectedUid(placed.uid);
    },
    [config, setConfig],
  );

  // Apply auto-placement at render time for any legacy widget that came in
  // without `layout`. This both feeds RGL the coordinates it requires and
  // gets persisted on the next layout change. Memoised against widget identity.
  const placedWidgets = useMemo(() => {
    const out: MetersWidgetInstance[] = [];
    for (const w of config.widgets) {
      if (w.layout) {
        out.push(w);
      } else {
        out.push(placeWidgetInGrid(w, out));
      }
    }
    return out;
  }, [config.widgets]);

  const onLayoutChange = useCallback(
    (next: Layout) => {
      // Map RGL's layout array back into the widget list. We only persist
      // when at least one coordinate actually changed to avoid loops.
      const byUid = new Map<string, LayoutItem>(next.map((l) => [l.i, l]));
      let changed = false;
      const widgets = placedWidgets.map((w) => {
        const l = byUid.get(w.uid);
        if (!l) return w;
        const cur = w.layout;
        if (
          cur &&
          cur.x === l.x &&
          cur.y === l.y &&
          cur.w === l.w &&
          cur.h === l.h
        ) {
          return w;
        }
        changed = true;
        return { ...w, layout: { x: l.x, y: l.y, w: l.w, h: l.h } };
      });
      if (changed) {
        setConfig({ ...config, widgets });
      }
    },
    [config, placedWidgets, setConfig],
  );

  const commitTitle = useCallback(() => {
    setEditingTitle(false);
    const trimmed = titleDraft.trim();
    if (trimmed === '' || trimmed === title) {
      setTitleDraft(title);
      return;
    }
    setConfig({ ...config, title: trimmed });
    if (renameTab) renameTab(trimmed);
  }, [titleDraft, title, config, setConfig, renameTab]);

  const headerStyle: CSSProperties = {
    height: 24,
    display: 'flex',
    alignItems: 'center',
    gap: 6,
    padding: '0 8px',
    background: 'linear-gradient(180deg, var(--panel-top), var(--panel-bot))',
    borderBottom: '1px solid var(--panel-border)',
    flexShrink: 0,
  };
  const headerBtnStyle: CSSProperties = {
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: 20,
    height: 20,
    borderRadius: 'var(--r-xs)',
    color: 'var(--fg-1)',
    background: 'transparent',
    transition: 'background var(--dur-fast)',
  };
  const titleStyle: CSSProperties = {
    flex: 1,
    fontSize: 11,
    fontFamily: 'var(--font-sans)',
    textTransform: 'uppercase',
    letterSpacing: '0.08em',
    color: 'var(--fg-1)',
    cursor: 'text',
    userSelect: 'none',
  };

  return (
    <div
      data-testid="meters-panel"
      style={{
        position: 'relative',
        display: 'flex',
        flexDirection: 'column',
        height: '100%',
        overflow: 'hidden',
        background: 'var(--bg-0)',
      }}
    >
      {/* Header */}
      <div style={headerStyle}>
        <button
          type="button"
          aria-label={libraryOpen ? 'Close meter library' : 'Open meter library'}
          aria-pressed={libraryOpen}
          title={libraryOpen ? 'Close library' : 'Add meters'}
          onClick={() => setLibraryOpen((o) => !o)}
          style={headerBtnStyle}
          data-testid="meters-library-toggle"
        >
          <Settings size={14} />
        </button>
        {libraryOpen ? (
          <button
            type="button"
            aria-label="Collapse drawer"
            title="Collapse drawer"
            onClick={() => setLibraryOpen(false)}
            style={headerBtnStyle}
          >
            <ChevronLeft size={14} />
          </button>
        ) : null}
        {editingTitle ? (
          <input
            type="text"
            autoFocus
            value={titleDraft}
            onChange={(e) => setTitleDraft(e.target.value)}
            onBlur={commitTitle}
            onKeyDown={(e) => {
              if (e.key === 'Enter') commitTitle();
              else if (e.key === 'Escape') {
                setEditingTitle(false);
                setTitleDraft(title);
              }
            }}
            style={{
              ...titleStyle,
              background: 'var(--bg-2)',
              border: '1px solid var(--accent)',
              borderRadius: 'var(--r-xs)',
              padding: '0 4px',
              outline: 'none',
            }}
          />
        ) : (
          <span
            style={titleStyle}
            onDoubleClick={() => {
              setTitleDraft(title);
              setEditingTitle(true);
            }}
            title="Double-click to rename"
          >
            {title}
          </span>
        )}
        {settingsOpen ? (
          <button
            type="button"
            aria-label="Close settings"
            title="Close settings"
            onClick={() => setSelectedUid(null)}
            style={headerBtnStyle}
          >
            <ChevronRight size={14} />
          </button>
        ) : null}
      </div>

      {/* Widget canvas — measured by useContainerWidth so the grid sizes to
          its parent. Empty-state and the grid itself both render inside the
          same scroll container so layout is consistent. */}
      <MetersCanvas
        widgets={placedWidgets}
        selectedUid={selectedUid}
        onSelectWidget={(uid) =>
          setSelectedUid((current) => (current === uid ? null : uid))
        }
        onRemoveWidget={removeWidget}
        onLayoutChange={onLayoutChange}
      />

      {/* Library drawer (left) */}
      <LibraryDrawer
        open={libraryOpen}
        filter={filter}
        setFilter={setFilter}
        search={search}
        setSearch={setSearch}
        existing={config.widgets}
        onAdd={addWidget}
        onClose={() => setLibraryOpen(false)}
      />

      {/* Settings drawer (right) */}
      <SettingsDrawer
        open={settingsOpen}
        widget={selectedWidget}
        onChange={(patch) => {
          if (selectedWidget) updateWidget(selectedWidget.uid, patch);
        }}
        onRemove={() => {
          if (selectedWidget) removeWidget(selectedWidget.uid);
        }}
        onClose={() => setSelectedUid(null)}
      />
    </div>
  );
}

interface LibraryDrawerProps {
  open: boolean;
  filter: MeterFilter;
  setFilter: (f: MeterFilter) => void;
  search: string;
  setSearch: (s: string) => void;
  existing: MetersWidgetInstance[];
  onAdd: (id: MeterReadingId) => void;
  onClose: () => void;
}

interface MetersCanvasProps {
  widgets: MetersWidgetInstance[];
  selectedUid: string | null;
  onSelectWidget: (uid: string) => void;
  onRemoveWidget: (uid: string) => void;
  onLayoutChange: (next: Layout) => void;
}

function MetersCanvas({
  widgets,
  selectedUid,
  onSelectWidget,
  onRemoveWidget,
  onLayoutChange,
}: MetersCanvasProps) {
  // useContainerWidth uses ResizeObserver to track the parent's pixel width
  // and feed it into ResponsiveGridLayout. Replaces the legacy WidthProvider
  // HOC; renders nothing until measured (mounted=false) to avoid a 1280-px
  // first-paint flash that snaps to actual width on the second tick.
  const { width, containerRef, mounted } = useContainerWidth();
  return (
    <div
      ref={containerRef}
      style={{
        flex: 1,
        minHeight: 0,
        overflowY: 'auto',
        position: 'relative',
      }}
      data-testid="meters-canvas"
    >
      {widgets.length === 0 ? (
        <div
          style={{
            padding: 24,
            textAlign: 'center',
            color: 'var(--fg-2)',
            fontSize: 12,
            fontFamily: 'var(--font-sans)',
          }}
          data-testid="meters-empty-state"
        >
          No meters yet — tap ⚙ to configure.
        </div>
      ) : !mounted ? (
        // Reserve space silently while ResizeObserver measures.
        <div style={{ minHeight: 80 }} aria-hidden />
      ) : (
        <ResponsiveGridLayout
          className="meters-grid"
          width={width}
          // Same column count + row geometry across breakpoints — the operator
          // does pixel-grain placement and we don't want their layout
          // reflowing when the tile is just a bit narrower.
          breakpoints={{ lg: 0 }}
          cols={{ lg: METERS_GRID_COLS }}
          rowHeight={METERS_GRID_ROW_HEIGHT_PX}
          margin={[6, 6]}
          containerPadding={[6, 6]}
          // Drag only via the small grip in each widget's header — clicks on
          // the body, gear, or numeric readout don't initiate a drag.
          dragConfig={{ handle: '.meter-widget-drag-handle', bounded: false }}
          onLayoutChange={onLayoutChange}
          layouts={{
            lg: widgets.map((w) => ({
              i: w.uid,
              x: w.layout?.x ?? 0,
              y: w.layout?.y ?? 0,
              w: w.layout?.w ?? DEFAULT_WIDGET_SPAN[w.kind].w,
              h: w.layout?.h ?? DEFAULT_WIDGET_SPAN[w.kind].h,
              minW: 2,
              minH: 2,
            })),
          }}
        >
          {widgets.map((w) => (
            <div key={w.uid} data-grid-uid={w.uid}>
              <MeterWidget
                widget={w}
                selected={w.uid === selectedUid}
                onSelect={() => onSelectWidget(w.uid)}
                onRemove={() => onRemoveWidget(w.uid)}
              />
            </div>
          ))}
        </ResponsiveGridLayout>
      )}
    </div>
  );
}

function LibraryDrawer({
  open,
  filter,
  setFilter,
  search,
  setSearch,
  existing,
  onAdd,
  onClose,
}: LibraryDrawerProps) {
  const term = search.trim().toLowerCase();
  const items = METER_READINGS.filter((def) => {
    if (!meterMatchesFilter(def, filter)) return false;
    if (term) {
      return (
        def.label.toLowerCase().includes(term) ||
        def.short.toLowerCase().includes(term) ||
        def.id.toLowerCase().includes(term)
      );
    }
    return true;
  });
  const existingIds = new Set(existing.map((w) => w.reading));

  return (
    <div
      role="dialog"
      aria-label="Meter library"
      aria-hidden={!open}
      style={{
        position: 'absolute',
        top: 24,
        bottom: 0,
        left: 0,
        width: 240,
        background: 'var(--bg-1)',
        borderRight: '1px solid var(--panel-border)',
        boxShadow: 'var(--panel-shadow)',
        transform: open ? 'translateX(0)' : 'translateX(-100%)',
        transition: 'transform var(--dur-med) var(--ease-out)',
        overflowY: 'auto',
        zIndex: 5,
        display: 'flex',
        flexDirection: 'column',
      }}
      data-testid="meters-library-drawer"
    >
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'space-between',
          padding: '6px 8px',
          borderBottom: '1px solid var(--panel-border)',
        }}
      >
        <span
          style={{
            fontSize: 11,
            textTransform: 'uppercase',
            letterSpacing: '0.08em',
            color: 'var(--fg-1)',
          }}
        >
          Library
        </span>
        <button
          type="button"
          aria-label="Close library"
          onClick={onClose}
          style={{
            display: 'inline-flex',
            alignItems: 'center',
            justifyContent: 'center',
            width: 18,
            height: 18,
            color: 'var(--fg-2)',
          }}
        >
          <X size={12} />
        </button>
      </div>
      <div style={{ padding: 8, display: 'flex', flexDirection: 'column', gap: 8 }}>
        <input
          type="text"
          placeholder="Search meters…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          aria-label="Search meters"
          style={{
            padding: '4px 8px',
            background: 'var(--bg-0)',
            border: '1px solid var(--panel-border)',
            borderRadius: 'var(--r-xs)',
            color: 'var(--fg-0)',
            fontSize: 12,
          }}
        />
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
          {METER_FILTERS.map((f) => (
            <button
              key={f}
              type="button"
              aria-pressed={filter === f}
              onClick={() => setFilter(f)}
              style={{
                padding: '2px 8px',
                fontSize: 10,
                textTransform: 'uppercase',
                letterSpacing: '0.04em',
                borderRadius: 'var(--r-xs)',
                color:
                  filter === f ? 'var(--btn-active-text)' : 'var(--fg-1)',
                background:
                  filter === f
                    ? 'linear-gradient(180deg, var(--btn-active-top), var(--btn-active-bot))'
                    : 'linear-gradient(180deg, var(--btn-top), var(--btn-bot))',
                border: '1px solid var(--btn-edge)',
              }}
            >
              {f}
            </button>
          ))}
        </div>
      </div>
      <div
        style={{
          flex: 1,
          minHeight: 0,
          overflowY: 'auto',
          padding: '0 4px 8px',
        }}
        data-testid="meters-library-list"
      >
        {items.length === 0 ? (
          <div
            style={{
              padding: 16,
              fontSize: 11,
              color: 'var(--fg-2)',
              textAlign: 'center',
            }}
          >
            No meters match
          </div>
        ) : (
          items.map((def) => {
            const already = existingIds.has(def.id);
            return (
              <button
                key={def.id}
                type="button"
                onClick={() => onAdd(def.id)}
                title={`Add "${def.label}" widget`}
                data-meter-id={def.id}
                style={{
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'space-between',
                  width: '100%',
                  padding: '4px 8px',
                  margin: '2px 0',
                  background: 'transparent',
                  border: '1px solid transparent',
                  borderRadius: 'var(--r-xs)',
                  textAlign: 'left',
                  color: 'var(--fg-1)',
                  fontSize: 12,
                  fontFamily: 'var(--font-sans)',
                }}
                onMouseEnter={(e) => {
                  e.currentTarget.style.background = 'var(--bg-2)';
                  e.currentTarget.style.borderColor = 'var(--panel-border)';
                }}
                onMouseLeave={(e) => {
                  e.currentTarget.style.background = 'transparent';
                  e.currentTarget.style.borderColor = 'transparent';
                }}
              >
                <span>{def.label}</span>
                <span
                  aria-hidden="true"
                  style={{
                    fontSize: 9,
                    color: already ? 'var(--accent)' : 'var(--fg-3)',
                    textTransform: 'uppercase',
                  }}
                >
                  {already ? '+ another' : 'add'}
                </span>
              </button>
            );
          })
        )}
      </div>
    </div>
  );
}

interface SettingsDrawerProps {
  open: boolean;
  widget: MetersWidgetInstance | null;
  onChange: (patch: Partial<MetersWidgetInstance>) => void;
  onRemove: () => void;
  onClose: () => void;
}

function SettingsDrawer({
  open,
  widget,
  onChange,
  onRemove,
  onClose,
}: SettingsDrawerProps) {
  const def = widget ? METER_CATALOG[widget.reading] : null;
  return (
    <div
      role="dialog"
      aria-label="Widget settings"
      aria-hidden={!open}
      style={{
        position: 'absolute',
        top: 24,
        bottom: 0,
        right: 0,
        width: 240,
        background: 'var(--bg-1)',
        borderLeft: '1px solid var(--panel-border)',
        boxShadow: 'var(--panel-shadow)',
        transform: open ? 'translateX(0)' : 'translateX(100%)',
        transition: 'transform var(--dur-med) var(--ease-out)',
        overflowY: 'auto',
        zIndex: 5,
        display: 'flex',
        flexDirection: 'column',
      }}
      data-testid="meters-settings-drawer"
    >
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'space-between',
          padding: '6px 8px',
          borderBottom: '1px solid var(--panel-border)',
        }}
      >
        <span
          style={{
            fontSize: 11,
            textTransform: 'uppercase',
            letterSpacing: '0.08em',
            color: 'var(--fg-1)',
          }}
        >
          Settings
        </span>
        <button
          type="button"
          aria-label="Close settings"
          onClick={onClose}
          style={{
            display: 'inline-flex',
            alignItems: 'center',
            justifyContent: 'center',
            width: 18,
            height: 18,
            color: 'var(--fg-2)',
          }}
        >
          <X size={12} />
        </button>
      </div>
      {widget && def ? (
        <div
          style={{
            padding: 8,
            display: 'flex',
            flexDirection: 'column',
            gap: 10,
          }}
        >
          <div
            style={{
              fontSize: 11,
              color: 'var(--fg-2)',
              fontFamily: 'var(--font-sans)',
            }}
          >
            {def.label}
          </div>

          <SettingsField label="Kind">
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
              {METERS_WIDGET_KINDS.map((k) => (
                <button
                  key={k}
                  type="button"
                  aria-pressed={widget.kind === k}
                  onClick={() => onChange({ kind: k as MetersWidgetKind })}
                  style={{
                    padding: '2px 8px',
                    fontSize: 10,
                    textTransform: 'uppercase',
                    borderRadius: 'var(--r-xs)',
                    color:
                      widget.kind === k
                        ? 'var(--btn-active-text)'
                        : 'var(--fg-1)',
                    background:
                      widget.kind === k
                        ? 'linear-gradient(180deg, var(--btn-active-top), var(--btn-active-bot))'
                        : 'linear-gradient(180deg, var(--btn-top), var(--btn-bot))',
                    border: '1px solid var(--btn-edge)',
                  }}
                >
                  {k}
                </button>
              ))}
            </div>
          </SettingsField>

          <SettingsField label="Axis min">
            <NumberInput
              value={widget.settings.min ?? def.defaultMin}
              onChange={(v) => onChange({ settings: { min: v } })}
            />
          </SettingsField>
          <SettingsField label="Axis max">
            <NumberInput
              value={widget.settings.max ?? def.defaultMax}
              onChange={(v) => onChange({ settings: { max: v } })}
            />
          </SettingsField>

          <SettingsField label="Peak hold">
            <label
              style={{
                display: 'inline-flex',
                gap: 6,
                alignItems: 'center',
                fontSize: 11,
                color: 'var(--fg-1)',
              }}
            >
              <input
                type="checkbox"
                checked={widget.settings.peakHold !== false}
                onChange={(e) =>
                  onChange({ settings: { peakHold: e.target.checked } })
                }
              />
              Show peak tick
            </label>
          </SettingsField>

          <SettingsField label="Label">
            <input
              type="text"
              value={widget.settings.label ?? ''}
              placeholder={def.label}
              onChange={(e) =>
                onChange({
                  settings: {
                    label: e.target.value === '' ? undefined : e.target.value,
                  },
                })
              }
              style={{
                padding: '4px 8px',
                background: 'var(--bg-0)',
                border: '1px solid var(--panel-border)',
                borderRadius: 'var(--r-xs)',
                color: 'var(--fg-0)',
                fontSize: 12,
              }}
            />
          </SettingsField>

          <button
            type="button"
            onClick={onRemove}
            data-testid="meters-remove-widget"
            style={{
              marginTop: 8,
              padding: '6px 10px',
              background: 'transparent',
              border: '1px solid var(--tx)',
              borderRadius: 'var(--r-xs)',
              color: 'var(--tx)',
              fontSize: 11,
              fontFamily: 'var(--font-sans)',
              textTransform: 'uppercase',
              letterSpacing: '0.04em',
              display: 'inline-flex',
              alignItems: 'center',
              justifyContent: 'center',
              gap: 6,
            }}
          >
            <Trash2 size={12} />
            Remove widget
          </button>
        </div>
      ) : (
        <div style={{ padding: 16, fontSize: 11, color: 'var(--fg-2)' }}>
          Select a widget to configure it.
        </div>
      )}
    </div>
  );
}

function SettingsField({
  label,
  children,
}: {
  label: string;
  children: React.ReactNode;
}) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
      <span
        style={{
          fontSize: 9,
          textTransform: 'uppercase',
          letterSpacing: '0.08em',
          color: 'var(--fg-2)',
        }}
      >
        {label}
      </span>
      {children}
    </div>
  );
}

function NumberInput({
  value,
  onChange,
}: {
  value: number;
  onChange: (v: number) => void;
}) {
  return (
    <input
      type="number"
      value={isFinite(value) ? value : 0}
      onChange={(e) => {
        const n = Number(e.target.value);
        if (Number.isFinite(n)) onChange(n);
      }}
      style={{
        padding: '4px 8px',
        background: 'var(--bg-0)',
        border: '1px solid var(--panel-border)',
        borderRadius: 'var(--r-xs)',
        color: 'var(--fg-0)',
        fontSize: 12,
        width: '100%',
      }}
    />
  );
}

// Re-export so tests can use the parser without a TabNode
export { parseMetersPanelConfig };
