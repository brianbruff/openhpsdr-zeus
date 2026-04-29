# Multi-Panadapter Support — Design Proposal

## Problem Statement

Zeus currently supports only a single panadapter/slice per radio connection, even though some Protocol 2 hardware (G2 MkII, ANAN-7000DLE/8000DLE) supports up to 8 simultaneous DDC receivers. This limits operators who want to monitor multiple frequencies simultaneously.

**Issue:** #[TBD]
**Status:** Proposal — awaiting maintainer review
**Author:** Agent implementation based on issue request

## Goals

1. **Enable multi-slice operation** on capable Protocol 2 radios
2. **Preserve backward compatibility** — default single-panadapter behavior unchanged
3. **Minimal wire-format changes** — reuse existing `DisplayFrame.RxId` field
4. **Flexible UI** — allow operators to add/remove panadapters via flex layout
5. **Per-board capability awareness** — respect `NumReceivers` from discovery

## Non-Goals

- Protocol 1 multi-slice support (P1 radios are single-DDC only)
- Independent TX on non-primary slices (TX remains tied to RxId=0)
- Per-slice AGC/DSP/NR state (all slices share radio state initially; can be extended later)
- Slice synchronization/lock features (advanced feature for future consideration)

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

### Phase 1: Backend Multi-Slice Foundation (this PR)
- [ ] Add `MultiSliceConfig` to `StateDto` and persist in state store
- [ ] Refactor `DspPipelineService._channelId` → `_slices[]` array
- [ ] Open N WDSP RXA channels when multi-slice enabled
- [ ] Broadcast per-RxId `DisplayFrame` and `AudioFrame` (audio on RxId=0 only initially)
- [ ] Add `/api/radio/capabilities` endpoint exposing `NumReceivers`

### Phase 2: Frontend Per-RxId Display (this PR)
- [ ] Convert `useDisplayStore` to per-RxId map
- [ ] Update `Panadapter.tsx` to accept `rxId` prop
- [ ] Add `hero-rx1` through `hero-rx7` to panel registry
- [ ] Filter "Add Panel" modal by active slice count

### Phase 3: Multi-Slice UI Controls (this PR)
- [ ] Add multi-slice toggle to settings panel
- [ ] Fetch `/api/radio/capabilities` at connect
- [ ] Show/hide per-slice VFO controls
- [ ] Update default layout for multi-slice mode (optional preset)

### Phase 4: Testing & Documentation (this PR)
- [ ] Test single-slice backward compatibility
- [ ] Test 2-slice simulation (synthetic engine)
- [ ] Test on G2 MkII hardware (if available)
- [ ] Update `flex-layout-widgets.md` with multi-panadapter info
- [ ] Add user guide: "Using Multiple Slices"

## Open Questions for Maintainer Review

1. **Per-slice state:** Should each slice have independent AGC/NR/Filter settings, or share radio state initially?
   - **Recommendation:** Share state in v1 (simpler), add per-slice state in v2 if needed

2. **Audio routing:** Should we support audio from non-primary slices (requires multi-channel WebAudio)?
   - **Recommendation:** RxId=0 audio only in v1, add mixer in v2

3. **Default layout:** Should enabling multi-slice auto-add RX1 panadapter, or require manual "Add Panel"?
   - **Recommendation:** Manual add (operator choice), document in settings tooltip

4. **Protocol 1 compat:** Should P1 UI hide multi-slice controls entirely, or show grayed-out with tooltip?
   - **Recommendation:** Hide entirely (avoid clutter)

5. **Slice naming:** "RX0/RX1/RX2" vs "Slice A/B/C" vs "Panadapter 1/2/3"?
   - **Recommendation:** "RX0/RX1/RX2" (matches pihpsdr/Thetis convention)

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

**Status:** ⏸️ Pending Review
**Reviewer:** Brian (EI6LF)

**Decision Options:**
- ✅ **Approve:** Proceed with implementation as proposed
- 🔄 **Revise:** Request changes to design (comment below)
- ❌ **Defer:** Not ready for v1, revisit in future release
- 🔀 **Alternative:** Propose different approach

---

## Implementation Notes (if approved)

Once approved, implementation will follow phases 1-4 above. Each phase will be a separate commit for review. Backend changes will be tested with synthetic engine before requiring hardware. Frontend changes will maintain single-panadapter default.

This design intentionally avoids the "red-light" traps:
- No new dependencies (reuse WDSP multi-channel API)
- No UX changes for single-slice users (feature gated)
- No visual design changes (panadapter rendering unchanged)
- Default values unchanged (multi-slice defaults to OFF)

The flex layout system already supports panel duplication (see `docs/lessons/flex-layout-widgets.md`), so the "multiple panadapters" UI pattern is an extension of existing architecture, not a new paradigm.
