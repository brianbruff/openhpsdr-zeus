# Configurable Meters Panel — Implementation Plan

**Status:** draft (planner output, awaiting maintainer sign-off)
**Owner:** unassigned (split across backend + frontend implementers per PR breakdown)
**Inputs:**
- Catalog: `docs/lessons/meters-catalog.md`
- Visual reference: `docs/pics/meter_design_generic.png` (ignore the mock's neon palette)
- Maintainer constraints (see Open Questions for what is locked):
  1. v1 supports **all** Thetis RX + TX readings (Contracts wire-format change accepted).
  2. Library + Settings appear as **collapsible overlay drawers inside each Meters tile**, toggled by a gear/edit button in the panel header. Default view = just the configured widgets.
  3. Per-instance widget list + per-widget settings persist in the **FlexLayout node `config` blob** (round-trips with workspace JSON save/load). No new storage layer.
  4. **No sub-tabs** inside one Meters panel for v1 — multi-view = multi-Meters-panel-instance.

**Color discipline (CLAUDE.md / `tokens.css`):**
- `--accent` blue (#4a9eff) → nominal/level fill for non-signal readings
- `--tx` red (#e63a2b) → over-limit / clip / danger zone
- `--power` yellow (#ffc93a) → power readings (forward watts), warn zone
- amber `#FFA028` → **only** signal-strength bar fill and peak-hold ticks
- `--good` (where present) → healthy compression band
- No raw hex; never the AI mock's neon greens/teals/limes.

---

## 1. Contracts changes

### 1.1 Inventory of existing wire format (do not regress)

| Frame | MsgType | Bytes | Cadence | Carries |
|---|---|---|---|---|
| `RxMeterFrame` | 0x14 | 5 | 5 Hz (RX live) | RX signal dBm (calibrated, S_AV path) |
| `TxMetersFrame` | 0x11 | 37 | 10 Hz (MOX) | v1: FWD/REF/SWR/Mic/EQ pk/Lvlr pk/ALC pk+gr/Out pk |
| `TxMetersV2Frame` | 0x16 | 81 | 10 Hz (MOX) | **All 17 TXA stage readings** + FWD/REF/SWR |
| `PaTempFrame` | 0x17 | 5 | 2 Hz | HL2 PA temp °C |
| `PsMetersFrame` | 0x18 | 15 | 10 Hz (PS armed) | PS feedback / correctionDb / cal state |

**The catalog confirms the entire TX side is already on the wire via 0x16.** The Meters Panel can read TX from `useTxStore` today with zero backend work.

### 1.2 Gap on the RX side

Missing from the wire (catalog §RX, "Computed in server but not on wire"):
1. ADC input (peak)
2. ADC input (avg) — already read at 1 Hz for diagnostic logging in `WdspDspEngine.GetRxaSignalDbm`
3. AGC gain — already read at 1 Hz for diagnostic logging
4. AGC envelope (peak)
5. AGC envelope (avg) — already read at 1 Hz for diagnostic logging
6. ADC2_L/R + AGC2 — **out of scope for v1**: these need a second RXA channel and depend on sub-RX support which Zeus does not yet have. Document, do not implement.

### 1.3 Proposed new frame: `RxMetersV2Frame`

**MsgType assignment:** **0x19** (next free; the prompt's "after 0x16" overlooked PaTemp 0x17 and PsMeters 0x18).

**Justify additive, not keyed:** the existing `RxMeterFrame` (0x14) is intentionally tiny (5 B) and broadcasts at 5 Hz so a default UI never pays the cost of fields it doesn't render. Bolting all RX readings into 0x14 would force every connected client (including the older `SMeterLive` panel) to grow a parser. A single new bare-payload frame mirroring the `TxMetersV2Frame` pattern is consistent, decoder-additive (existing `RxMeterFrame` consumers ignore 0x19), and keeps the 5 Hz wire light. A keyed frame (`MeterReadingsFrame { readings: Map<id, float> }`) was considered — rejected because (a) the readings are a fixed schema for a known set of WDSP indices, not extensible at runtime, (b) keyed encoding adds 1–2 B per field for what would otherwise be a fixed-size 28 B struct, and (c) the existing meter pipeline is structurally `record struct → BinaryPrimitives.WriteSingleLittleEndian` — adding a keyed variant introduces a parser shape not used elsewhere.

**Layout** (bare-payload, no 16-byte `WireFormat` header — same convention as 0x11/0x14/0x16/0x17/0x18):

```
[0]      MsgType byte (0x19 = RxMetersV2)
[1..4]   SignalPk : f32 LE  (dBm, calibrated, RXA_S_PK + cal offset)
[5..8]   SignalAv : f32 LE  (dBm, calibrated, RXA_S_AV + cal offset)
[9..12]  AdcPk    : f32 LE  (dBFS, RXA_ADC_PK)
[13..16] AdcAv    : f32 LE  (dBFS, RXA_ADC_AV)
[17..20] AgcGain  : f32 LE  (dB, RXA_AGC_GAIN — sign-flipped to positive "reduction" only if AGC reducing? See §1.4)
[21..24] AgcEnvPk : f32 LE  (dBm, calibrated, RXA_AGC_PK + cal offset)
[25..28] AgcEnvAv : f32 LE  (dBm, calibrated, RXA_AGC_AV + cal offset)
```

**Total: 1 + 7×4 = 29 bytes.**

**Cadence:** 5 Hz, dispatched from the same `DspPipelineService.Tick` modulus that fires `RxMeterFrame` today (every 6th 30 Hz tick — see `DspPipelineService.cs:1084`). 0x14 is **kept in flight** (do not remove): older clients and the simple `SMeterLive` view continue to read it; 0x19 is purely additive.

**RX cal offset:** `WdspDspEngine.GetRxaSignalDbm` applies +0.98 dB (HL2/ANAN-200D default) to the signal path. The new helper that produces `RxMetersV2Frame` must apply the same offset to `Signal*` and `AgcEnv*` fields (the AGC envelope is downstream of the smeter tap in the WDSP RXA chain — see WDSP `RXA.c:645/662`). `Adc*` is dBFS (raw ADC, board-independent units) and gets **no** cal offset.

### 1.4 AGC gain sign convention

WDSP `GetRXAMeter(ch, RXA_AGC_GAIN)` returns dB gain — **positive when boosting, negative when cutting**. This is genuinely a gain reading, not a reduction reading: when AGC is off it reports 0; when AGC is boosting a weak signal it reports +30 dB; when AGC is cutting a hot signal under fast/slow attack it reports −12 dB. The frontend wants both directions, so: **leave the sign alone** and document the convention in the frame (`AgcGain` is signed dB gain, ≥ 0 means AGC is boosting). This differs from TX gain-reduction fields (`*Gr`) in `TxMetersV2Frame`, which are negated to a positive "how much are we cutting?" scale — but TX GR is only ever non-negative reduction; RX AGC genuinely swings both ways.

### 1.5 Test plan (Contracts)

Add `tests/Zeus.Contracts.Tests/RxMetersV2FrameTests.cs` mirroring `TxMetersV2FrameTests.cs`:
- `Roundtrip_Preserves_All_Fields` — assert all 7 floats survive serialize/deserialize with bit-exact equality.
- `Deserialize_RejectsWrongMsgType` (push a 0x16 first byte, expect throw).
- `Deserialize_RejectsShortBuffer` (length−1, expect throw).
- `ByteLength_Equals_29`.

---

## 2. Zeus.Server pipeline

### 2.1 New helper on `IDspEngine`

Add to `Zeus.Dsp/IDspEngine.cs`:

```csharp
/// <summary>RXA per-stage readings (signal pk/av, ADC pk/av, AGC gain,
/// AGC envelope pk/av). Returns RxStageMeters.Silent on synthetic engine
/// or when the channel is closed. Cal offset is NOT applied here — caller
/// (DspPipelineService) decides whether to apply the board offset before
/// putting the value on the wire, so unit tests can assert raw WDSP output.</summary>
RxStageMeters GetRxStageMeters(int channelId);
```

New record `Zeus.Dsp/RxStageMeters.cs` mirroring `TxStageMeters.cs`:

```csharp
public readonly record struct RxStageMeters(
    float SignalPk,   // RXA_S_PK   (idx 0) — uncalibrated dBm
    float SignalAv,   // RXA_S_AV   (idx 1) — uncalibrated dBm
    float AdcPk,      // RXA_ADC_PK (idx 2) — dBFS
    float AdcAv,      // RXA_ADC_AV (idx 3) — dBFS
    float AgcGain,    // RXA_AGC_GAIN (idx 4) — dB, signed
    float AgcEnvPk,   // RXA_AGC_PK (idx 5) — uncalibrated dBm
    float AgcEnvAv)   // RXA_AGC_AV (idx 6) — uncalibrated dBm
{
    public static readonly RxStageMeters Silent = new(
        SignalPk: -200f, SignalAv: -200f,
        AdcPk: -200f, AdcAv: -200f,
        AgcGain: 0f,
        AgcEnvPk: -200f, AgcEnvAv: -200f);
}
```

### 2.2 WDSP implementation

In `Zeus.Dsp/Wdsp/WdspDspEngine.cs`, replace the diagnostic-only reads in `GetRxaSignalDbm` (lines 880–890) with a single helper that fetches all 7 indices in one pass and publishes a snapshot under a new lock — same pattern as the existing TX path (`_latestTxStageMeters` / `_txMeterPublishLock`):

```csharp
private readonly object _rxMeterPublishLock = new();
private RxStageMeters _latestRxStageMeters = RxStageMeters.Silent;

public RxStageMeters GetRxStageMeters(int channelId)
{
    if (!_channels.ContainsKey(channelId)) return RxStageMeters.Silent;
    var snap = new RxStageMeters(
        SignalPk: (float)NativeMethods.GetRXAMeter(channelId, 0),
        SignalAv: (float)NativeMethods.GetRXAMeter(channelId, 1),
        AdcPk:    (float)NativeMethods.GetRXAMeter(channelId, 2),
        AdcAv:    (float)NativeMethods.GetRXAMeter(channelId, 3),
        AgcGain:  (float)NativeMethods.GetRXAMeter(channelId, 4),
        AgcEnvPk: (float)NativeMethods.GetRXAMeter(channelId, 5),
        AgcEnvAv: (float)NativeMethods.GetRXAMeter(channelId, 6));
    lock (_rxMeterPublishLock) { _latestRxStageMeters = snap; }
    return snap;
}
```

`SyntheticDspEngine.GetRxStageMeters` returns `RxStageMeters.Silent`.

**WDSP-init order:** No new init dependency. The 7 indices are all on the existing primary RXA channel; per `docs/lessons/wdsp-init-gotchas.md` the channel must already be at `state=1` (post-`SetChannelState(id, 1, 0)`) before any meter call returns non-sentinel. The existing `OpenChannel` → `ApplyStateToNewChannel` → IQ-pump start sequence already satisfies this; no reorder needed.

### 2.3 Broadcast wiring

In `Zeus.Server.Hosting/DspPipelineService.cs::Tick` (around line 1084 where `RxMeterFrame` is built):

1. **Keep** the existing `RxMeterFrame` broadcast (older clients still read 0x14 via `SMeterLive`).
2. After it, fetch the full snapshot once:
   ```csharp
   var rx = engine.GetRxStageMeters(channel);
   const double calOffsetDb = 0.98; // HL2/ANAN-200D; G2 = -4.48; ANAN-7000/8000 = +4.84
   //
   // NOTE: per CLAUDE.md "use per-board abstractions". The cal offset is
   // currently hard-coded inside WdspDspEngine.GetRxaSignalDbm as
   // Hl2MeterCalOffsetDb = 0.98. Extract a single source of truth — e.g.
   // a new static `RadioMeterCalibration.RxOffsetDb(HpsdrBoardKind)` —
   // before duplicating it here. Out-of-scope for this PR if it widens
   // the diff; track in a follow-up if so.
   //
   _hub.Broadcast(new RxMetersV2Frame(
       SignalPk: rx.SignalPk + (float)calOffsetDb,
       SignalAv: rx.SignalAv + (float)calOffsetDb,
       AdcPk: rx.AdcPk,
       AdcAv: rx.AdcAv,
       AgcGain: rx.AgcGain,
       AgcEnvPk: rx.AgcEnvPk + (float)calOffsetDb,
       AgcEnvAv: rx.AgcEnvAv + (float)calOffsetDb));
   ```
3. Add `Broadcast(in RxMetersV2Frame frame)` to `Zeus.Server.Hosting/StreamingHub.cs` — copy/paste of the existing `Broadcast(RxMeterFrame)` overload at line 243, swapping types and `ByteLength`.

### 2.4 Threading + lifecycle

- `GetRxStageMeters` is called from the same pipeline tick thread as `GetRxaSignalDbm` today; no new thread, no new mutex contention.
- Sentinel handling: when `GetRXAMeter` returns ≤ −399 (the "meter didn't run" sentinel — see `wdsp-init-gotchas.md`), the value passes through unchanged into `RxMetersV2Frame`. The frontend already has the `<= -200` "bypassed" convention from TX (see `TxStageMeters.tsx:isBypassed`); reuse that to render an em-dash for stages whose underlying WDSP path hasn't started.
- Cal offset: applied **only** to dBm-scale fields (Signal/AgcEnv); ADC and AGC-gain are board-independent and get the raw value.

### 2.5 Test plan (server)

- Extend `tests/Zeus.Dsp.Tests/WdspDspEngineTests.cs::GetRXAMeter_SAv_EscapesSentinel_AfterIqFlows_WithTxChannelAndProductionState` to also call `GetRxStageMeters` and assert all 7 fields escape the −400 sentinel after IQ has flowed through.
- Add `tests/Zeus.Server.Tests/RxMetersBroadcastTests.cs` — boot a `DspPipelineService` with a stub engine that returns canned `RxStageMeters`, run one tick, assert `StreamingHub` received an `RxMetersV2Frame` with cal offset applied to Signal/AgcEnv but not to ADC/AgcGain.
- Manual on **HL2**: connect, watch the dev-tools WS frames pane; confirm a 29-byte 0x19 frame fires every 200 ms, that `SignalPk`/`SignalAv` ≈ existing `RxMeterFrame.RxDbm` ± 0.5 dB, that `AdcPk`/`AdcAv` move when an antenna is plugged/unplugged, and that `AgcGain` swings positive on weak-signal SSB and toward zero on a strong carrier.
- Cross-board sanity: HL2 only for the v1 PR. Other boards' cal offsets are documented but not exercised; flag in PR description that ANAN/G2 cal review is a follow-up.

---

## 3. Frontend types, persistence, multi-instance

### 3.1 New shared types

Create `zeus-web/src/components/meters/meterCatalog.ts`:

```typescript
// One ID per Thetis-supported reading, RX + TX. The catalog table here is
// the single source of truth that the Library drawer reads to populate the
// "available meters" list AND that the per-widget renderer uses to find the
// live store-selector.
export enum MeterReadingId {
  // RX (RxMetersV2Frame 0x19 + RxMeterFrame 0x14)
  RxSignalPk = 'rx.signal.pk',
  RxSignalAv = 'rx.signal.av',
  RxAdcPk    = 'rx.adc.pk',
  RxAdcAv    = 'rx.adc.av',
  RxAgcGain  = 'rx.agc.gain',
  RxAgcEnvPk = 'rx.agc.env.pk',
  RxAgcEnvAv = 'rx.agc.env.av',
  // TX (TxMetersV2Frame 0x16, already on wire)
  TxFwdWatts = 'tx.fwd.watts',
  TxRefWatts = 'tx.ref.watts',
  TxSwr      = 'tx.swr',
  TxMicPk    = 'tx.mic.pk',
  TxMicAv    = 'tx.mic.av',
  TxEqPk     = 'tx.eq.pk',
  TxEqAv     = 'tx.eq.av',
  TxLvlrPk   = 'tx.lvlr.pk',
  TxLvlrAv   = 'tx.lvlr.av',
  TxLvlrGr   = 'tx.lvlr.gr',
  TxCfcPk    = 'tx.cfc.pk',
  TxCfcAv    = 'tx.cfc.av',
  TxCfcGr    = 'tx.cfc.gr',
  TxCompPk   = 'tx.comp.pk',
  TxCompAv   = 'tx.comp.av',
  TxAlcPk    = 'tx.alc.pk',
  TxAlcAv    = 'tx.alc.av',
  TxAlcGr    = 'tx.alc.gr',
  TxOutPk    = 'tx.out.pk',
  TxOutAv    = 'tx.out.av',
}

export type MeterUnit = 'dBm' | 'dBFS' | 'dB' | 'W' | 'ratio';
export type MeterCategory = 'rx-signal' | 'rx-adc' | 'rx-agc' | 'tx-power' | 'tx-stage' | 'tx-protection';

export interface MeterReadingDef {
  id: MeterReadingId;
  label: string;          // "Signal (Pk)"
  short: string;          // "S Pk" — for compact widgets
  category: MeterCategory;
  unit: MeterUnit;
  /** Default axis range for non-signal widgets. Signal-strength widgets
   *  use the SMeter's S-unit scale instead. */
  defaultMin: number;
  defaultMax: number;
  /** Where in the danger zone the bar turns --tx red. null = no danger zone. */
  dangerAt?: number;
  /** Color-token override — the widget defaults to --accent unless this
   *  reading explicitly belongs to amber-signal/yellow-power/red-tx. */
  colorToken?: 'amber-signal' | 'power' | 'tx' | 'accent';
}
```

A constant `METER_CATALOG: Record<MeterReadingId, MeterReadingDef>` lists all 27 entries. RX-signal entries get `colorToken: 'amber-signal'`; `TxFwdWatts` gets `'power'`; `TxSwr` and `Tx*Gr` get `'tx'` once over the danger threshold; everything else falls through to `'accent'`.

### 3.2 Subscription helpers

Create `zeus-web/src/components/meters/useMeterReading.ts`:

```typescript
// One hook to map a MeterReadingId to its live numeric value, reading from
// the existing tx-store (TX + the 0x14 RX dBm) and a new rx-meters-store
// for the 0x19 fields. Returns NaN until the first frame lands.
export function useMeterReading(id: MeterReadingId): number { ... }
```

This hook is the **only** seam widgets use to read data. Adding a future reading is one row in the catalog + one branch in this hook. The hook's selector closure is keyed off `id` so React-Zustand re-renders only when that field mutates.

### 3.3 RX-meters store

Create `zeus-web/src/state/rx-meters-store.ts` (Zustand, transient — not persisted; matches the tx-store treatment of meter telemetry):

```typescript
export interface RxMetersState {
  signalPk: number; signalAv: number;
  adcPk: number; adcAv: number;
  agcGain: number;
  agcEnvPk: number; agcEnvAv: number;
  setMeters: (m: Omit<RxMetersState, 'setMeters'>) => void;
}
```

In `zeus-web/src/realtime/ws-client.ts`, register `MSG_TYPE_RX_METERS_V2 = 0x19` (29 bytes), decode 7 floats, push into `useRxMetersStore`. The existing 0x14 path stays as-is.

### 3.4 Per-instance config in `TabNode.config`

flexlayout-react accepts an arbitrary JSON `config` field on every tab node. It's preserved by `Model.toJson()` / `Model.fromJson()`, so it round-trips through `useLayoutStore` → `/api/ui/layout` PUT → server `LayoutStore` → next session — **without any new storage layer**. We mutate it via `Actions.updateNodeAttributes(nodeId, { config: nextConfig })`, which fires `onModelChange` in `FlexWorkspace.tsx` (line 213), which writes the layout JSON back to the store.

**Per-instance config schema** (`zeus-web/src/components/meters/metersConfig.ts`):

```typescript
export interface WidgetSettings {
  /** Optional axis-min override (defaults to METER_CATALOG[id].defaultMin). */
  min?: number;
  /** Optional axis-max override. */
  max?: number;
  /** Show the peak-hold tick. Defaults to true for level meters. */
  peakHold?: boolean;
  /** Operator-friendly label override. Defaults to METER_CATALOG[id].label. */
  label?: string;
}

export interface MetersWidgetInstance {
  /** Stable per-widget id so React keys + drag re-orders survive re-renders. */
  uid: string;
  /** What to read. */
  reading: MeterReadingId;
  /** How to render. */
  kind: 'hbar' | 'vbar' | 'dial' | 'sparkline' | 'digital';
  /** Per-widget overrides; merged on top of catalog defaults at render time. */
  settings: WidgetSettings;
}

export interface MetersPanelConfig {
  schemaVersion: 1;
  widgets: MetersWidgetInstance[];
  /** Operator-named instance — shown in the FlexLayout tab strip. Optional;
   *  falls back to "Meters" until renamed. */
  title?: string;
}

export const EMPTY_METERS_CONFIG: MetersPanelConfig = {
  schemaVersion: 1,
  widgets: [],
};
```

A helper `useMetersPanelConfig(node: TabNode)` reads `node.getConfig() as MetersPanelConfig | undefined ?? EMPTY_METERS_CONFIG` and exposes a setter that calls `Actions.updateNodeAttributes(node.getId(), { config: nextConfig })` on the parent `Model`. Because flexlayout-react fires `onModelChange` on attribute updates, the existing `useLayoutStore.setLayout` debounced PUT picks up the change automatically.

`schemaVersion: 1` lets a future migration shim drop unknown-version blobs gracefully without overwriting operator config.

### 3.5 Multi-instance support in PANELS / AddPanelModal

**Today's blocker:** `AddPanelModal.tsx:71` — `if (existingPanels.has(panel.id)) return false;`. Hard duplicate-block.

**Proposal — narrow the change:**

Add a single optional `multiInstance: true` flag on `PanelDef` (`zeus-web/src/layout/panels.ts`). Default false (no behavior change for existing panels). Only `meters` opts in.

`AddPanelModal.tsx`:
- Replace the duplicate-block with: `if (existingPanels.has(panel.id) && !panel.multiInstance) return false;`
- The Add Panel grid renders multi-instance panels with a "+ Add another" hint instead of greying them out.

`FlexWorkspace.tsx::addPanel`:
- For multi-instance panels, the `Actions.addNode` call sets `component: \`meters-${crypto.randomUUID()}\`` (rather than the bare `'meters'` panel id).
- The factory dispatcher (`FlexWorkspace.tsx::factory`) is updated to: `const panelId = component.startsWith('meters-') ? 'meters' : component; const panel = PANELS[panelId];`
- `getExistingPanels` strips the suffix the same way before checking the multi-instance flag in the modal.

**Why this scheme:**
- The `component` string in flexlayout JSON is what survives save/load. By giving each instance a unique component string, FlexLayout's tabset model treats them as distinct nodes (you can drag, close, and re-add them independently). The `node.getId()` is the stable handle — but it's auto-assigned by flexlayout and the operator never sees it; the unique `component` is what we author and recognise.
- `crypto.randomUUID()` is ~36 chars; not free in JSON size, but layout JSON is round-tripped once per layout-mutation, not per frame.
- The same recipe could be unblocked for any other future multi-instance panel without further refactor.

### 3.6 `MetersPanel` shell & overlay drawers

`zeus-web/src/layout/panels/MetersPanel.tsx`:

```
┌────────────────────────────────────────────────────────────────┐
│ ⚙  ◀  [ Title (from config.title or "Meters") ]            ▼ │  ← panel header (in tab body, NOT in tabset strip — flexlayout owns the strip; we add a 24 px tall header inside the tab content)
├────────────────────────────────────────────────────────────────┤
│ ┌──────────┐                                                  │
│ │ LIBRARY  │   ← left overlay drawer (when expanded), absolute, 240 px wide
│ │ search   │      slides in from left edge with translate3d, semi-transparent backdrop
│ │ ─────    │      categories: All · RX · TX · Power · Stage · AGC
│ │ □ S Pk  │      checkbox per catalog reading; clicking adds a default-shape
│ │ □ S Av  │      widget (HBar) to config.widgets and closes the drawer.
│ │ ...      │
│ └──────────┘
│
│         WIDGET CANVAS — vertical flex stack of <MeterWidget>s,
│         each rendering its config-driven primitive. Reorderable
│         via grip-handle drag (PR 3 stretch goal — v1 ships add/remove
│         only). Empty state: "No meters yet — tap ⚙ to configure."
│
│                                            ┌──────────┐
│                                            │ SETTINGS │  ← right overlay drawer
│                                            │ Selected │     (visible when a widget is "selected" by clicking its body)
│                                            │ widget:  │     - kind toggle (HBar / VBar / Dial / Sparkline / Digital)
│                                            │ S Pk     │     - axis min/max
│                                            │ Kind …   │     - peak-hold toggle
│                                            │ Min/Max  │     - label override
│                                            │ Peak hold│     - "Remove widget" red-text button
│                                            └──────────┘
└────────────────────────────────────────────────────────────────┘
```

**Mechanics:**
- Header is a 24 px `.panel-header` strip inside the tab body (so it scrolls with the content if the body grows tall, and so the tab strip stays standard FlexLayout chrome).
- Header buttons (left to right): `⚙` (toggles Library drawer), `◀`/`▶` (collapse the open drawer), title text (double-click to rename → updates `config.title` and flexlayout tab name via `Actions.renameTab`).
- Drawers are absolutely positioned over the widget canvas — they do not push content. CSS transitions on `transform: translateX(...)` so the open/close motion respects the existing 120/240ms `--dur-fast/--dur-med` tokens.
- Default state: both drawers closed; only the configured widgets show.
- A widget enters "selected" state on click, which opens the right Settings drawer; clicking elsewhere or the Settings X closes it.
- All chrome uses `tokens.css` variables — never raw hex. The drawer backdrop is `var(--bg-1)` with the existing `--panel-shadow`.

### 3.7 Registration in PANELS

```typescript
// panels.ts
meters: {
  id: 'meters',
  name: 'Meters',
  category: 'meters',
  tags: ['meters', 'rx', 'tx', 'signal', 'power', 'agc', 'alc', 'configurable'],
  component: MetersPanel,
  multiInstance: true,
},
```

Existing `smeter` and `txmeters` entries **stay** for v1 — they're still useful as one-shot quick views and removing them is a red-light UX change. A future PR can deprecate them once the maintainer has lived with the new panel.

---

## 4. Widget components

Five reusable visualisations, each a pure presentation component that takes `value: number`, `def: MeterReadingDef`, `settings: WidgetSettings`. None of them know about WDSP or stores; the wrapping `MeterWidget` does the `useMeterReading(reading)` lookup and merges defaults.

### 4.1 `<HBarMeter>` (default)

- Horizontal bar — the existing `LevelRow` pattern in `TxStageMeters.tsx` is the visual reference.
- Fill colour by category:
  - `rx-signal` → amber gradient (existing `SMeter.tsx` recipe at lines 184–196).
  - `tx-power` → `var(--power)` yellow; turn `var(--tx)` red past `def.dangerAt`.
  - `tx-stage` (level dBFS) → `var(--accent)` blue baseline; `var(--power)` in warn band; `var(--tx)` past clip — reuse `levelFillColor` from `TxStageMeters.tsx`.
  - `tx-stage` (`*Gr` reduction) → existing GR zone palette.
  - `rx-agc`/`rx-adc` → `var(--accent)` blue.
- Peak-hold tick = amber `#FFA028` @ 0.4 alpha (per CLAUDE.md, signal-strength + peak-tick only).
- **Props:** `{ value, def, settings, height? = 12 }`.

### 4.2 `<VBarMeter>`

- Vertical bar (e.g. for stacking three Mic/EQ/ALC bars side-by-side at narrow tile widths).
- Same colour rules as HBar; peak-hold rendered as a horizontal tick across the bar at peak position.

### 4.3 `<DialMeter>`

- Round analog-style dial (aligned with the HL2-front-panel aesthetic, **not** the AI mock's neon ring). Reference: `zeus-web/src/components/design/Meter.tsx` extended with an SVG arc.
- Needle colour `var(--fg-0)`; arc background dark; danger sector `var(--tx)` painted as a translucent overlay past `def.dangerAt`.
- Useful for: SWR (1.0..3.0 with red past 2.0), forward power (0..ratedW yellow ramp), AGC gain (−40..+60 dB centred at 0).

### 4.4 `<SparklineMeter>`

- 60–120 sample rolling buffer (60 samples @ 5 Hz = 12 s window; @ 10 Hz = 6 s).
- Drawn on a `<canvas>` (SVG path is fine for v1 if perf isn't a concern; canvas if profile-positive).
- Stroke colour follows category; no fill, just a line, with a faint gradient under it for depth.
- Useful for: AGC envelope drift, drive-percent vs ALC behaviour over a recent QSO.

### 4.5 `<DigitalMeter>`

- Big numeric readout with unit suffix. Mirrors the existing top-bar chips' typography (`Archivo Narrow`, tabular-nums).
- Optional colour cue when value crosses `def.dangerAt`.
- Useful for: SWR, ALC gain reduction, PA temp.

### 4.6 Color-token mapping (recap, for reviewer)

| Category / context | Token |
|---|---|
| RX signal-strength bar fill | amber `#FFA028` (signal-only) |
| Peak-hold ticks | amber `#FFA028` @ 0.4 alpha |
| TX forward power (nominal) | `var(--power)` yellow |
| TX forward power (over rated) | `var(--tx)` red |
| TX stage level (nominal) | `var(--accent)` blue |
| TX stage level (warn, e.g. ≥ −6 dBFS) | `var(--power)` yellow |
| TX stage level (clip ≥ 0 dBFS) | `var(--tx)` red |
| TX `*Gr` (gain reduction) | `var(--good)` healthy band, `var(--power)` quiet, `var(--tx)` overdrive |
| ADC, AGC gain, AGC envelope (RX non-signal) | `var(--accent)` blue |
| SWR (≥ 2.0) | `var(--tx)` red, with `var(--power)` warn between 1.5 and 2.0 |
| Dial chrome | `var(--bg-0)` background, `var(--panel-border)` rim |

No raw hex anywhere except the documented amber `#FFA028` (already a project-wide constant — see `gl/panadapter.ts` `TRACE_R/G/B`).

---

## 5. Test plan

### 5.1 Unit / contract tests

- `tests/Zeus.Contracts.Tests/RxMetersV2FrameTests.cs` — roundtrip, wrong-msg-type, short-buffer, byte-length (29).
- `tests/Zeus.Server.Tests/RxMetersBroadcastTests.cs` — stub engine, assert StreamingHub receives 0x19 each tick with cal offset applied to dBm fields only.
- Frontend Vitest (in `zeus-web/src/components/meters/__tests__/`):
  - `meterCatalog.test.ts` — every `MeterReadingId` has a `METER_CATALOG` entry; categories reasonable.
  - `metersConfig.test.ts` — `MetersPanelConfig` JSON serializes + parses unchanged through 5+ round trips (simulates layout JSON storage).
  - `useMeterReading.test.ts` — hook returns expected store value for one RX and one TX reading; returns `NaN` before any frame.
  - `MetersPanel.test.tsx` — gear toggles Library drawer; selecting a catalog entry inserts a widget into config; remove-widget-via-Settings deletes it; renders zero widgets in empty state.

### 5.2 Manual UI checks (HL2 rack)

1. Connect to HL2; add a Meters tile; open Library; pick "RX Signal (Pk)" → HBar; verify amber bar tracks band noise.
2. Add a second Meters tile (Add Panel modal must not block the duplicate); each tile holds an independent widget set.
3. Add SWR (Digital), Forward Power (Dial 0..5W), ALC Gain Reduction (HBar). Key TX with a tone — verify SWR shows ~1.0, FwdW reads 1–4 W, ALC GR reads 0–10 dB band.
4. Drag a Meters tile to a different column; close + reopen browser → tile + widgets restored from server-persisted layout JSON.
5. Reset layout (DisplayPanel button) → Meters tiles disappear (default layout doesn't include them); operator can re-add freely.
6. Confirm the `RxMetersV2Frame` (0x19) appears in the WS frames pane every 200 ms with the right byte length (29).
7. Color discipline scan — open the panel screenshot in the Pull Request and visually confirm: no neon teal/lime/cyan fills; amber only on signal-strength widgets; red only when SWR ≥ 2 or ALC GR > 10 dB or TX stage clips.

### 5.3 Risk surfaces flagged for the maintainer

- **HL2 only.** All cal-offset and AGC behaviour is exercised on HL2. **PR description must explicitly state** that ANAN/G2/Orion cal offsets are unverified and recommend a one-radio-at-a-time confirmation pass before broadcasting publicly. This sits squarely in the CLAUDE.md "drive/PA changes need HL2 sanity-check" warning surface, even though meters don't touch the TX path.
- **No defaults shift.** Existing `SMeterPanel` and `TxMetersPanel` keep their visual language exactly. The new Meters tile is opt-in — operator must explicitly add it. Therefore this is **not** a "default an operator will feel" red-light change.
- **Panel JSON growth.** Each Meters tile adds ~200 B per widget × N widgets to the layout JSON. With a dozen widgets across two tiles that's ~5 KB. Well within `LayoutStore` PUT body limits, but flag it in the PR.

---

## 6. PR breakdown (recommended)

Three PRs, sequenced for parallelism — backend can start PR 1 the moment this plan is approved; frontend can start PR 2 in parallel using the existing 0x14/0x16 wire format and add 0x19 consumption in PR 3.

### PR 1 — Backend: RX meter telemetry parity (additive 0x19 frame)

**Owner:** backend implementer.
**Scope:**
- New `Zeus.Contracts/RxMetersV2Frame.cs` (record struct, Serialize/Deserialize).
- New `MsgType.RxMetersV2 = 0x19` enum value.
- New `Zeus.Dsp/RxStageMeters.cs` (record struct + `Silent`).
- New `IDspEngine.GetRxStageMeters(int channelId)`.
- WDSP impl in `WdspDspEngine.cs` (one new helper, snapshot publish under lock — same pattern as TX).
- Synthetic impl returns `RxStageMeters.Silent`.
- New `StreamingHub.Broadcast(in RxMetersV2Frame)` overload.
- `DspPipelineService.Tick` broadcasts 0x19 alongside the existing 0x14 (do not remove 0x14).
- Tests: `RxMetersV2FrameTests`, `RxMetersBroadcastTests`, extension to `WdspDspEngineTests`.
**Inputs:** This plan + catalog.
**Outputs:** `dotnet build` clean, `dotnet test` green, manual: WS frame pane shows 29-byte 0x19 every 200 ms on HL2.
**Acceptance:** all tests pass; HL2 manual smoke confirms ADC/AGC values are non-sentinel within 2 s of channel open and move with antenna activity.
**Estimate:** ~200 LOC across 8 files (mostly mechanical mirror of TX path).

### PR 2 — Frontend: MetersPanel shell + multi-instance + persistence (uses existing 0x14 + 0x16 only)

**Owner:** frontend implementer A.
**Scope:**
- `panels.ts` adds `multiInstance?: boolean` field on `PanelDef`; new `meters` entry with the flag; existing entries unchanged.
- `AddPanelModal.tsx` — relax duplicate filter for `multiInstance: true`.
- `FlexWorkspace.tsx` — unique-id minting via `crypto.randomUUID()` for multi-instance panels; factory dispatcher recognises the `meters-<uuid>` prefix.
- `zeus-web/src/components/meters/metersConfig.ts` — types + `EMPTY_METERS_CONFIG` + `useMetersPanelConfig(tabNode)` helper.
- `zeus-web/src/components/meters/meterCatalog.ts` — `MeterReadingId` enum + `METER_CATALOG` table + categories.
- `zeus-web/src/components/meters/useMeterReading.ts` — selector hook (only TX entries + the existing 0x14 RX dBm wired in this PR; new 0x19 fields throw "not yet on wire" if PR 1 hasn't landed).
- `zeus-web/src/layout/panels/MetersPanel.tsx` — header, drawer chrome, widget canvas.
- One widget shipped: `<HBarMeter>`.
- Library drawer can add HBar widgets for the readings whose hook is currently wired (TX + RX signal pk via 0x14).
- Settings drawer can change axis range / peak-hold / label / remove widget.
- Vitest: `MetersPanel.test.tsx`, `metersConfig.test.ts`, `useMeterReading.test.ts`.
**Inputs:** Plan §3, §4.1.
**Outputs:** `npm --prefix zeus-web run lint && npm --prefix zeus-web run test` green; manual: gear button toggles Library; widget add/remove works; layout JSON in `/api/ui/layout` PUT carries `widgets: [...]` blob; reload restores.
**Acceptance:** Two Meters tiles can coexist with different widget sets; one survives full browser restart with the other still independently restored.
**Estimate:** ~700 LOC; ~50% in the `MetersPanel.tsx` shell, ~30% in widget + hook plumbing, ~20% in tests.

### PR 3 — Frontend: Widget library + RX-meters-store (consumes PR 1's 0x19)

**Owner:** frontend implementer B (can start once PR 1 is merged).
**Scope:**
- `MSG_TYPE_RX_METERS_V2 = 0x19` parser in `ws-client.ts`; `useRxMetersStore` writes 7 fields.
- `useMeterReading.ts` extended — every catalog entry resolves to a real selector.
- Four additional widget components: `<VBarMeter>`, `<DialMeter>`, `<SparklineMeter>`, `<DigitalMeter>`.
- Settings drawer kind-toggle wired to all 5 widget kinds.
- Library drawer fully populated from `METER_CATALOG`; category filter chips at top of drawer (All · RX · TX · Power · Stage · AGC).
- Per-widget defaults: signal readings default to amber HBar; SWR defaults to Digital; FwdW defaults to Dial 0..ratedW.
- Vitest for each widget component + RX store roundtrip test.
**Inputs:** PR 1 merged; PR 2 merged (or sequenced same release).
**Outputs:** All 27 catalog readings can be added as live widgets; HL2 smoke shows AGC envelope sparkline tracking band conditions.
**Acceptance:** Maintainer screenshot review of the assembled panel against `meter_design_generic.png` (composition, not palette); color-discipline visual scan passes.
**Estimate:** ~900 LOC including widgets + tests.

### Optional follow-up (not in v1, flagged for maintainer queue)

- **PR 4 (red-light, maintainer review):** deprecate `SMeterPanel` and `TxMetersPanel` from `PANELS` once operators have lived with the new tile. Pure UX change — not for this batch.
- **Sub-RX support → ADC2 / AGC2 readings.** Blocked on a separate sub-RX feature.
- **Per-board cal-offset abstraction:** extract `Hl2MeterCalOffsetDb` from `WdspDspEngine.cs` into a `RadioMeterCalibration.RxOffsetDb(HpsdrBoardKind)` lookup so PR 1's broadcast helper, the existing `GetRxaSignalDbm`, and any future per-board path read from one source.

---

## 7. Open questions / decisions for the maintainer

1. **MsgType byte:** confirm 0x19 is acceptable. Alternative: bump `RxMeterFrame` (0x14) in-place from 5 B → 29 B and add v2 parsing — rejected here because it forces every existing decoder to grow, but the maintainer may prefer it for "fewer wire types".
2. **AGC gain sign convention:** plan keeps WDSP's signed dB (positive = boosting). Thetis displays "AGC Gain" with this sign convention. Confirm we should not flip to a "reduction" scale for UI.
3. **Per-board cal offset extraction:** either bake the duplicate `0.98` into `DspPipelineService.Tick` for now (one-line, fast) and file an extraction follow-up, or extract first as part of PR 1 (slightly larger diff, no functional change). Planner recommends "follow-up" to keep PR 1 small.
4. **Existing `SMeterPanel` / `TxMetersPanel` lifetime:** plan keeps both registered in v1 (no UX surprise). Confirm the maintainer wants both visible in Add Panel during the transition.
5. **Drawer mechanics:** the plan uses **overlay drawers** (absolute, do not push content) to maximise widget canvas in narrow tiles. Confirm vs side-pushing splitters (which would shrink the widget area while a drawer is open).
6. **Widget reorder:** plan ships add/remove only in v1. Drag-reorder of widgets within a tile is a stretch goal for PR 3 if time allows; otherwise a small follow-up.
7. **Schema versioning:** `MetersPanelConfig.schemaVersion: 1` is in place. Any v2 migration policy preferences? Default: drop unknown-version blobs and reset to `EMPTY_METERS_CONFIG`.
8. **Renaming a Meters tile:** plan supports double-click-on-title to rename, mirrored to the FlexLayout tab name via `Actions.renameTab`. Confirm this UX (vs. only renaming the body title).

---

## 8. Critical files for implementation

- `Zeus.Contracts/MsgType.cs` and a new `Zeus.Contracts/RxMetersV2Frame.cs` (PR 1)
- `Zeus.Dsp/Wdsp/WdspDspEngine.cs` and a new `Zeus.Dsp/RxStageMeters.cs` (PR 1)
- `Zeus.Server.Hosting/DspPipelineService.cs` and `Zeus.Server.Hosting/StreamingHub.cs` (PR 1)
- `zeus-web/src/layout/panels.ts`, `zeus-web/src/layout/AddPanelModal.tsx`, `zeus-web/src/layout/FlexWorkspace.tsx` (PR 2)
- `zeus-web/src/layout/panels/MetersPanel.tsx` (new, PR 2) plus `zeus-web/src/components/meters/{meterCatalog,metersConfig,useMeterReading}.ts` (PR 2/3) and `zeus-web/src/realtime/ws-client.ts` (PR 3)
