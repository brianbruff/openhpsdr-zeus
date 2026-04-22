import { create } from 'zustand';

// Opaque flexlayout-react JSON blob — we don't strongly-type the tree.
type FlexLayoutJson = Record<string, unknown>;

interface LayoutState {
  layout: FlexLayoutJson | null;
  isLoaded: boolean;
  loadFromServer: () => Promise<void>;
  setLayout: (json: FlexLayoutJson) => void;
  resetLayout: () => void;
  syncToServer: () => void;
  syncToServerBeforeUnload: () => void;
}

let debounceTimer: ReturnType<typeof setTimeout> | null = null;

export const useLayoutStore = create<LayoutState>((set, get) => ({
  layout: null,
  isLoaded: false,

  loadFromServer: async () => {
    try {
      const res = await fetch('/api/ui/layout');
      if (res.status === 404) {
        set({ isLoaded: true });
        return;
      }
      if (!res.ok) {
        set({ isLoaded: true });
        return;
      }
      const dto = (await res.json()) as { layoutJson: string };
      set({ layout: JSON.parse(dto.layoutJson) as FlexLayoutJson, isLoaded: true });
    } catch {
      set({ isLoaded: true });
    }
  },

  setLayout: (json) => {
    set({ layout: json });
    get().syncToServer();
  },

  resetLayout: () => {
    set({ layout: null });
    fetch('/api/ui/layout', { method: 'DELETE' }).catch(() => {});
  },

  syncToServer: () => {
    if (debounceTimer) clearTimeout(debounceTimer);
    debounceTimer = setTimeout(() => {
      const { layout } = get();
      if (!layout) return;
      void fetch('/api/ui/layout', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ layoutJson: JSON.stringify(layout) }),
      });
    }, 1000);
  },

  syncToServerBeforeUnload: () => {
    const { layout } = get();
    if (!layout) return;
    const body = JSON.stringify({ layoutJson: JSON.stringify(layout) });
    const blob = new Blob([body], { type: 'application/json' });
    if (!navigator.sendBeacon('/api/ui/layout-beacon', blob)) {
      void fetch('/api/ui/layout', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body,
        keepalive: true,
      });
    }
  },
}));
