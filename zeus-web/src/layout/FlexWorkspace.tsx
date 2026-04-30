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

import { useCallback, useEffect, useRef, useState, type MouseEvent, type PointerEvent } from 'react';
import {
  Actions,
  BorderNode,
  DockLocation,
  Layout,
  Model,
  TabSetNode,
  type IJsonModel,
  type ITabSetRenderValues,
  type TabNode,
} from 'flexlayout-react';
import { Plus } from 'lucide-react';
import 'flexlayout-react/style/dark.css';
import '../styles/flex-layout.css';
import { useWorkspace } from './WorkspaceContext';
import { useLayoutStore } from '../state/layout-store';
import { PANELS, panelIdFromComponent } from './panels';
import { DEFAULT_LAYOUT } from './defaultLayout';
import { TerminatorLines } from '../components/design/TerminatorLines';
import { AddPanelModal } from './AddPanelModal';

// Stop pointer/mouse-down events inside panel content from bubbling up to
// flexlayout-react's parent tabset, which uses them to start its own
// drag-detection. When that detection fires on a normal control click it
// triggers Actions.selectTab and forces FlexLayout to re-render the active
// tab — symptoms include the panadapter canvas restarting, accordion
// buttons appearing dead because their local useState resets on remount,
// and inline forms losing focus mid-keystroke. The tab-strip header lives
// outside this wrapper, so tab dragging / reordering / docking are all
// unaffected.
const stopPropagationProps = {
  onPointerDown: (e: PointerEvent) => e.stopPropagation(),
  onMouseDown: (e: MouseEvent) => e.stopPropagation(),
} as const;

function factory(node: TabNode) {
  const component = node.getComponent();
  if (!component) return null;
  // Multi-instance panels have a `<id>-<uuid>` component string; collapse it
  // back to the registry key so we look up the same PanelDef regardless of
  // which Meters tile we're rendering.
  const id = panelIdFromComponent(component);
  const panel = PANELS[id];
  if (!panel) return null;
  const Component = panel.component;
  // Wrap with stopPropagationProps so panel-body clicks/drags don't bubble
  // into flexlayout's tab-drag detection (#204). Pass `node` so multi-
  // instance panels (Meters) can read/write their per-instance config blob
  // via TabNode.getConfig().
  return (
    <div className="flex-panel-content" style={{ width: '100%', height: '100%' }} {...stopPropagationProps}>
      <Component node={node} />
    </div>
  );
}

// FlexWorkspace: renders the movable/dockable panel layout when ?layout=flex
// is active and the viewport is desktop (>900px). The mobile layout is
// unchanged and does not use this component — see App.tsx feature flag.
export function FlexWorkspace() {
  const { terminatorActive } = useWorkspace();
  const { layout, isLoaded, setLayout, loadFromServer, syncToServerBeforeUnload } = useLayoutStore();
  const [model, setModel] = useState<Model | null>(null);
  const [targetTabSetId, setTargetTabSetId] = useState<string | null>(null);
  const loadedRef = useRef(false);

  useEffect(() => {
    if (!loadedRef.current) {
      loadedRef.current = true;
      void loadFromServer();
    }
  }, [loadFromServer]);

  // Build the FlexLayout Model **once** on initial server-state load.
  //
  // After that the Model is its own source of truth — it mutates internally
  // for every drag/dock/tab-switch/click and fires onModelChange so we can
  // persist the JSON to the store. We must NOT rebuild the Model from
  // store JSON on every layout change: doing so replaces the Model
  // reference, which makes flexlayout-react's <Layout> remount every panel
  // and reinitialise stateful widgets (the panadapter WebGL context, audio
  // worklet, etc.). The visible symptoms were "click anything in a flex
  // panel and the panadapter refreshes from scratch / button clicks do
  // nothing" — flexlayout fires onModelChange even on tab-focus updates,
  // which were round-tripping through the store and remounting the world.
  //
  // The "RESET FLEX LAYOUT" button in DisplayPanel uses window.location
  // .reload(), so it gets a fresh Model that way; we don't need to react
  // to layout-store changes here.
  useEffect(() => {
    if (!isLoaded) return;
    setModel((current) => {
      if (current) return current;
      const json = (layout ?? DEFAULT_LAYOUT) as unknown as IJsonModel;
      return Model.fromJson(json);
    });
  }, [isLoaded, layout]);

  // Sync layout to server on page unload (sendBeacon → XHR fallback).
  useEffect(() => {
    const handler = () => syncToServerBeforeUnload();
    window.addEventListener('beforeunload', handler);
    return () => window.removeEventListener('beforeunload', handler);
  }, [syncToServerBeforeUnload]);

  // Get list of existing panel IDs in the layout. Multi-instance components
  // are stored as `<id>-<uuid>` strings in the FlexLayout JSON; we collapse
  // them back to the registry key so the AddPanel modal sees a single id
  // (and decides whether to allow another based on PanelDef.multiInstance).
  const getExistingPanels = (): Set<string> => {
    if (!model) return new Set();
    const panelIds = new Set<string>();

    model.visitNodes((node) => {
      if (node.getType() === 'tab') {
        const tabNode = node as TabNode;
        const component = tabNode.getComponent();
        if (component) panelIds.add(panelIdFromComponent(component));
      }
    });

    return panelIds;
  };

  const addPanel = (panelId: string) => {
    if (!model || !targetTabSetId) return;

    const panel = PANELS[panelId];
    if (!panel) return;

    // Multi-instance panels mint a unique component string per instance so
    // FlexLayout treats them as distinct nodes (drag, close, save/load
    // independently). The factory uses panelIdFromComponent() to recover
    // the registry key. crypto.randomUUID adds ~36 chars to the layout JSON
    // — paid only on layout-mutation, not per frame.
    const componentId = panel.multiInstance
      ? `${panelId}-${typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function' ? crypto.randomUUID() : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`}`
      : panelId;

    model.doAction(
      Actions.addNode(
        {
          type: 'tab',
          name: panel.name,
          component: componentId,
        },
        targetTabSetId,
        DockLocation.CENTER,
        -1,
        true,
      ),
    );
  };

  // Per-tabset "+" button — pushed into each tabset's sticky-button slot so it
  // sits next to the maximise control. Mirrors Log4YM's pattern; uses the
  // lucide-react Plus icon so the visual matches.
  const onRenderTabSet = useCallback(
    (node: TabSetNode | BorderNode, renderValues: ITabSetRenderValues) => {
      if (!(node instanceof TabSetNode)) return;
      const id = node.getId();
      renderValues.stickyButtons.push(
        <button
          key="add-panel"
          type="button"
          title="Add panel to this tabset"
          aria-label="Add panel"
          className="flexlayout__tab_toolbar_button"
          onClick={() => setTargetTabSetId(id)}
        >
          <Plus size={14} />
        </button>,
      );
    },
    [],
  );

  if (!model) {
    // Brief loading state while server layout fetch resolves.
    return <div className="flex-workspace" />;
  }

  return (
    <div className={`flex-workspace ${terminatorActive ? 'terminator' : ''}`}>
      <Layout
        model={model}
        factory={factory}
        onRenderTabSet={onRenderTabSet}
        onModelChange={(updatedModel) => {
          setLayout(updatedModel.toJson() as unknown as Record<string, unknown>);
        }}
      />
      <TerminatorLines active={terminatorActive} />
      {targetTabSetId && (
        <AddPanelModal
          existingPanels={getExistingPanels()}
          onAdd={addPanel}
          onClose={() => setTargetTabSetId(null)}
        />
      )}
    </div>
  );
}
