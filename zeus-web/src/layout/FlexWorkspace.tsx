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

import { useCallback, useEffect, useMemo } from 'react';
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
  // Loading is driven by App.tsx via loadForRadio(boardKey) — no local
  // first-load effect here. The active layout's parsed WorkspaceLayout
  // arrives via `workspace` once that resolves.
  const workspace = useLayoutStore((s) => s.workspace);
  const isLoaded = useLayoutStore((s) => s.isLoaded);
  const syncToServerBeforeUnload = useLayoutStore((s) => s.syncToServerBeforeUnload);
  const addTile = useLayoutStore((s) => s.addTile);
  const removeTile = useLayoutStore((s) => s.removeTile);
  const updateTilePlacement = useLayoutStore((s) => s.updateTilePlacement);
  // Modal visibility lifted into the store so the trigger button can live
  // in the LeftLayoutBar — the workspace just renders the modal when the
  // store says open.
  const addPanelOpen = useLayoutStore((s) => s.addPanelOpen);
  const setAddPanelOpen = useLayoutStore((s) => s.setAddPanelOpen);

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
      <TerminatorLines active={terminatorActive} />
      {addPanelOpen && (
        <AddPanelModal
          existingPanels={existingPanels}
          onAdd={onAddPanel}
          onClose={() => setAddPanelOpen(false)}
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
          // Drag from anywhere in the tile header (the grip + title strip),
          // EXCEPT the close button. A tiny grip-only handle is too small to
          // grab — and panels that have their own pointer logic in the body
          // (panadapter canvas's pan/tune gesture, sliders) also need a
          // generous header target so the operator can reposition the tile
          // without getting their input stolen by the body. dragConfig.cancel
          // excludes the X so close clicks still register.
          dragConfig={{
            handle: '.workspace-tile-header',
            cancel: '.workspace-tile-close',
            bounded: false,
          }}
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
  // Headerless panels own their entire tile surface and draw their own
  // header (if any). They MUST include an element with class
  // `.workspace-tile-header` so RGL drag picks up, and a
  // `.workspace-tile-close` button bound to the injected onRemove.
  if (def.headerless) {
    return (
      <div className="workspace-tile workspace-tile--headerless">
        <PanelBody tile={tile} onRemove={onRemove} />
      </div>
    );
  }
  return (
    <div className="workspace-tile">
      <TileChrome title={def.name} onRemove={onRemove} />
      <div className="workspace-tile-body">
        <PanelBody tile={tile} />
      </div>
    </div>
  );
}

function PanelBody({
  tile,
  onRemove,
}: {
  tile: WorkspaceTile;
  onRemove?: () => void;
}) {
  // Per-tile config-bound rendering for multi-instance / configurable
  // panels. Single-instance panels just render their component as-is.
  if (tile.panelId === 'meters') {
    return <MetersTileBody tile={tile} onRemove={onRemove} />;
  }
  const def = PANELS[tile.panelId];
  if (!def) return null;
  const Component = def.component;
  return <Component />;
}

function MetersTileBody({
  tile,
  onRemove,
}: {
  tile: WorkspaceTile;
  onRemove?: () => void;
}) {
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
  return (
    <MetersPanel config={config} setConfig={setConfig} onRemove={onRemove} />
  );
}
