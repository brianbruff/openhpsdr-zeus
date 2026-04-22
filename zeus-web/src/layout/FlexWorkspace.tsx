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
