# Multi-Panadapter Support — Design Proposal

## Implementation Status (2026-05-06)

> **Phase 1 has shipped on the `claude/support-multiple-panadapters` branch.**
> The doc below was written before the work landed; status flags here reflect
> what's actually in the worktree.

### ✅ Phase 1 — In this PR

Shipped on HL2 (Protocol 1, the one board with hardware multi-DDC support
and an authoritative on-the-wire spec we can lean on). The 0x0A Saturn-class
boards advertise 8 DDCs in the new-protocol path, but Phase 1 stays Protocol-1
only — Protocol-2 multi-DDC is Phase 2.

- `BoardCapabilities.MaxReceivers` field + per-board values
  (`Zeus.Contracts/BoardCapabilities.cs`, `BoardCapabilitiesTable.cs`).
  Surfaced via `/api/radio/capabilities`. Frontend reads it once at connect.
- HL2 wire layer for nddc 1..4 (`Zeus.Protocol1/Protocol1Client.cs`,
  `PacketParser.Hl2MultiRx*`). EP6 IQ slots laid out as `(6N+2)` bytes
  per slot when nddc=N; gateware bits at C0=0x00 / C4 [5:3] = `(nddc - 1)`
  per `docs/references/protocol-1/hermes-lite2-protocol.md:478-485`.
- `DspPipelineService` opens / configures / tears down per-slice WDSP RXA
  channels using the `state=0 → configure → SetChannelState(id, 1, 0)`
  sequence already pinned in `docs/lessons/wdsp-init-gotchas.md`.
- Per-RxId `DisplayFrame` routing (the `RxId` field that's been on the wire
  but pinned to 0 since v0.1).
- `POST /api/multi-slice` to enable / disable / size the multi-RX configuration.
- `POST /api/vfo` with optional `RxId` for per-slice tuning. `RxId=0` is
  bit-identical to the pre-Phase-1 path.
- Frontend per-RxId display store (`zeus-web/src/state/display-store.ts`),
  `hero-rx1` / `hero-rx2` / `hero-rx3` panel definitions in the Add Panel
  modal, and a multi-slice control in the Radio Selector (HL2 only).
- **PS-precedence rule.** When PureSignal is armed, `RadioService.SetMultiSlice`
  refuses to enable multi-RX, logs a warning, and returns `Enabled=false`
  on the response. PS+multi-RX coexistence is Phase 2; Phase 1 deliberately
  doesn't try to thread that needle.
- Single-slice path is bit-identical to v0.6.x — no change for operators
  who don't toggle multi-slice on.

### ⏸ Phase 2 — Deferred

Explicitly out of scope for this PR; the maintainer's stance is that v1
ships shared-state, audio-on-RX0-only, and HL2-only. Phase 2 candidates:

- Per-slice DSP / AGC / mode / filter state (today every slice inherits the
  primary's settings).
- Audio mixing for non-primary slices (RxId=0 audio only in Phase 1).
- PureSignal + multi-RX coexistence — the 4-DDC HL2 layout with 2 user RX +
  2 PS feedback DDCs. The wire layer can already carry it; the policy gate
  in `RadioService.SetMultiSlice` is the seam where this would unlock.
- Per-slice VFO optimistic UI on the FreqAxis dial marker (today the marker
  follows the master VFO; the per-slice VFO is correctly applied on the wire
  but the dial visualisation lags one update).
- Protocol-2 multi-DDC for Saturn-class boards (G2 / G2-MkII / 7000DLE /
  8000DLE / Red Pitaya / ANVELINA-PRO3). The capability matrix already
  carries `MaxReceivers=8` for these boards; the P2 client and the layout
  selector still gate to single-slice.

**Issue:** #[TBD]
**Status:** Phase 1 shipped; Phase 2 deferred
**Author:** Agent implementation based on issue request

## Goals

1. **Enable multi-slice operation** on capable Protocol 2 radios
2. **Preserve backward compatibility** — default single-panadapter behavior unchanged
3. **Minimal wire-format changes** — reuse existing `DisplayFrame.RxId` field
4. **Flexible UI** — allow operators to add/remove panadapters via flex layout
5. **Per-board capability awareness** — respect `NumReceivers` from discovery

## Non-Goals

- Independent TX on non-primary slices (TX remains tied to RxId=0)
- Per-slice AGC/DSP/NR state (all slices share radio state initially; can be extended later)
- Slice synchronization/lock features (advanced feature for future consideration)
- ~~Protocol 1 multi-slice support~~ — *originally listed here; HL2 turned out
  to be Protocol 1 with documented multi-DDC support, so Phase 1 ships P1
  multi-RX on HL2. P2 multi-DDC (Saturn-class boards) is now the deferred item.*

## Architecture Changes

### 1. Backend: DspPipelineService Refactor

**Current:** Single `_channelId` int, one DisplayFrame per tick
**Proposed:** Array of `SliceContext` structs, one DisplayFrame per active slice

```csharp
private struct SliceContext
{
    public int ChannelId;      // WDSP RXA channel
    public byte RxId;          // Wire RxId (0-based)
    public long VfoHz;         // Per-slice VFO
    public bool Active;        // Slice enabled
}

private SliceContext[] _slices = Array.Empty<SliceContext>();
```

**Tick() changes:**
- Loop over `_slices.Where(s => s.Active)`
- Call `engine.TryGetDisplayPixels(slice.ChannelId, ...)` per slice
- Broadcast `DisplayFrame` with correct `RxId` per slice
- **RxId=0 remains primary** (inherits main VFO, drives TX analyzer on MOX)

### 2. Backend: Multi-Slice Configuration

Add `MultiSliceConfig` to `StateDto`:

```csharp
public record MultiSliceConfig(
    bool Enabled,              // Master toggle (default false)
    byte NumActiveSlices,      // 1-8, clamped to radio NumReceivers
    long[] SliceVfoHz          // Per-slice VFO offsets
);
```

**Discovery integration:**
- Read `NumReceivers` from Protocol2 discovery reply
- Expose via `/api/radio/capabilities` endpoint
- Frontend fetches at connect time to show/hide multi-slice toggle

### 3. Contracts: No Breaking Changes

`DisplayFrame.RxId` field already exists (line 62 of DisplayFrame.cs). Currently hardcoded to 0; this proposal simply uses it.

**Backward compatibility:**
- Single-slice clients ignore `RxId` (treat all frames as RxId=0)
- Multi-slice clients dispatch frames to per-RxId display stores

### 4. Frontend: Per-RxId Display Store

**Current:**
```typescript
export type DisplayState = {
  panDb: Float32Array | null;
  wfDb: Float32Array | null;
  centerHz: bigint;
  // ...single slice state
};
```

**Proposed:**
```typescript
export type SliceDisplayState = {
  panDb: Float32Array | null;
  wfDb: Float32Array | null;
  centerHz: bigint;
  hzPerPixel: number;
  panValid: boolean;
  wfValid: boolean;
  lastSeq: number;
};

export type DisplayState = {
  connected: boolean;
  slices: Map<number, SliceDisplayState>;  // RxId → state
  setConnected: (c: boolean) => void;
  pushFrame: (f: DecodedFrame) => void;    // Dispatches by RxId
};
```

**Frame dispatch:**
```typescript
pushFrame: (f) => {
  const rxId = f.rxId ?? 0;
  set((state) => ({
    slices: new Map(state.slices).set(rxId, {
      panDb: f.panDb,
      wfDb: f.wfDb,
      centerHz: f.centerHz,
      hzPerPixel: f.hzPerPixel,
      panValid: f.panValid,
      wfValid: f.wfValid,
      lastSeq: f.seq,
    }),
  }));
}
```

### 5. Frontend: Multiple Panadapter Panels

**Current:** Single `hero` panel (panadapter+waterfall)
**Proposed:** `hero`, `hero-rx1`, `hero-rx2`, ..., `hero-rx7` panel IDs

**Panel registry update (panels.ts):**
```typescript
export const PANELS: Record<string, PanelDef> = {
  hero: {
    id: 'hero',
    name: 'Panadapter · RX0 (Primary)',
    category: 'spectrum',
    tags: ['panadapter', 'waterfall', 'spectrum', 'rx0'],
    component: HeroPanel,
    config: { rxId: 0 },
  },
  'hero-rx1': {
    id: 'hero-rx1',
    name: 'Panadapter · RX1',
    category: 'spectrum',
    tags: ['panadapter', 'waterfall', 'spectrum', 'rx1'],
    component: HeroPanel,
    config: { rxId: 1 },
  },
  // ... hero-rx2 through hero-rx7
};
```

**Panadapter component changes:**
```typescript
export function HeroPanel({ config }: { config?: { rxId?: number } }) {
  const rxId = config?.rxId ?? 0;
  const slice = useDisplayStore((s) => s.slices.get(rxId));
  // ... render using slice state
}
```

**Layout duplication:**
- "Add Panel" modal shows `hero-rx1` through `hero-rx{N-1}` when multi-slice enabled
- Each panel reads from its RxId slice in the display store
- Panels can be independently added/removed/resized via flex layout

### 6. Frontend: Multi-Slice Settings Toggle

Add to Radio Settings panel (or new Advanced Settings):

```
☑ Enable Multi-Slice Operation (requires Protocol 2 radio)
  Number of active slices: [2] (max: 6 for this radio)

  Slice VFOs:
  RX0 (Primary): 14.200.000 MHz
  RX1:           14.150.000 MHz
```

**Behavior:**
- Toggle hidden on Protocol 1 radios
- Toggle disabled when `NumReceivers < 2`
- Changing `NumActiveSlices` triggers backend reconfiguration
- Backend opens/closes WDSP RXA channels as needed

## Implementation Phases

> Status as of 2026-05-06. The four phases below were the original PR plan;
> see "Implementation Status" at the top for the trimmed scope that actually
> shipped (HL2 only; Protocol-2 deferred).

### Phase 1: Backend Multi-Slice Foundation (this PR)
- [x] Add `MultiSliceConfig` to `StateDto` and persist in state store
- [x] Refactor `DspPipelineService._channelId` → `_slices[]` array
- [x] Open N WDSP RXA channels when multi-slice enabled
- [x] Broadcast per-RxId `DisplayFrame` (audio on RxId=0 only — Phase 2 mixer)
- [x] `/api/radio/capabilities` exposes `MaxReceivers` (the per-board ceiling
      sourced from the documented protocol limits, not a runtime probe)

### Phase 2: Frontend Per-RxId Display (this PR)
- [x] Convert `useDisplayStore` to per-RxId map
- [x] Update `Panadapter.tsx` (and `Waterfall.tsx`, `FreqAxis.tsx`,
      `PassbandOverlay.tsx`) to accept `rxId` config
- [x] Add `hero-rx1` / `hero-rx2` / `hero-rx3` to panel registry (HL2 ceiling)
- [x] Filter "Add Panel" modal by active slice count + `MaxReceivers`

### Phase 3: Multi-Slice UI Controls (this PR)
- [x] Add multi-slice control to RadioSelector (HL2 only in Phase 1)
- [x] Fetch `/api/radio/capabilities` at connect
- [x] Per-slice VFO via `POST /api/vfo` `{ Hz, RxId }`
- [ ] Update default layout for multi-slice mode (deferred — manual add per
      maintainer decision in Open Questions §3)

### Phase 4: Testing & Documentation (this PR)
- [x] Single-slice backward compatibility — bit-identical wire / DSP path
- [x] 2-slice / 3-slice / 4-slice unit tests
      (`tests/Zeus.Protocol1.Tests/MultiSliceWireFormatTests.cs`,
      `tests/Zeus.Server.Tests/PerRxVfoTests.cs`)
- [ ] On-air bench test on operator HL2 (post-merge — Brian's HL2)
- [ ] User guide: "Using Multiple Slices" (wiki, post-merge)

## Open Questions for Maintainer Review — Resolved

> Decisions taken during the Phase 1 implementation session (2026-05-06).
> The recommendations stand; stances below are the ones actually wired in
> the shipped code.

1. **Per-slice state:** Should each slice have independent AGC/NR/Filter settings, or share radio state initially?
   - **Decision (v1): Share state.** Every active slice inherits the
     primary's AGC / NR / filter / mode. Per-slice DSP state is a Phase 2
     concern — see "Implementation Status" above.

2. **Audio routing:** Should we support audio from non-primary slices (requires multi-channel WebAudio)?
   - **Decision (v1): RxId=0 audio only.** Non-primary slices stream IQ +
     panadapter / waterfall data only. TX is also tied to RxId=0. Multi-channel
     audio mixing is Phase 2.

3. **Default layout:** Should enabling multi-slice auto-add RX1 panadapter, or require manual "Add Panel"?
   - **Decision: Manual add.** Toggling multi-slice does not mutate the
     operator's saved layout. The Add Panel modal exposes `hero-rx1` /
     `hero-rx2` / `hero-rx3` once multi-slice is enabled and the connected
     board's `MaxReceivers` is high enough. This avoids surprising layout
     mutations on operators who flip the toggle to experiment.

4. **Protocol 1 compat:** Should P1 UI hide multi-slice controls entirely, or show grayed-out with tooltip?
   - **Decision: Show on capable P1 boards.** HL2 is a Protocol-1 board
     with multi-DDC support, so a blanket "P1 hides multi-slice" rule was
     wrong. The control is gated on `BoardCapabilities.MaxReceivers > 1`,
     not on protocol. Single-RX boards (Hermes / G2E) hide it.

5. **Slice naming:** "RX0/RX1/RX2" vs "Slice A/B/C" vs "Panadapter 1/2/3"?
   - **Decision: RX0/RX1/RX2/RX3.** Matches pihpsdr / Thetis convention.
     Panel labels read "Panadapter · RX0 (Primary)" / "Panadapter · RX1"
     etc. The wire-format `RxId` is 0-indexed and that's what surfaces in
     the UI.

## Risk Assessment

**Low Risk:**
- Using existing `RxId` field (no wire-format change)
- Feature gated behind toggle (default OFF)
- Backward-compatible frame dispatch (RxId=0 fallback)

**Medium Risk:**
- `DspPipelineService` refactor (touch load-bearing pipeline code)
- WDSP multi-channel management (ordering, state sync)
- Frontend display-store migration (existing components must adapt)

**Mitigation:**
- Incremental rollout (Phase 1 backend-only, Phase 2 frontend-only)
- Extensive single-slice regression testing
- Synthetic engine simulation before hardware testing

## Alternatives Considered

### Alt 1: Separate WebSocket Streams Per Slice
**Pros:** Cleaner separation, no RxId dispatch
**Cons:** N WebSocket connections, higher latency, complex reconnect

### Alt 2: Protocol 1 "Fake Multi-Slice" (VFO Scanning)
**Pros:** Works on HL2
**Cons:** Not true simultaneous monitoring, violates HPSDR protocol semantics

### Alt 3: New `MultiDisplayFrame` Wire Type
**Pros:** Explicit multi-slice intent
**Cons:** Breaking wire change, complicates single-slice path

**Chosen:** Use existing `RxId` field + backward-compatible frame dispatch

## References

- **Issue:** #[TBD] Support multiple panadapters for radios with multi-slice capability
- **Radio Capabilities Doc:** `docs/references/radio-multi-slice-capabilities.md`
- **Protocol 2 Client:** `Zeus.Protocol2/Protocol2Client.cs` (DDC management)
- **WDSP Multi-Channel:** `Zeus.Dsp/IDspEngine.cs` (already multi-channel aware)
- **pihpsdr Reference:** `new_protocol.c` — DDC2-7 handling on G2
- **Thetis Reference:** `ChannelMaster.cs` — multi-receiver UI (Windows Forms)

## Maintainer Decision

**Status:** ✅ Phase 1 approved & implemented (2026-05-06)
**Reviewer:** Brian (EI6LF)

Phase 1 shipped on the `claude/support-multiple-panadapters` branch with the
scope described in "Implementation Status" above. Phase 2 items remain open
for a future PR.

---

## Implementation Notes (if approved)

Once approved, implementation will follow phases 1-4 above. Each phase will be a separate commit for review. Backend changes will be tested with synthetic engine before requiring hardware. Frontend changes will maintain single-panadapter default.

This design intentionally avoids the "red-light" traps:
- No new dependencies (reuse WDSP multi-channel API)
- No UX changes for single-slice users (feature gated)
- No visual design changes (panadapter rendering unchanged)
- Default values unchanged (multi-slice defaults to OFF)

The flex layout system already supports panel duplication (see `docs/lessons/flex-layout-widgets.md`), so the "multiple panadapters" UI pattern is an extension of existing architecture, not a new paradigm.
