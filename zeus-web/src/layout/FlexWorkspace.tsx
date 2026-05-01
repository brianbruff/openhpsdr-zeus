// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// FlexWorkspace — react-grid-layout (RGL) substrate for the desktop
// workspace. Replaces the flexlayout-react implementation that lived here
// before. The export name `FlexWorkspace` is preserved so App.tsx import
// paths don't churn; a follow-up rename can land separately.
//
// Layout semantics:
//   - 12-column grid, WORKSPACE_ROW_HEIGHT_PX rows (see workspace.ts).
//   - Tiles persist via the layout-store (debounced PUT to /api/ui/layout).
//   - Drag handle is the small grip in each tile's chrome header — clicks
//     inside the panel body do not initiate a drag (RGL's dragConfig.handle
//     is scoped to .workspace-tile-drag-handle).
//   - "+ Add Panel" is a single workspace-level button at the top-right,
//     opening the categorized AddPanelModal.

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Plus } from 'lucide-react';
import {
  ResponsiveGridLayout,
  useContainerWidth,
  type Layout,
} from 'react-grid-layout';
import { useWorkspace } from './WorkspaceContext';
import { useLayoutStore } from '../state/layout-store';
import { PANELS } from './panels';
import {
  WORKSPACE_GRID_COLS,
  WORKSPACE_ROW_HEIGHT_PX,
  WORKSPACE_TILE_MIN_H,
  WORKSPACE_TILE_MIN_W,
  type WorkspaceTile,
} from './workspace';
import { AddPanelModal } from './AddPanelModal';
import { TileChrome } from './TileChrome';
import { TerminatorLines } from '../components/design/TerminatorLines';
import { MetersPanel } from './panels/MetersPanel';
import {
  parseMetersPanelConfig,
  type MetersPanelConfig,
} from '../components/meters/metersConfig';

export function FlexWorkspace() {
  const { terminatorActive } = useWorkspace();
  const workspace = useLayoutStore((s) => s.workspace);
  const isLoaded = useLayoutStore((s) => s.isLoaded);
  const loadFromServer = useLayoutStore((s) => s.loadFromServer);
  const syncToServerBeforeUnload = useLayoutStore((s) => s.syncToServerBeforeUnload);
  const addTile = useLayoutStore((s) => s.addTile);
  const removeTile = useLayoutStore((s) => s.removeTile);
  const updateTilePlacement = useLayoutStore((s) => s.updateTilePlacement);

  const [addOpen, setAddOpen] = useState(false);
  const loadedRef = useRef(false);

  useEffect(() => {
    if (!loadedRef.current) {
      loadedRef.current = true;
      void loadFromServer();
    }
  }, [loadFromServer]);

  // Best-effort persist on page-unload (sendBeacon → fetch keepalive fallback).
  useEffect(() => {
    const handler = () => syncToServerBeforeUnload();
    window.addEventListener('beforeunload', handler);
    return () => window.removeEventListener('beforeunload', handler);
  }, [syncToServerBeforeUnload]);

  const existingPanels = useMemo(
    () => new Set(workspace.tiles.map((t) => t.panelId)),
    [workspace.tiles],
  );

  const onLayoutChange = useCallback(
    (next: Layout) => {
      // RGL fires onLayoutChange on every render with the current layout
      // (including the very first paint). Diff each item against the store
      // and only PUT through when something actually moved.
      for (const item of next) {
        updateTilePlacement(item.i, {
          x: item.x,
          y: item.y,
          w: item.w,
          h: item.h,
        });
      }
    },
    [updateTilePlacement],
  );

  const onAddPanel = useCallback(
    (panelId: string) => {
      addTile(panelId);
    },
    [addTile],
  );

  // Brief loading state while the server fetch resolves. We render the
  // empty container so it has measurable width when the tiles arrive.
  return (
    <div className={`flex-workspace ${terminatorActive ? 'terminator' : ''}`}>
      <WorkspaceCanvas
        tiles={workspace.tiles}
        isLoaded={isLoaded}
        onLayoutChange={onLayoutChange}
        onRemoveTile={removeTile}
      />
      <button
        type="button"
        className="workspace-add-panel-btn"
        onClick={() => setAddOpen(true)}
        title="Add panel"
        aria-label="Add panel"
      >
        <Plus size={12} />
        Add Panel
      </button>
      <TerminatorLines active={terminatorActive} />
      {addOpen && (
        <AddPanelModal
          existingPanels={existingPanels}
          onAdd={onAddPanel}
          onClose={() => setAddOpen(false)}
        />
      )}
    </div>
  );
}

interface WorkspaceCanvasProps {
  tiles: WorkspaceTile[];
  isLoaded: boolean;
  onLayoutChange: (next: Layout) => void;
  onRemoveTile: (uid: string) => void;
}

function WorkspaceCanvas({
  tiles,
  isLoaded,
  onLayoutChange,
  onRemoveTile,
}: WorkspaceCanvasProps) {
  // useContainerWidth from RGL's modern API: ResizeObserver-backed parent
  // measurement. mounted=false on first paint to avoid the 1280-px width
  // flash before the observer fires. Same pattern MetersCanvas uses.
  const { width, containerRef, mounted } = useContainerWidth();

  // RGL needs a stable per-render layouts.lg array. Memoise against the
  // tile list identity so we don't push a new prop on every parent render.
  const rglLayouts = useMemo(
    () => ({
      lg: tiles.map((t) => ({
        i: t.uid,
        x: t.x,
        y: t.y,
        w: t.w,
        h: t.h,
        minW: WORKSPACE_TILE_MIN_W,
        minH: WORKSPACE_TILE_MIN_H,
      })),
    }),
    [tiles],
  );

  return (
    <div ref={containerRef} className="all-panels-workspace">
      {!isLoaded || !mounted ? (
        // Reserve space silently while server load + ResizeObserver settle.
        <div style={{ minHeight: 80 }} aria-hidden />
      ) : (
        <ResponsiveGridLayout
          className="all-panels-grid"
          width={width}
          breakpoints={{ lg: 0 }}
          cols={{ lg: WORKSPACE_GRID_COLS }}
          rowHeight={WORKSPACE_ROW_HEIGHT_PX}
          margin={[6, 6]}
          containerPadding={[6, 6]}
          // Drag only via the grip in each tile's chrome — clicks on the
          // body or the X don't kidnap input.
          dragConfig={{ handle: '.workspace-tile-drag-handle', bounded: false }}
          onLayoutChange={onLayoutChange}
          layouts={rglLayouts}
        >
          {tiles.map((tile) => (
            <div key={tile.uid} data-tile-uid={tile.uid}>
              <PanelTile tile={tile} onRemove={() => onRemoveTile(tile.uid)} />
            </div>
          ))}
        </ResponsiveGridLayout>
      )}
    </div>
  );
}

interface PanelTileProps {
  tile: WorkspaceTile;
  onRemove: () => void;
}

function PanelTile({ tile, onRemove }: PanelTileProps) {
  const def = PANELS[tile.panelId];
  if (!def) return null;
  return (
    <div className="workspace-tile">
      <TileChrome title={def.name} onRemove={onRemove} />
      <div className="workspace-tile-body">
        <PanelBody tile={tile} />
      </div>
    </div>
  );
}

function PanelBody({ tile }: { tile: WorkspaceTile }) {
  // Per-tile config-bound rendering for multi-instance / configurable
  // panels. Single-instance panels just render their component as-is.
  if (tile.panelId === 'meters') {
    return <MetersTileBody tile={tile} />;
  }
  const def = PANELS[tile.panelId];
  if (!def) return null;
  const Component = def.component;
  return <Component />;
}

function MetersTileBody({ tile }: { tile: WorkspaceTile }) {
  const updateTileInstanceConfig = useLayoutStore(
    (s) => s.updateTileInstanceConfig,
  );
  const config: MetersPanelConfig = useMemo(
    () => parseMetersPanelConfig(tile.instanceConfig),
    [tile.instanceConfig],
  );
  const setConfig = useCallback(
    (next: MetersPanelConfig) => {
      updateTileInstanceConfig(tile.uid, next);
    },
    [tile.uid, updateTileInstanceConfig],
  );
  return <MetersPanel config={config} setConfig={setConfig} />;
}
