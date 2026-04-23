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
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { useEffect, useRef, useState } from 'react';
import { Layout, Model, type IJsonModel, type TabNode } from 'flexlayout-react';
import 'flexlayout-react/style/dark.css';
import '../styles/flex-layout.css';
import { useWorkspace } from './WorkspaceContext';
import { useLayoutStore } from '../state/layout-store';
import { PANELS } from './panels';
import { DEFAULT_LAYOUT } from './defaultLayout';
import { TerminatorLines } from '../components/design/TerminatorLines';

function factory(node: TabNode) {
  const id = node.getComponent();
  const panel = id ? PANELS[id] : undefined;
  if (!panel) return null;
  const Component = panel.component;
  return <Component />;
}

// FlexWorkspace: renders the movable/dockable panel layout when ?layout=flex
// is active and the viewport is desktop (>900px). The mobile layout is
// unchanged and does not use this component — see App.tsx feature flag.
export function FlexWorkspace() {
  const { terminatorActive } = useWorkspace();
  const { layout, isLoaded, setLayout, loadFromServer, syncToServerBeforeUnload } = useLayoutStore();
  const [model, setModel] = useState<Model | null>(null);
  const loadedRef = useRef(false);

  useEffect(() => {
    if (!loadedRef.current) {
      loadedRef.current = true;
      void loadFromServer();
    }
  }, [loadFromServer]);

  // (Re-)build the Model once the server response arrives.
  useEffect(() => {
    if (!isLoaded) return;
    const json = (layout ?? DEFAULT_LAYOUT) as unknown as IJsonModel;
    setModel(Model.fromJson(json));
  }, [isLoaded, layout]);

  // Sync layout to server on page unload (sendBeacon → XHR fallback).
  useEffect(() => {
    const handler = () => syncToServerBeforeUnload();
    window.addEventListener('beforeunload', handler);
    return () => window.removeEventListener('beforeunload', handler);
  }, [syncToServerBeforeUnload]);

  if (!model) {
    // Brief loading state while server layout fetch resolves.
    return <div className="flex-workspace" />;
  }

  return (
    <div className={`flex-workspace ${terminatorActive ? 'terminator' : ''}`}>
      <Layout
        model={model}
        factory={factory}
        onModelChange={(updatedModel) => {
          setLayout(updatedModel.toJson() as unknown as Record<string, unknown>);
        }}
      />
      <TerminatorLines active={terminatorActive} />
    </div>
  );
}
