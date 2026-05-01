# All-Panels Workspace — Replace flexlayout-react with react-grid-layout

**Status:** draft (planner output, awaiting maintainer sign-off)
**Owner:** unassigned (split across implementer per PR breakdown — task #6 will spawn after this is approved)
**Branch:** `feature/all-panels` (already created off `feature/meters-panel`)
**Inputs:**
- Task #5 description (this is what created the plan)
- Constraint inheritance from `feature/meters-panel`: react-grid-layout (`^2.2.3`) is already in `package.json`; `MetersPanel.tsx` is the working RGL exemplar
- CLAUDE.md red-light surfaces: **architecture (workspace substrate swap)**, **UX behavior (tabs/splitters/dock/maximize all disappear)** — both must be flagged in the implementing PR description
- Mobile shell stays on flexlayout for now (`MobileApp.tsx` is out of scope; see §6 for what stays installed and why)

**🔴 Red-light flags for maintainer sign-off (please confirm before merge):**
1. Loss of **tabs** — every panel becomes a top-level tile; no two panels can co-occupy the same tile.
2. Loss of **proportional splitters** — operator pixel-grain resizes via RGL's bottom-right grip instead of a draggable splitter line.
3. Loss of **dock-to-edge** — RGL drops the dragged tile at the closest free grid cell; no edge-snap zones.
4. Loss of **maximize/tab close** — there is no maximise button; an "X" remove button replaces both the maximise and the tab-close affordances.
5. The current 5-row stacked sidebar (VFO / S-Meter / DSP / Azimuth / Step) becomes 5 stacked tiles — same visual outcome, but the operator can no longer collapse one into another via tabbing.

---

## 1. Inventory of every PanelDef + proposed default RGL coordinates

The new workspace is a **12-column** grid with a configurable row-height (`WORKSPACE_ROW_HEIGHT_PX = 30`). Defaults below approximate the existing flexlayout `DEFAULT_LAYOUT` shape (left ~75% / right ~25%, with the bottom row spanning the left column).

For reference: the current default layout puts **9 of 16 panels** on screen at once (filter, hero, qrz, logbook, txmeters, vfo, smeter, dsp, azimuth, step — actually 10). The other 6 (cw, tx, ps, band, mode, **meters**) are not in the default — they're available via Add Panel only. The new default shows the same 10 panels.

Coordinate space: `x ∈ [0..11], y ∈ [0..∞], w ∈ [1..12], h ∈ [1..∞]`. The 12-column convention matches MetersPanel's internal grid — keeps mental model consistent.

| Panel id | Name | Category | In default? | Default x,y,w,h | Notes |
|---|---|---|:---:|---|---|
| `filter` | Bandwidth Filter | dsp | ✓ | 0,0,9,2 | Top of left column, narrow strip |
| `hero` | Panadapter · World Map | spectrum | ✓ | 0,2,9,12 | Left column main canvas |
| `qrz` | QRZ Lookup | tools | ✓ | 0,14,3,6 | Bottom-left of left column |
| `logbook` | Logbook | log | ✓ | 3,14,3,6 | Bottom-middle of left column |
| `txmeters` | TX Stage Meters | meters | ✓ | 6,14,3,6 | Bottom-right of left column |
| `vfo` | Frequency · VFO | vfo | ✓ | 9,0,3,4 | Top of right column (sized for big-digit VFO) |
| `smeter` | S-Meter | meters | ✓ | 9,4,3,2 | Below VFO |
| `dsp` | DSP | dsp | ✓ | 9,6,3,3 | Below S-Meter |
| `azimuth` | Azimuth Map | tools | ✓ | 9,9,3,8 | Below DSP — tall slot for the map |
| `step` | Tuning Step | controls | ✓ | 9,17,3,3 | Bottom of right column |
| `cw` | CW Keyer | tools | — | 0,20,4,4 | (defaults below the visible default tiles; appears when added) |
| `tx` | TX (Drive · Tune · Mic · Filter) | controls | — | 4,20,4,5 | |
| `ps` | PureSignal | tools | — | 8,20,4,5 | |
| `band` | Band Buttons | controls | — | 0,25,6,2 | |
| `mode` | Mode Buttons | controls | — | 6,25,4,2 | |
| `meters` | Meters (configurable) | meters | — | 0,27,6,8 | Multi-instance — see §3.5 |

**Visual sanity check** (ASCII, columns 0–11 → 80 cols):

```
  0    1    2    3    4    5    6    7    8    9    10   11
  ┌───────────────────────────────────────────────┬─────────────┐  y=0
  │              filter (0..8, h=2)                │             │
  ├───────────────────────────────────────────────┤    vfo      │  y=2
  │                                                │  (h=4)      │
  │                                                ├─────────────┤  y=4
  │                                                │   smeter    │
  │              hero (0..8, h=12)                 │   (h=2)     │  y=6
  │                                                ├─────────────┤
  │                                                │             │
  │                                                │     dsp     │
  │                                                │   (h=3)     │  y=9
  │                                                ├─────────────┤
  │                                                │             │
  │                                                │             │
  │                                                │  azimuth    │
  │                                                │   (h=8)     │  y=14
  ├──────────────┬────────────┬────────────────────┤             │
  │     qrz      │  logbook   │   txmeters         │             │
  │   (h=6)      │   (h=6)    │     (h=6)          │             │  y=17
  │              │            │                    ├─────────────┤
  │              │            │                    │   step (h=3)│
  └──────────────┴────────────┴────────────────────┴─────────────┘  y=20
```

This preserves the operator's muscle memory: panadapter on the left taking the bulk of the screen, VFO at the top-right, S-Meter just below it, sidebar continuing down with DSP / Azimuth / Step, and the bottom row of the left column carrying QRZ / Logbook / TX Stage Meters — same as today.

The default layout constant lives in `zeus-web/src/layout/defaultLayout.ts` (replaces the existing flexlayout JSON) as `DEFAULT_WORKSPACE_LAYOUT: WorkspaceLayout`.

---

## 2. Workspace shell

### 2.1 Where the new code lives

**Replace `zeus-web/src/layout/FlexWorkspace.tsx` contents in-place**, preserving the export name `FlexWorkspace` so `App.tsx:47` import is unchanged. The file becomes the RGL-based workspace and the misnomer can be cleaned up in a follow-up rename PR (file → `WorkspaceShell.tsx`, export → `Workspace`). Doing the rename now adds churn to every grep result without changing behaviour; defer.

(Justification for the in-place edit vs. new file: keeps the diff focused on substance, doesn't touch `App.tsx`, avoids 16+ panel files needing import updates.)

### 2.2 New shape, mirroring MetersPanel exactly

The shell mirrors `MetersPanel.MetersCanvas` (zeus-web/src/layout/panels/MetersPanel.tsx:389–470) one-to-one:

```tsx
export function FlexWorkspace() {              // export name unchanged
  const { workspace, setWorkspace, ... } = useLayoutStore();
  const { width, containerRef, mounted } = useContainerWidth();

  return (
    <div ref={containerRef} className="all-panels-workspace">
      {!mounted ? <SilentMeasureSpacer /> : (
        <ResponsiveGridLayout
          className="all-panels-grid"
          width={width}
          breakpoints={{ lg: 0 }}
          cols={{ lg: WORKSPACE_GRID_COLS }}      // 12
          rowHeight={WORKSPACE_ROW_HEIGHT_PX}     // 30
          margin={[6, 6]}
          containerPadding={[6, 6]}
          dragConfig={{ handle: '.workspace-tile-drag-handle', bounded: false }}
          onLayoutChange={onLayoutChange}
          layouts={{ lg: workspace.tiles.map(toRglLayoutItem) }}
        >
          {workspace.tiles.map((t) => (
            <div key={t.uid} data-tile-uid={t.uid}>
              <PanelTile tile={t} onRemove={...} onConfigChange={...} />
            </div>
          ))}
        </ResponsiveGridLayout>
      )}
      <AddPanelButton onClick={() => setAddOpen(true)} />
      {addOpen && <AddPanelModal ... onClose={...} />}
    </div>
  );
}
```

**No `Model.fromJson()`-once dance** — the RGL component does NOT remount on `layouts={...}` updates the way flexlayout's `<Layout model={...}>` does. RGL is happy reading from a fresh `layouts` prop on every render; that was the entire reason MetersPanel's grid mechanics work without the workaround flexlayout needed.

**No `node` prop drilling** — every panel just renders `<panel.component />` with no node argument. MetersPanel's per-instance config is supplied as a direct prop (see §4.4 for the refactor).

### 2.3 `PanelTile` — the wrapper around each panel body

```tsx
function PanelTile({ tile, onRemove, onConfigChange }: PanelTileProps) {
  const def = PANELS[tile.panelId];
  if (!def) return null;
  const Component = def.component;
  return (
    <div className="workspace-tile">
      <TileChrome
        title={def.name}
        onRemove={onRemove}
        // For multi-instance panels, the title can come from instanceConfig.
        // Keep the chrome generic; specifics live in the panel body.
      />
      <div className="workspace-tile-body">
        {def.id === 'meters'
          ? <MetersPanel
              config={(tile.instanceConfig as MetersPanelConfig) ?? EMPTY_METERS_CONFIG}
              setConfig={(next) => onConfigChange(next)}
            />
          : <Component />}
      </div>
    </div>
  );
}
```

Notes:
- Most panels take no props — they're rendered as-is.
- The `meters` panel gets its config + setter as direct props, replacing the `useMetersPanelConfig(node)` hook from the flexlayout era. See §4.4.
- The conditional inside `PanelTile` is the only special-case in v1. If/when more panels grow per-instance config, generalise this to `def.takesInstanceConfig` and pass `(config, setConfig)` uniformly. Out of scope for this PR.

### 2.4 `useContainerWidth` is sufficient

The existing `useContainerWidth` hook (already used by MetersPanel; comes from `react-grid-layout`) handles the workspace too. No `WidthProvider` HOC needed. The `mounted` flag prevents the 1280-px first-paint flash. The hook reads from the immediate parent `<div>` (the `all-panels-workspace`), so the workspace fills its container exactly.

### 2.5 Inner-MetersPanel-RGL guard

MetersPanel's inner RGL is unaffected — it has its own `useContainerWidth` and its own `cols={12}`. The two RGL instances are independent: outer RGL runs on the workspace `<div>`, inner RGL runs on the `meters-canvas` `<div>` inside one tile. No CSS leak risk because both already use namespaced classes (`.meters-grid`, `.all-panels-grid`).

---

## 3. Categorized AddPanel modal (PR B)

### 3.1 Wireframe

```
┌──────────────────────────────────────────────────────────────────┐
│  ADD PANEL                                              [×]      │
├────────────┬─────────────────────────────────────────────────────┤
│            │   Search: [_________________________]               │
│  All       │                                                     │
│ ─────────  │   ┌────────────────────────────────────────────┐   │
│  Spectrum  │   │  Panadapter · World Map           [+ Add]  │   │
│  VFO       │   │  panadapter · waterfall · spectrum · map   │   │
│  Meters    │   ├────────────────────────────────────────────┤   │
│  DSP       │   │  Bandwidth Filter                 [+ Add]  │   │
│  Log       │   │  filter · bandwidth · passband · ribbon    │   │
│  Tools     │   ├────────────────────────────────────────────┤   │
│  Controls  │   │  ...                                        │   │
│            │   └────────────────────────────────────────────┘   │
│            │                                                     │
└────────────┴─────────────────────────────────────────────────────┘
```

Left rail = vertical list of category chips (current six values: `spectrum, vfo, meters, dsp, log, tools, controls` — keep `PanelCategory` enum as-is, no extension needed). "All" stays at the top as a passthrough.

Right pane = panel cards filtered by `(searchTerm, selectedCategory)`. Existing `multiInstance` "+ Add another" badge stays as-is.

### 3.2 The "Meters category drill-down" question

The task asked: when the operator clicks the **Meters** category, do we (a) drill into the catalog so they pick a specific reading, or (b) just show panels in that category and let them open the Library inside the Meters tile they create?

**Recommendation: (b) — show the Meters category panels (S-Meter, TX Stage Meters, Meters), and clicking the configurable "Meters" entry adds an empty tile.** Justification:

1. **One source of truth for catalog UX.** The MetersPanel already owns the Library drawer with search + category chips + the right-set-of-defaults-per-reading logic. Drilling into the catalog from AddPanel duplicates all of it (or worse, requires extracting it into a standalone modal). Keep one place.
2. **Two-step flow is no worse than the alternative.** Option (a) would be: open AddPanel → pick category Meters → pick reading → tile lands with one widget. Option (b) is: open AddPanel → pick Meters → tile lands empty → tap ⚙ → pick reading. Same number of clicks; option (b) re-uses muscle memory the operator builds for adding subsequent widgets.
3. **The Meters category remains useful.** Operators can add the legacy `S-Meter` (`smeter`) and `TX Stage Meters` (`txmeters`) tiles too — they're independent panels with their own purpose.
4. **PR scope.** Option (a) materially expands PR B (catalog UI extracted, drill-back UX, multi-step modal state). Option (b) is purely the categorized list rewrite.

If operators later complain that the empty-tile-then-Library is too many steps, we can add a "with first widget pre-selected" shortcut as a follow-up — but only if real usage demands it.

### 3.3 Wire into the workspace

`AddPanelModal.onAdd(panelId)` calls `setWorkspace(workspace.addTile(panelId))`. The workspace's `addTile` helper:
- Mints `uid = crypto.randomUUID()` for the tile.
- Picks a default `(x, y, w, h)` from a per-panel default span (see §1 table, defaults extracted into `DEFAULT_TILE_SPAN: Record<PanelId, {w, h}>`).
- Places at `y = max existing y + h, x = 0` — the same auto-placement strategy MetersPanel uses for new widgets (`metersConfig.ts:placeWidgetInGrid`). RGL compacts upward into any free space at render.
- For multi-instance panels (just `meters` today), seeds `instanceConfig: EMPTY_METERS_CONFIG`.

### 3.4 Where the "+" lives in the workspace

flexlayout had a per-tabset `+` button. The new shell has **one** "Add Panel" button (top-right of the workspace, fixed-positioned `12 px / 12 px` from the corner, behind any maximised modal layer) plus the existing "Reset Layout" button in `DisplayPanel`. No per-tile add affordance — there are no tabsets to add to.

### 3.5 Multi-instance still works

The `multiInstance: true` flag on `PanelDef` (from `feature/meters-panel`) is preserved. The new modal still shows the "+ Add another" badge for multi-instance panels that already exist. The unique-per-tile `uid` (no longer the `meters-<uuid>` component string trick FlexLayout needed) is the per-tile handle in the workspace `tiles[]`.

---

## 4. Persistence (extend `layout-store.ts`)

### 4.1 Workspace shape

```typescript
// zeus-web/src/layout/workspace.ts (NEW)
export interface WorkspaceTile {
  /** Stable per-tile id. Survives drag/resize so React keys + RGL identity
   *  stay aligned. */
  uid: string;
  /** Panel registry id (e.g. 'hero', 'meters'). For multi-instance panels,
   *  the same panelId can appear on multiple tiles; the per-tile uid is
   *  what differentiates them. */
  panelId: string;
  x: number;
  y: number;
  w: number;
  h: number;
  /** Opaque per-instance config for panels that need it. Only `meters`
   *  uses this in v1 (carries MetersPanelConfig). Forward-compatible:
   *  unknown panels' instanceConfig is preserved as-is across save/load. */
  instanceConfig?: unknown;
}

export interface WorkspaceLayout {
  schemaVersion: 6;
  tiles: WorkspaceTile[];
}

export const EMPTY_WORKSPACE_LAYOUT: WorkspaceLayout = {
  schemaVersion: 6,
  tiles: [],
};
```

### 4.2 Default layout

```typescript
// zeus-web/src/layout/defaultLayout.ts (REWRITTEN — replaces the existing
// flexlayout JSON tree)
export const DEFAULT_WORKSPACE_LAYOUT: WorkspaceLayout = {
  schemaVersion: 6,
  tiles: [
    { uid: 'tile-filter',   panelId: 'filter',   x: 0, y: 0,  w: 9, h: 2 },
    { uid: 'tile-hero',     panelId: 'hero',     x: 0, y: 2,  w: 9, h: 12 },
    { uid: 'tile-qrz',      panelId: 'qrz',      x: 0, y: 14, w: 3, h: 6 },
    { uid: 'tile-logbook',  panelId: 'logbook',  x: 3, y: 14, w: 3, h: 6 },
    { uid: 'tile-txmeters', panelId: 'txmeters', x: 6, y: 14, w: 3, h: 6 },
    { uid: 'tile-vfo',      panelId: 'vfo',      x: 9, y: 0,  w: 3, h: 4 },
    { uid: 'tile-smeter',   panelId: 'smeter',   x: 9, y: 4,  w: 3, h: 2 },
    { uid: 'tile-dsp',      panelId: 'dsp',      x: 9, y: 6,  w: 3, h: 3 },
    { uid: 'tile-azimuth',  panelId: 'azimuth',  x: 9, y: 9,  w: 3, h: 8 },
    { uid: 'tile-step',     panelId: 'step',     x: 9, y: 17, w: 3, h: 3 },
  ],
};
```

The `tile-*` uids are stable strings (not random UUIDs) for the default layout — this lets a future migration map "the old default 'qrz' tile" to a new layout without losing operator overrides on it. Net wire cost: ~30 chars × 10 tiles = ~300 B.

### 4.3 `layout-store.ts` extension

Most of the existing store stays — it already handles "JSON blob in, debounced PUT to `/api/ui/layout`, sendBeacon on unload, schema-version-bump-resets-saved-layout". Changes:

```diff
- const LAYOUT_SCHEMA_VERSION = 5;
+ const LAYOUT_SCHEMA_VERSION = 6;  // 2026-05-01: swapped flexlayout-react workspace
+                                   //             for react-grid-layout tiles. Existing
+                                   //             flexlayout JSON cannot be parsed by RGL,
+                                   //             so all v5 layouts reset to default on first
+                                   //             load (the existing version-mismatch path
+                                   //             handles this — `DELETE /api/ui/layout`).

- type FlexLayoutJson = Record<string, unknown>;
+ // Workspace JSON is now WorkspaceLayout (schema-versioned), but the store
+ // still treats it as opaque JSON on the wire.

interface LayoutState {
-  layout: FlexLayoutJson | null;
+  workspace: WorkspaceLayout;             // never null — fall back to DEFAULT_WORKSPACE_LAYOUT
   isLoaded: boolean;
   loadFromServer: () => Promise<void>;
-  setLayout: (json: FlexLayoutJson) => void;
+  setWorkspace: (next: WorkspaceLayout) => void;
   resetLayout: () => void;
   syncToServer: () => void;
   syncToServerBeforeUnload: () => void;
+  // Helpers — keep mutation logic out of components.
+  addTile: (panelId: string) => void;
+  removeTile: (uid: string) => void;
+  updateTilePlacement: (uid: string, layout: Pick<WorkspaceTile, 'x'|'y'|'w'|'h'>) => void;
+  updateTileInstanceConfig: (uid: string, instanceConfig: unknown) => void;
}
```

The `loadFromServer` path's existing parse stays the same shape (JSON.parse the layoutJson string from the server response into the `workspace` field). Validation goes through a new `parseWorkspaceLayout(raw): WorkspaceLayout` (same defensive pattern as `parseMetersPanelConfig` from `metersConfig.ts:101` — drop unknown-version blobs to default; filter out tiles whose `panelId` is no longer in `PANELS`; coerce numeric fields).

### 4.4 MetersPanel refactor — config as prop

The current `MetersPanel.tsx` couples to `flexlayout-react.TabNode` (lines 24, 58–87). Refactor so `MetersPanel` always takes `(config, setConfig)` as props, and the FlexLayout-only branch (`MetersPanelBound`) goes away:

```diff
- import { Actions, type TabNode } from 'flexlayout-react';
- ...
- interface MetersPanelProps { node?: TabNode }
-
- export function MetersPanel({ node }: MetersPanelProps) {
-   if (!node) {
-     return <MetersPanelInner config={EMPTY_METERS_CONFIG} setConfig={() => {}} />;
-   }
-   return <MetersPanelBound node={node} />;
- }
- function MetersPanelBound({ node }: { node: TabNode }) { ... }
+ // No flexlayout import.
+ export interface MetersPanelProps {
+   config: MetersPanelConfig;
+   setConfig: (next: MetersPanelConfig) => void;
+   /** Optional — when provided, the panel calls this on title rename so the
+    *  workspace can update its tile chrome too. The new workspace doesn't
+    *  use this in v1 (titles live in PANELS only); reserved for future. */
+   renameTab?: (name: string) => void;
+ }
+
+ export function MetersPanel({ config, setConfig, renameTab }: MetersPanelProps) {
+   return <MetersPanelInner config={config} setConfig={setConfig} renameTab={renameTab} />;
+ }
```

`MetersPanelInner` body is unchanged — it already takes `(config, setConfig)`. The `useMetersPanelConfig(node)` hook in `metersConfig.ts:161` becomes dead code; remove it (and the flexlayout-react import in `metersConfig.ts:16`). The `parseMetersPanelConfig` parser stays; the workspace store's `updateTileInstanceConfig` will eventually call it once on load and trust the runtime shape thereafter.

### 4.5 Per-tile config wiring in `PanelTile`

```typescript
function PanelTile({ tile }: { tile: WorkspaceTile }) {
  const updateInstanceConfig = useLayoutStore((s) => s.updateTileInstanceConfig);
  const removeTile = useLayoutStore((s) => s.removeTile);
  // ...
  if (def.id === 'meters') {
    const config = parseMetersPanelConfig(tile.instanceConfig);
    return (
      <div className="workspace-tile">
        <TileChrome title={config.title ?? def.name} onRemove={() => removeTile(tile.uid)} />
        <MetersPanel
          config={config}
          setConfig={(next) => updateInstanceConfig(tile.uid, next)}
        />
      </div>
    );
  }
  // ...
}
```

This is a strictly smaller seam than the flexlayout `Actions.updateNodeAttributes` indirection — the operator's edit hits the store directly, the store's debounced PUT fires, the server saves the same shape it received.

### 4.6 Wire format compatibility on `/api/ui/layout`

Server-side: `LayoutStore` (from prior task's plumbing — `Zeus.Server.Hosting/LayoutStore.cs`) treats `layoutJson` as opaque text. Zero server changes. The client serialises `WorkspaceLayout` instead of FlexLayout JSON; the server doesn't care.

---

## 5. Tile chrome (shared component)

```tsx
// zeus-web/src/layout/TileChrome.tsx (NEW)
import { GripVertical, X } from 'lucide-react';

export interface TileChromeProps {
  title: string;
  onRemove: () => void;
  /** Optional — extra header buttons (e.g. ⚙ for the Meters panel). Rendered
   *  between the title and the X. */
  rightSlot?: React.ReactNode;
}

export function TileChrome({ title, onRemove, rightSlot }: TileChromeProps) {
  return (
    <div className="workspace-tile-header">
      <span
        className="workspace-tile-drag-handle"
        aria-hidden="true"
        title="Drag to reposition"
        onClick={(e) => e.stopPropagation()}
      >
        <GripVertical size={12} />
      </span>
      <span className="workspace-tile-title">{title}</span>
      {rightSlot}
      <button
        type="button"
        className="workspace-tile-close"
        aria-label={`Remove ${title}`}
        title="Remove panel"
        onClick={(e) => { e.stopPropagation(); onRemove(); }}
        onMouseDown={(e) => e.stopPropagation()}
      >
        <X size={12} />
      </button>
    </div>
  );
}
```

Style sketch (`zeus-web/src/styles/all-panels.css` — NEW; mirrors `meters-grid.css` patterns):

```css
@import 'react-grid-layout/css/styles.css';
@import 'react-resizable/css/styles.css';

.all-panels-workspace { position: relative; height: 100%; overflow: auto; }

.all-panels-grid.react-grid-layout { background: transparent; }

.all-panels-grid .react-grid-item {
  background: transparent;
  transition: transform var(--dur-fast) var(--ease-out),
              width var(--dur-fast) var(--ease-out),
              height var(--dur-fast) var(--ease-out);
}
.all-panels-grid .react-grid-item.react-draggable-dragging,
.all-panels-grid .react-grid-item.resizing { z-index: 4; opacity: 0.92; }
.all-panels-grid .react-grid-placeholder {
  background: var(--accent); opacity: 0.18; border-radius: var(--r-sm);
}
.all-panels-grid .react-resizable-handle { /* same chevron treatment as meters-grid.css */ }

.workspace-tile {
  display: flex; flex-direction: column; height: 100%;
  background: var(--bg-1);
  border: 1px solid var(--panel-border);
  border-radius: var(--r-sm);
  overflow: hidden;
  box-shadow: var(--panel-shadow);
}
.workspace-tile-header {
  height: 24px; flex-shrink: 0;
  display: flex; align-items: center; gap: 6px; padding: 0 8px;
  background: linear-gradient(180deg, var(--panel-top), var(--panel-bot));
  border-bottom: 1px solid var(--panel-border);
}
.workspace-tile-title {
  flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
  font-size: 11px; text-transform: uppercase; letter-spacing: 0.08em;
  color: var(--fg-1); font-family: var(--font-sans);
}
.workspace-tile-drag-handle { color: var(--fg-3); cursor: grab; display: inline-flex; }
.workspace-tile-drag-handle:active { cursor: grabbing; }
.workspace-tile-close {
  width: 16px; height: 16px; display: inline-flex; align-items: center; justify-content: center;
  color: var(--fg-3); background: transparent; border: 0; border-radius: var(--r-xs);
  cursor: pointer;
}
.workspace-tile-close:hover { color: var(--tx); background: var(--bg-2); }
.workspace-tile-body { flex: 1; min-height: 0; overflow: auto; }
```

Drag handle is the grip icon **only** (matching MetersPanel's `dragConfig.handle: '.meter-widget-drag-handle'` discipline). Title is text-only — clicking it does nothing (no drag, no rename in v1; rename was a Meters-tile feature only). The X removes the tile from `workspace.tiles[]` and the layout is persisted.

---

## 6. Loss of features (explicit, for the maintainer)

| Feature today (flexlayout) | After RGL swap | Severity |
|---|---|---|
| **Tabs** — multiple panels in one tabset, click-to-switch | Gone. Each panel is a top-level tile. | 🔴 |
| **Splitters** — drag-to-resize line between siblings, proportional | Replaced by per-tile bottom-right resize handle (RGL). Each tile resizes independently; siblings don't reflow proportionally. | 🔴 |
| **Dock-to-edge** — drag a tab to the right edge of another tabset to dock | Gone. RGL drops at the closest free grid cell. | 🟡 |
| **Maximize tab** | Gone. No equivalent in RGL. | 🟡 |
| **Tab close (X on the strip)** | Replaced by the X button on each tile's header chrome. | 🟢 (functionally equivalent) |
| **Per-tabset "+ Add panel" button** | Replaced by a single workspace-level "+ Add panel" button (top-right corner, fixed). | 🟢 (functionally equivalent) |
| **Reset layout** (DisplayPanel button) | Unchanged — still calls `useLayoutStore.resetLayout()`, still triggers `window.location.reload()`. | 🟢 |
| **Mobile shell** (`MobileApp.tsx`) | Unchanged — out of scope; still uses the existing chrome. `flexlayout-react` stays in `package.json` because `MobileApp` does not import it (verified — see grep results for "flexlayout" in src/) — actually neither does mobile, but since mobile is its own shell, `flexlayout-react` becomes unreferenced. **Decision: leave the dep in `package.json` for one release** in case rollback is needed; remove in a follow-up after the maintainer confirms the swap stuck. | 🟢 |

**Maintainer must explicitly approve items marked 🔴 before merge.** The plan's working assumption is that these losses are acceptable per the task description: *"The user has accepted the loss of tabs/splitters/dock-to-edge as a trade-off."*

**Note on `flexlayout-react` removal:** I checked `MobileApp.tsx` — it doesn't import flexlayout. The only remaining flexlayout consumers after this swap will be the workspace shell (which no longer uses it). The dep is unreferenced. Recommend `package.json` cleanup as a separate trivial PR after one stable release on the new shell.

---

## 7. Migration

The existing version-mismatch path in `layout-store.ts:65` does the right thing as-is:

```typescript
async loadFromServer() {
  if (getStoredVersion() !== LAYOUT_SCHEMA_VERSION) {
    await fetch('/api/ui/layout', { method: 'DELETE' }).catch(() => {});
    setStoredVersion(LAYOUT_SCHEMA_VERSION);
    set({ layout: null, isLoaded: true });
    return;
  }
  // ...
}
```

Bumping `LAYOUT_SCHEMA_VERSION` from 5 → 6 means: on every existing user's first browser load after the swap, `localStorage` reports v5, the constant is v6, the saved layout JSON is DELETEd from the server, and the workspace falls back to `DEFAULT_WORKSPACE_LAYOUT`.

**No migration shim needed.** Operator's old saved flexlayout-shaped JSON is never parsed by the new code — we just throw it away.

**Documentation note for the implementer:** add a one-line entry to the existing version-comments block in `layout-store.ts` (which already documents v2→v5 changes):

```
//   v6 (2026-05-01): swapped flexlayout-react workspace for react-grid-layout
//                    tiles. Saved v5 layouts are not parseable; reset on load.
```

---

## 8. PR breakdown

**Recommend 2 PRs.** A single bundled PR is technically possible but would make code review harder (substrate swap + new modal in one diff = ~1,800 LOC change), and the risk of the categorized AddPanel discovering some UX wrinkle that holds up the substrate swap is real. Splitting also lets the maintainer bench-test PR A on HL2 before any UX polish lands.

### PR A — RGL workspace shell (substrate swap)

**Owner:** frontend implementer.
**Scope:**
- `zeus-web/src/layout/workspace.ts` (NEW) — `WorkspaceTile`, `WorkspaceLayout`, `EMPTY_WORKSPACE_LAYOUT`, `parseWorkspaceLayout` parser, `DEFAULT_TILE_SPAN: Record<PanelId, {w,h}>`.
- `zeus-web/src/layout/defaultLayout.ts` — replace flexlayout JSON with `DEFAULT_WORKSPACE_LAYOUT` (10 tiles per §1).
- `zeus-web/src/layout/FlexWorkspace.tsx` — replace contents with the RGL shell per §2 (export name unchanged). Drop `flexlayout-react` imports. Drop `Model.fromJson` / `onModelChange` / `onRenderTabSet` / `BorderNode` / `TabSetNode` / `Actions` / `DockLocation` machinery.
- `zeus-web/src/layout/TileChrome.tsx` (NEW) — the shared header per §5.
- `zeus-web/src/layout/AddPanelModal.tsx` — keep current flat layout for now (categorization in PR B). Refactor `onAdd(panelId)` to call `useLayoutStore().addTile(panelId)` instead of pushing into a flexlayout model.
- `zeus-web/src/styles/all-panels.css` (NEW) — per §5.
- `zeus-web/src/styles/flex-layout.css` (KEEP for now — referenced inline by `FlexWorkspace.tsx`'s old import; the new file should drop the import. Leave the CSS file in place; it's tiny and removing it is a cosmetic cleanup that adds churn).
- `zeus-web/src/state/layout-store.ts` — bump `LAYOUT_SCHEMA_VERSION 5 → 6`, swap `layout: FlexLayoutJson | null` → `workspace: WorkspaceLayout`, add `addTile / removeTile / updateTilePlacement / updateTileInstanceConfig` helpers, `setWorkspace` replaces `setLayout`. Loading path stays the same shape (parse JSON string from server response, validate via `parseWorkspaceLayout`, fall back to `DEFAULT_WORKSPACE_LAYOUT`).
- `zeus-web/src/layout/panels/MetersPanel.tsx` — per §4.4: `MetersPanel` now takes `(config, setConfig, renameTab?)` directly, drop the `node?: TabNode` prop and `MetersPanelBound`.
- `zeus-web/src/components/meters/metersConfig.ts` — drop the flexlayout-react import + the `useMetersPanelConfig(node)` hook (callers move to direct props per §4.4). Keep `parseMetersPanelConfig`.
- `zeus-web/src/layout/panels/__tests__/MetersPanel.test.tsx` — update test harness to pass `(config, setConfig)` props instead of mocking a TabNode.
- `zeus-web/src/state/__tests__/layout-store.test.ts` (NEW) — Vitest: addTile / removeTile / updateTilePlacement / updateTileInstanceConfig round-trip, schema-version-mismatch resets, parseWorkspaceLayout drops invalid tiles.
- `zeus-web/src/layout/__tests__/FlexWorkspace.test.tsx` (NEW) — Vitest: render workspace from default layout, RGL receives the right `layouts.lg` array, removeTile mutation reflects in next render, AddPanel modal opens when "+" clicked.
- `package.json` — DO NOT remove `flexlayout-react` in this PR (see §6). Document in PR description that a follow-up dep-cleanup PR will drop it after one stable release.

**PR description must include:**
- A "🔴 Architecture / UX trade-off" section explicitly listing the §6 losses.
- Screenshot(s) of the new workspace at default layout vs. the current flexlayout default.
- Manual confirmation list (see §9 below).

**Acceptance criteria:**
- All Vitest tests pass.
- `npm --prefix zeus-web run lint && npm --prefix zeus-web run typecheck && npm --prefix zeus-web run test` green.
- Manual: load the page on an existing operator's browser → workspace resets to default → all 10 default tiles appear in the right places per the §1 sanity-check ASCII.
- Manual: drag a tile, refresh → tile is in the new position.
- Manual: resize a tile via the bottom-right grip, refresh → size persists.
- Manual: the inner Meters tile (added via Add Panel) keeps its widget config across full browser restart.

**Estimate:** ~1,000–1,200 LOC across ~12 files. Diff would be heavy on FlexWorkspace.tsx (full rewrite) and layout-store.ts (~50% rewrite). MetersPanel changes are surgical (~30 LOC delta). New files (TileChrome, all-panels.css, workspace.ts, two test files) are small.

### PR B — Categorized AddPanel + tile-chrome polish

**Owner:** frontend implementer (can be a different person; PR A's deliverables are the only prerequisite).
**Scope:**
- `zeus-web/src/layout/AddPanelModal.tsx` — rewrite to the §3.1 wireframe (left rail of categories, right pane of panel cards). Keep the existing search + multiInstance "+ Add another" badge logic. Add Vitest covering category filter / search / multi-instance "+ Add another" / Meters category showing `smeter, txmeters, meters`.
- `zeus-web/src/layout/TileChrome.tsx` — small polish pass: keyboard accessibility (tab / enter to focus the close button; aria-labels per panel), a hover-to-reveal close button option (so the X doesn't permanently fight the title for visual real estate at narrow widths), tooltip on the drag grip explaining "Drag to reposition".
- `zeus-web/src/layout/__tests__/AddPanelModal.test.tsx` (NEW) — Vitest for category drill-down behaviour: clicking "Meters" shows three panels; selecting Meters adds an empty tile; selecting S-Meter does NOT use multi-instance.
- A short Playwright smoke (see §9.2) validates the full flow.

**Acceptance criteria:**
- Vitest + lint + typecheck green.
- Manual: AddPanel opens via the "+" button at the workspace top-right; categories filter the right pane; the Meters category shows the three meter-class panels; clicking "Meters" adds an empty tile that's immediately operable.

**Estimate:** ~400 LOC.

---

## 9. Test plan

### 9.1 Vitest (lives in PRs A + B as above)

- `zeus-web/src/state/__tests__/layout-store.test.ts` — workspace mutations, schema-version reset, parseWorkspaceLayout (invalid tiles dropped, missing schemaVersion → empty, unknown panelId tiles → dropped, instanceConfig preserved verbatim through parse → serialise → re-parse cycle).
- `zeus-web/src/layout/__tests__/FlexWorkspace.test.tsx` — workspace renders default tiles, calls onLayoutChange when RGL fires, addTile / removeTile reflect in the next render. Mock RGL via `vi.mock('react-grid-layout', ...)` if its DOM APIs (ResizeObserver) make jsdom upset — same trick used in MetersPanel tests.
- `zeus-web/src/layout/__tests__/AddPanelModal.test.tsx` — categories filter behavior; multi-instance badge; Meters category drill (renders smeter+txmeters+meters cards; selecting meters fires onAdd with panelId='meters').
- `zeus-web/src/layout/panels/__tests__/MetersPanel.test.tsx` — already exists; update to pass `(config, setConfig)` props directly.
- `zeus-web/src/components/meters/__tests__/metersConfig.test.ts` — already exists; remove tests for the deleted `useMetersPanelConfig(node)` hook.

### 9.2 Playwright manual smoke list (run on `feature/all-panels` after each PR)

This list goes in the PR description; the implementer or maintainer runs it before merge. **Cannot be fully automated until Zeus has a Playwright harness wired** (currently no e2e tests exist — manual is fine).

**PR A smoke:**
1. Existing user: open the app on a profile with a saved v5 layout → workspace resets to default → 10 tiles appear per §1.
2. Drag the `vfo` tile from `(9, 0)` to `(0, 0)`. The `filter` tile should bump out of the way per RGL compaction. Refresh → `vfo` is at `(0, 0)`.
3. Resize the `hero` tile from `9×12` to `9×16` via the bottom-right grip. Refresh → size persists.
4. Click the X on the `step` tile. Tile vanishes. Refresh → still gone.
5. Click "+ Add Panel" in the workspace top-right corner → AddPanel modal opens. Add `cw`. The CW Keyer tile lands at the next free row.
6. Add `meters` (a Meters tile). Open its Library drawer (gear icon). Add 3 widgets (S Pk amber HBar, ALC GR HBar, FwdW dial). Verify they render. Refresh full browser → the Meters tile is still there, with the same 3 widgets, in the same positions.
7. Add a SECOND `meters` tile via Add Panel ("+ Add another" badge present). Add a different widget set. Refresh → both tiles independently restored.
8. Inspect `localStorage.getItem('zeus.layout.schemaVersion')` → should read `"6"`.
9. Inspect a `PUT /api/ui/layout` request body in DevTools → should be a JSON `{layoutJson: "{...}"}` where the inner JSON has `schemaVersion: 6` and a `tiles` array.
10. Mobile (≤900 px viewport) → still renders the existing `MobileApp` (untouched).

**PR B smoke:**
11. Click "+ Add Panel" → modal opens with category rail visible at left.
12. Click "Meters" category → right pane shows S-Meter, TX Stage Meters, Meters (3 cards).
13. Click "Tools" category → right pane shows QRZ, Azimuth, CW, PureSignal (4 cards).
14. Search for "tune" → narrows results across categories (matches `tx`, `step`, `cw` panels via tags).
15. With one Meters tile in the workspace, click "Meters" category → the configurable Meters card shows the "+ Add another" badge.
16. Selecting "Meters" from the modal adds an empty tile; the operator immediately taps ⚙ inside it → Library drawer opens; the catalog list works.

### 9.3 Risk surfaces flagged for the maintainer

- **Hero panel reflow.** The panadapter inside `HeroPanel` currently re-measures its WebGL canvas on every flexlayout `onModelChange`. With RGL the tile is mounted/remounted on add/remove, but **drag and resize do NOT remount the tile** (RGL preserves the React element across `layouts` updates — it just changes the wrapping `<div>`'s position/size). Flag for an HL2 manual confirmation: drag the hero tile, then resize it, and confirm the panadapter trace continues without flicker.
- **Per-tile resize on the panadapter.** The hero is the only tile that owns a heavy WebGL context. Resizing it triggers a `<canvas>` resize → WebGL viewport reset → potentially one frame of black. Acceptable per "no visual design changes beyond what's needed for the swap", but flag it.
- **No tab-strip means no per-panel close affordance for users used to flexlayout's X-on-tab.** The tile chrome's X serves the same purpose but lives in a different place. Flag in PR A description; ask the maintainer to confirm the X position is acceptable.

---

## 10. Open questions / decisions for the maintainer

1. **Workspace row height.** Plan picks `WORKSPACE_ROW_HEIGHT_PX = 30`. MetersPanel uses 40. Smaller row height gives finer pixel-grain placement but smaller minimum tile heights. Reasonable alternatives: 24, 30, 40. Confirm.
2. **Tile body scrollability.** Plan makes `.workspace-tile-body` `overflow: auto` so tall content (e.g. a logbook with many rows) can scroll inside its tile. Some panels (panadapter, S-meter) prefer `overflow: hidden` because they fill the body exactly. Plan says "let the panel body decide via its own CSS", but a per-panel hint on `PanelDef` (`overflow?: 'auto'|'hidden'`) might be cleaner. Recommend: defer; only add if a real bug surfaces.
3. **Meters tile rename UX.** The current MetersPanel supports double-click-on-title to rename the tile. With the new chrome the title is in the tile header (the workspace-level chrome), not in the panel body. Three options: (a) keep rename in MetersPanel's own internal header (next to the gear) — wastes vertical space; (b) move rename to the workspace tile-chrome title via an optional `editableTitle?: boolean` flag on `PanelDef` — clean; (c) drop rename in v1 and add it back later. Recommend (c) for PR A scope; revisit if operators ask.
4. **"+ Add Panel" button placement.** Plan puts it top-right of the workspace, fixed `12 px / 12 px`. Alternative: a bottom-right floating action button. Top-right matches DisplayPanel's existing "Reset Layout" button location and keeps both controls together. Confirm.
5. **`flexlayout-react` dep removal.** Plan keeps the dep through PR A for rollback safety, removes in a follow-up. Confirm vs. removing immediately in PR A (minus ~85 KB minified, but rollback requires npm install).
6. **Tile minimum size.** RGL supports `minW`/`minH` per item. Plan uses `minW: 2, minH: 2` globally (matching MetersPanel). Per-panel minimums (e.g. hero needs `minW: 6, minH: 6`) might be useful — recommend adding `defaultSpan: {w, h, minW?, minH?}` to `PanelDef` later if needed.
7. **Chrome hover-reveal X button.** Plan keeps the X always visible. Hover-to-reveal would reduce visual noise but trips on touch devices. Recommend always-visible for v1; revisit if visual review flags it.
8. **Mobile shell migration.** Out of scope for this plan, confirmed. But if the maintainer wants, a future task could swap mobile too — `react-grid-layout` works on touch devices via `react-draggable`. Flag for future planning.

---

## 11. Critical files for implementation

- `zeus-web/src/layout/FlexWorkspace.tsx` (rewritten in PR A — keep export name)
- `zeus-web/src/layout/workspace.ts` (NEW in PR A — `WorkspaceLayout` types + parser)
- `zeus-web/src/layout/defaultLayout.ts` (rewritten in PR A — RGL coords per §1)
- `zeus-web/src/state/layout-store.ts` (modified in PR A — schema bump + tile mutators)
- `zeus-web/src/layout/AddPanelModal.tsx` (rewritten in PR B — categorized layout)
- `zeus-web/src/layout/panels/MetersPanel.tsx` + `zeus-web/src/components/meters/metersConfig.ts` (modified in PR A — drop flexlayout coupling, take config as prop)

---

## 12. Plan revisions (post-team-lead override)

After the planner output, the maintainer locked these decisions, which override the corresponding planner recommendations above:

- **Single bundled PR**, not 2 PRs (overrides §8 split). Substrate swap + categorized AddPanel + dep removal all in this branch.
- **`flexlayout-react` fully removed** in this PR (overrides the "follow-up cleanup" recommendation in §6 / §10 Q5). Mobile shell does not import flexlayout-react (verified). The dep is unreferenced after the swap; clean cut now.
- **`+ Add Panel` button: top-right of the workspace** (overrides §3.4's bottom-right FAB suggestion). Matches the existing chrome conventions.
- **`rowHeight = 30` px** (overrides §2.2's `rowHeight = 48`). Finer pixel-grain placement.
- **Meters tile rename: dropped for v1** (matches §10 Q3 option c). Workspace tile chrome shows `def.name`; per-instance title editing is a follow-up.
- **No per-panel min sizes for v1** (overrides §10 Q6's `hero` minSpan suggestion). Global `minW: 2, minH: 2`. Revisit if the panadapter misbehaves at 2×2.
- **X close button always visible** (matches §10 Q7).
- **Meters category drill = adds an empty Meters tile**, operator uses the tile's own Library drawer for reading selection (matches §3.2 recommendation). No double-modal stack.

### 12.1 Addendum from planner — full flexlayout removal verification

Confirmed flexlayout-react footprint via grep — incorporating into the plan as concrete file-touch list.

| Location | What it imports | Action in this PR |
|---|---|---|
| `zeus-web/package.json` line 17 | `flexlayout-react: ^0.8.19` | **Remove** |
| `zeus-web/src/layout/FlexWorkspace.tsx` | `Actions, BorderNode, DockLocation, Layout, Model, TabSetNode, IJsonModel, ITabSetRenderValues, TabNode` + `dark.css` + `flex-layout.css` | File rewritten — all imports gone |
| `zeus-web/src/layout/panels.ts` | `import type { TabNode }` | **Remove** — `TabNode` no longer in `PanelComponentProps` |
| `zeus-web/src/layout/panels/MetersPanel.tsx` | `Actions, type TabNode` | **Remove** — `MetersPanel` switches to `{tile, onConfigChange}` props |
| `zeus-web/src/components/meters/metersConfig.ts` | `Actions, type Model, type TabNode` | **Remove** — `useMetersPanelConfig` rewritten |
| `zeus-web/src/styles/flex-layout.css` (entire file) | flexlayout-react theme overrides | **Delete** |
| `zeus-web/src/state/layout-store.ts` | comment only ("Opaque flexlayout-react JSON blob…") | Update comment |
| `zeus-web/src/App.tsx` | comments + `useFlexLayout` derived flag | Comments updated; `useFlexLayout` flag name kept (operators' saved preference uses `'flex'` mode key — minimal-diff path) |

`zeus-web/src/main.tsx` checked — no flexlayout import.

### 12.2 Verification gate before merge

Implementer must run and confirm **zero hits** on each:

```bash
cd zeus-web
grep -rn "flexlayout-react" src/
grep -rn "flexlayout-react" package.json
grep -rn "flex-layout.css" src/
test ! -f src/styles/flex-layout.css   # CSS file deleted
```

Plus a build-artefact scan after `npm run build`:

```bash
grep -r "flexlayout" zeus-web/Zeus.Server.Hosting/wwwroot/ 2>/dev/null
```

Both must be empty before push.

### 12.3 Smoke step 11 (added to §9.2)

11. **Bundle scan** — DevTools network tab on a fresh `npm run dev` start, filter for `flexlayout`. Zero hits. Run `npm --prefix zeus-web run build` and grep the build artefact for `flexlayout` — zero hits. Confirms the package is fully removed from the bundle, not just unused source.
