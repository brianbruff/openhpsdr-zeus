# PRD — Filter visualization & filter panel

**Status:** Draft (2026-04-23) — Brian Keating (EI6LF), via team-lead.
**Related:** `docs/proposals/band-planning-prd.md` (co-designed; this PRD consumes the
`inBand(freqHz, mode)` predicate from that one).
**Research:** `docs/proposals/research/wdsp-filter-inventory.md`,
`docs/proposals/research/thetis-filter-ux.md`.
**Design reference:** `docs/pics/filterpanel_mockup.png` — the authoritative visual target
for the advanced filter ribbon. Implementations must match this mockup; deviations are
red-light (CLAUDE.md).

---

## 1. Problem statement

Zeus today exposes the RX filter as **two numbers** on `/api/state` (`FilterLowHz`,
`FilterHighHz`) and has no operator-facing way to change them. The operator cannot:

- See the filter passband on the panadapter/waterfall (Thetis's single most-used visual cue
  for "am I hearing the signal I want to hear").
- Click a preset to switch between common widths (2.4k / 2.7k / 2.9k SSB, 500/250/100 Hz CW).
- Nudge the low/high edges independently for a variable SSB roofing shape.
- Tell at a glance whether the filter is hanging over a band edge — critical for SSB near
  the bottom of a band where a wide filter can push the low sideband out of allocation.

The WDSP wiring is already correct (engine-side `SetFilter` already writes all three RXA
stages per `rxa.cs:110–124`, and mode-switch preserves stored magnitudes — verified shipping
code at `WdspDspEngine.cs:333–343, 1175–1200`). The gap is **surface**: wire format,
REST/hub endpoint, frontend panel, and panadapter/waterfall overlay.

## 2. Non-goals

- TX filter editing surface. Thetis itself treats the TX filter as a per-profile pair set in
  Setup > DSP, not a per-mode preset stack. Zeus will follow suit: TX filter stays
  engine-managed.
- Second receiver (RX2) preset separation. Contract is already per-channel-id; UI shows
  whichever RXA is open.
- Filter tap count / min-phase knobs (`SetRXABandpassNC`, `SetRXABandpassMP`). Marked as
  `[GAP]` in the inventory; reserved for a later "DSP advanced" panel.
- FM filter presets — Thetis has none; Zeus will not invent them.
- Enforcement of out-of-band: this PRD only **visualizes** it. Transmit inhibit stays a band-
  planning PRD follow-up.

## 3. UX specification

Zeus ships **two** filter surfaces. The compact panel (§3.1) is always visible next to the
VFO; the advanced ribbon (§3.2) is an opt-in, closable pane that operators toggle from the
compact panel. The advanced ribbon is the surface shown in `docs/pics/filterpanel_mockup.png`
and is the primary design target of this PRD.

### 3.1 Compact filter panel (always visible, flex + mobile)

Placement: a single row under the VFO readout, spanning the width of the VFO + mode cluster.
In the existing Zeus flex-layout it joins the same panel as the VFO/mode/AGC group; the
maintainer (Brian) signs off on final placement (CLAUDE.md: visual design is red-light, so
the first PR lands with a plausible default and the maintainer adjusts).

Contents:

- **Width readout** (left): current filter width in Hz/kHz (e.g., `2.7 kHz` for USB F6,
  `500 Hz` for CWL F4). Single-hue amber (`#FFA028`), matches the existing VFO readout style.
- **Preset chip row** (center): 12 chips labelled from the Thetis default preset table for
  the current mode (`F1..F10` + `VAR1 VAR2`). The selected chip is filled amber; others show
  only the amber outline. Modes without presets (FM) hide the chip row and show just Lo/Hi.
- **Lo / Hi nudge controls** (right): two compact up/down pairs, each stepping by 10 Hz (SSB)
  or 10 Hz (CW) or 100 Hz (AM/SAM/DSB/DIGx). Clicking the chip `VAR1` or `VAR2` and then
  nudging writes the value back into that slot (matching Thetis's edit-in-place behavior).
  Clicking a fixed `F*` chip and nudging is **silent-accepted** — Zeus does not overwrite
  fixed-preset slots (cleaner semantics than Thetis, where any edit bleeds back into the
  slot). The server will return HTTP 409 for a fixed-slot write and the frontend surfaces a
  brief toast.
- **Advanced toggle**: a single icon button (amber outline, small waveform/ribbon glyph) at
  the far right of the panel. Clicking it opens the advanced filter ribbon (§3.2). The
  button is a toggle: pressed state = ribbon open. Hidden on mobile breakpoints (the ribbon
  itself is desktop-flex-only; see §3.2).

### 3.2 Advanced filter ribbon (new component — primary design target)

**Visual reference:** `docs/pics/filterpanel_mockup.png`. The implementation must match the
mockup 1:1 for layout, typography weight, and column ordering. Color remains the Zeus amber
convention (`#FFA028`, varying alpha) — the mockup's cyan/blue accents on the passband
handles translate to amber in Zeus.

**Placement:** its own pane in the flex-layout grid. The operator drops it wherever other
flex panes can go. It is **desktop/flex-mode only** — on the mobile breakpoint the pane is
hidden entirely and the compact panel (§3.1) is the sole filter surface. The advanced
toggle button on §3.1 is also hidden on mobile so the operator cannot open a pane they
cannot see.

**Visibility model:** closable. A close affordance (`×`, top-right of the ribbon — see
mockup) hides the pane and releases its flex slot. Reopening is via the §3.1 advanced
toggle. The open/closed state persists per-operator in `localStorage` and across server
restarts in `DspSettingsStore.FilterAdvancedPaneOpen` (so an operator who closed the pane
on one browser sees it closed on another).

**Layout — columns left to right, matching mockup:**

1. **BANWIDTH** label (small-caps, muted amber) — section header for the compact text
   column that follows. (Spelling matches mockup; rendered in code as `BANDWIDTH`.)
2. **LOW CUT** — absolute frequency in MHz to 3-decimal-kHz precision (e.g. `14.254.650`).
   Updates live as the low edge moves. Format: `{MHz_int}.{kHz_3digits}.{Hz_3digits}` with
   a `.` thousands separator, exactly as the mockup shows.
3. **PASSBAND** — the width in kHz to two decimals (e.g. `2.70 kHz`). This is the widest
   readout, centered, and is the ribbon's focal element.
4. **HIGH CUT** — absolute frequency in MHz, same formatting as LOW CUT.
5. **Mini-panadapter** (the spectrum strip) — see §3.2.1 below.
6. **PRESET BANDWIDTHS column** (right side): a small header `≡ PRESET BANDWIDTHS`, then a
   3×2 grid of chip buttons. The six chips shown in the mockup are the defaults for SSB:
   `2.4 kHz`, `2.7 kHz` (selected in mockup), `3.6 kHz`, `6.0 kHz`, `9.0 kHz`, `12.0 kHz`.
   These map to the existing F-slot table for the active mode — the ribbon does not
   introduce a parallel preset system; it is a second *view* of the §3.1 chip row, filtered
   to the six most common widths per mode. Below the grid, a full-width `CUSTOM` button
   with a pencil-edit glyph; pressing it flips the active slot to `VAR1` and arms the nudge
   affordances (drag or keyboard) without changing the current passband.
7. **Close button** (`×`, top-right): hides the pane (see visibility model above).

**Footer hint** (below the mini-panadapter, matching the mockup): the string
`DRAG EDGES TO ADJUST • DRAG INSIDE TO MOVE` in muted amber small-caps. This is a
static affordance hint — not a status line.

#### 3.2.1 Mini-panadapter (inside the ribbon)

The spectrum strip inside the ribbon is a **separate, purpose-built panadapter** — not a
reuse of the main panadapter GL pipeline. Performance is a first-class constraint (the
operator may keep the ribbon open continuously on top of the main panadapter, so the
combined cost must remain < ~4 ms/frame on mid-range integrated GPUs).

- **Span: exactly 10 kHz**, centered on the VFO. The x-axis labels in the mockup
  (14.249 … 14.261 when VFO=14.255 MHz) are rendered every 2 kHz, with a centered tick on
  the VFO itself.
- **Data source:** the existing FFT frame already streamed for the main panadapter. The
  ribbon subscribes to the same frame stream and **decimates / re-windows** to its 10 kHz
  span — it does **not** request a second FFT from the server. If the current main-pan
  span is narrower than 10 kHz, the ribbon extrapolates by showing the available bins
  only (no synthetic fill); if wider, it windows.
- **Rendering:** single WebGL quad + line-strip, one draw call per frame. No waterfall, no
  grid lines beyond the six labelled ticks, no peak/avg overlays, no per-bin markers. The
  implementation must measure and document frame cost in the PR description.
- **Passband overlay inside the strip:** a translucent amber rectangle from `px(loHz)` to
  `px(hiHz)`, full ribbon height, with two 1 px vertical amber edge lines. At each top
  corner of the rectangle, a small triangular "handle" glyph (matching the mockup's cyan
  corner marks — amber in Zeus).
- **Drag behavior:** hovering within ±6 px of an edge line shows an ew-resize cursor and
  arms edge-drag. Hovering inside the rectangle (but outside the edge-hit zones) shows a
  move cursor and arms whole-passband drag — both edges move together, width preserved.
  Drag writes are rate-limited client-side (20 Hz max). First drag auto-flips the active
  slot to `VAR1`.
- **Keyboard:** when the ribbon has focus, `←`/`→` nudge the most-recently-touched edge by
  the mode's step size (SSB 10 Hz, CW 10 Hz, AM/SAM/DSB/DIGx 100 Hz, DIGL/DIGU 50 Hz);
  `Shift+←/→` nudges by 10× step. `Esc` closes the pane.

#### 3.2.2 Performance budget

The ribbon's mini-panadapter is explicitly scoped as a **super-optimized** component
because the operator may keep it open continuously. Hard targets:

- **< 2 ms/frame** GPU time on a 2020-era integrated GPU (Intel Iris Xe baseline).
- **Single WebGL context** shared with the main panadapter (no separate canvas that
  incurs a second compositor pass). The ribbon draws into a scissor-clipped sub-rect of
  the existing canvas, or into a separate canvas that reuses the same GL context via
  `canvas.transferControlToOffscreen` — implementation choice, but must be justified in
  the PR.
- **No per-frame allocations** in the render hot path. The decimation buffer is a single
  reused `Float32Array`.
- **Idle frame suppression:** if the operator is not dragging and the incoming FFT frame
  is bit-identical to the prior frame (or within a configurable tolerance), skip the
  draw. Eye-candy is not worth the battery.

If any of these targets cannot be hit on first implementation, the PR must state that
explicitly rather than shipping a slower version; the maintainer will decide whether to
accept the regression or iterate.

### 3.3 Panadapter / waterfall overlay (main panadapter, unchanged by advanced ribbon)

Single layer rendered on top of the existing GL panadapter (reuses the same coordinate space
the VFO center-frequency marker already uses):

- **Shaded passband fill**: a translucent amber rectangle (`rgba(255, 160, 40, 0.18)`) from
  x=`px(vfo + loHz)` to x=`px(vfo + hiHz)`, spanning the full panadapter height.
- **Edge lines**: two 1px solid amber vertical lines at the same x positions, full height.
- **Drag handles**: the edge lines become drag-cursor-enabled when the operator hovers
  within ±4 px. Dragging writes the new Lo or Hi through the same REST endpoint the preset
  chips use; the active chip auto-flips to `VAR1` if a fixed preset was selected. Drag is
  rate-limited client-side (20 Hz max) to keep the wire from thrashing.
- **Waterfall**: the same shaded column is drawn on the waterfall layer (no edge lines, just
  the fill — the panadapter carries the resolution for fine alignment).

Out-of-band coloring (requires `band-planning-prd.md` to ship first):

- If either `vfo + loHz` or `vfo + hiHz` falls outside the current region/mode band plan
  (as reported by `inBand(freqHz, mode)`), the fill color flips to red
  (`rgba(255, 60, 40, 0.28)`) and the edge line on the offending side goes solid red. The
  panel width readout appends a small red `OOB` label. No TX interference — purely visual.
- Until the band-planning PRD lands, this path is a no-op and the overlay stays amber in
  all cases. The filter PRD commits the client code path behind a `bandPlan.inBand`
  predicate that returns `true` when the band plan is unavailable.

### 3.4 Interaction details

- Clicking a preset chip: immediate write, no confirm. Optimistic UI with rollback on
  server-reported error.
- Mode switch: engine already re-applies stored magnitudes with correct sign
  (`ApplyBandpassForMode` via `SetMode`). Frontend shows the **LastFilter** slot (`F5` by
  default per Thetis convention) as active; if the persisted slot for the new mode is
  different, that one lights up instead.
- CW offset: the frontend adds `cw_pitch` (default 600 Hz; reads from state if/when we
  expose it) to the low/high when drawing CW — matching Thetis's `-cw_pitch ± half` form.
  The Lo/Hi panel values shown to the operator are **the actual Hz offsets from VFO**, not
  centered-on-pitch — operators expect to read VFO-relative numbers.

## 4. Data model

### 4.1 Wire contract additions (`Zeus.Contracts/FilterFrame.cs` — new file)

```csharp
namespace Zeus.Contracts;

public sealed record FilterStateFrame(
    int ChannelId,
    RxMode Mode,
    int LowHz,     // signed, VFO-relative
    int HighHz,    // signed, VFO-relative
    string? PresetName = null,  // e.g. "F6" or "VAR1" — nullable if operator dragged
    int? PresetIndex = null);

public sealed record FilterSetRequest(
    int LowHz,
    int HighHz,
    string? PresetName = null);  // optional — nudges without a preset context omit this

public sealed record FilterPresetWriteRequest(
    RxMode Mode,
    string SlotName,     // "VAR1" or "VAR2"; server rejects F1..F10
    int LowHz,
    int HighHz);
```

The server rejects `FilterPresetWriteRequest` for slots other than `VAR1/VAR2` with HTTP 409.

### 4.2 State extension

`StateDto` already carries `FilterLowHz` / `FilterHighHz`. Add:

- `FilterPresetName: string?` — the currently-active slot name, or `null` after a drag edit.

### 4.3 Server persistence

`DspSettingsStore` currently holds AGC/NR/attenuator state. Extend with:

- `FilterPresetOverrides: Dictionary<RxMode, { var1: (lo, hi), var2: (lo, hi) }>`
  — per-mode VAR1/VAR2 edits.
- `LastSelectedPreset: Dictionary<RxMode, string>` — remembers which preset slot the
  operator last used per mode, so mode-switch recalls the matching slot.
- `FilterAdvancedPaneOpen: bool` — whether the advanced filter ribbon (§3.2) is currently
  open in the flex layout. Defaults to `false` on first run.

All three persist across Zeus.Server restarts (LiteDB, same file).

## 5. Backend changes

### 5.1 `RadioService` / `StreamingHub`

- `SetFilter(int lowHz, int highHz, string? presetName)` — existing hub method; grow to
  accept `presetName` and broadcast the updated `FilterStateFrame`.
- `SetFilterPresetOverride(RxMode mode, string slotName, int loHz, int hiHz)` — new.
  Validates slotName in `{VAR1, VAR2}`. Persists via `DspSettingsStore`. Returns the
  updated override map.
- `GetFilterPresets(RxMode mode): IReadOnlyList<FilterPreset>` — returns the merged Thetis
  defaults + operator overrides for the requested mode. Frontend calls this on mount and
  after any VAR* write.

### 5.2 REST endpoints (parity with hub)

- `POST /api/filter` — body `FilterSetRequest`.
- `GET /api/filter/presets?mode=USB` — returns preset list.
- `POST /api/filter/presets` — body `FilterPresetWriteRequest` (VAR* only).

### 5.3 Frame publishing

Add `FilterStateFrame` to the `StreamingHub` broadcast set; emit on every `SetFilter` or
mode change. Existing state broadcast already carries `FilterLowHz/HighHz` — this is the
richer variant that adds preset context.

## 6. Frontend changes (`zeus-web/`)

### 6.1 New files

- `src/components/filter/FilterPanel.tsx` — the compact VFO-row component described in §3.1,
  including the advanced-ribbon toggle button.
- `src/components/filter/FilterRibbon.tsx` — the advanced ribbon pane described in §3.2
  (layout, LOW CUT / PASSBAND / HIGH CUT readouts, preset grid, CUSTOM button, close).
  Hidden on the mobile breakpoint via the existing layout's `desktopOnly` slot (no
  conditional render inside the component — the layout shell never mounts it on mobile).
- `src/components/filter/filterPresets.ts` — TypeScript constant mirroring
  `thetis-filter-ux.md` §2 (all 10 modes × 12 slots). Source of truth for default labels and
  default Lo/Hi. Also exposes the six-preset subset shown in the ribbon's right column.
- `src/gl/panadapter/FilterOverlay.ts` (or equivalent hook in the existing panadapter module)
  — renders the shaded passband + edge lines + drag handles on the **main** panadapter.
- `src/gl/filterRibbon/MiniPanadapter.ts` — the 10 kHz mini-panadapter renderer for §3.2.1.
  Shares the main panadapter's WebGL context (see §3.2.2 performance budget); reuses the
  incoming FFT frame and a single preallocated `Float32Array` decimation buffer. Owns its
  own vertex/index buffers but no textures.
- `src/state/filter.ts` — client state: active preset slot, drag-in-flight flag, per-mode
  override cache (seeded by `GET /api/filter/presets` on connect), advanced-ribbon
  open/closed flag (mirrored to `localStorage` and to the server-side
  `FilterAdvancedPaneOpen`).

### 6.2 Modified files

- `src/layout/*` — inject `FilterPanel` into the VFO row and register `FilterRibbon` as a
  desktop-only flex pane (mobile breakpoint omits it entirely — it is never mounted on
  mobile, never hidden-via-CSS). Maintainer to confirm placement.
- `src/realtime/hubClient.ts` — handle `FilterStateFrame` subscription.
- `src/gl/panadapter/*` — invoke the new overlay after the waterfall pass (Hz-to-pixel math
  is already available via the panadapter's freq-to-x projection). Expose the shared GL
  context so `MiniPanadapter.ts` can reuse it (§3.2.2).

## 7. Band-planning integration (forward contract)

This PRD commits to consuming the band-plan predicate without blocking on its
implementation. Contract:

```ts
// Provided by band-planning PRD; a no-op stub ships with this PRD.
export interface BandPlan {
  inBand(freqHz: number, mode: RxMode): boolean;
  getSegment(freqHz: number): BandSegment | null;  // nullable when off-plan
}
```

The filter overlay imports `BandPlan` via a React context. Until the band-planning PRD lands,
a stub is registered that returns `true` unconditionally, so the amber overlay ships as the
only visible state.

**Handoff definition**: `inBand(f, mode)` returns `true` iff the frequency is inside a
segment whose `mode` matches (mode-aware so 40m CW sub-band doesn't light green for USB).
`getSegment` is unused in this PRD — but the filter overlay will use it later for the hover
tooltip ("40m Extra CW").

## 8. Acceptance criteria

1. On fresh connect, operator sees the filter panel under the VFO with default preset `F6`
   (2.7k) highlighted for USB; shaded amber passband visible on panadapter and waterfall.
2. Clicking `F7` changes the filter to 100..2500 Hz (USB), width readout updates to `2.4 kHz`,
   audio passband narrows audibly, WDSP logs confirm the new Lo/Hi values.
3. Switching mode USB → LSB preserves the F6 selection (Thetis parity) and re-signs the
   passband so the overlay appears on the correct side of the carrier.
4. Dragging the right edge of the passband updates Hi in real time; the active chip flips to
   `VAR1`; server logs one write per ~50 ms during the drag (rate-limited).
5. Restarting Zeus.Server and reconnecting restores the operator's last VAR1 edit for USB
   (persisted in `DspSettingsStore`).
6. Attempting `POST /api/filter/presets` with `SlotName=F6` returns HTTP 409 and the
   frontend toasts "Fixed presets cannot be edited".
7. With the band-plan stub returning `true`, no red OOB coloring appears anywhere.
8. (Post band-plan PRD) Tuning USB on 14.347 MHz with a 5.0k filter (F1) shows the right
   edge crossing 14.350 MHz and the fill turns red on the high side.
9. Clicking the compact-panel advanced toggle opens the ribbon as a new flex pane; the
   ribbon shows a 10 kHz mini-panadapter centered on the VFO, with LOW CUT / PASSBAND /
   HIGH CUT readouts matching `docs/pics/filterpanel_mockup.png`, and the six preset chips
   on the right (2.4 / 2.7 / 3.6 / 6.0 / 9.0 / 12.0 kHz for SSB).
10. Dragging an edge handle in the ribbon updates HIGH CUT (or LOW CUT) live, PASSBAND
    updates to the new width, the active chip flips to `VAR1`, and the main panadapter's
    shaded passband updates in lock-step.
11. Dragging inside the passband rectangle (not on an edge) moves both edges together,
    preserving PASSBAND width; LOW CUT and HIGH CUT both update live.
12. Clicking the ribbon's close button (`×`) hides the pane and releases its flex slot;
    reopening via the compact panel toggle restores it at the same slot.
13. The ribbon's open/closed state survives a Zeus.Server restart
    (`FilterAdvancedPaneOpen` persisted in `DspSettingsStore`).
14. On the mobile breakpoint the compact panel is present, the advanced toggle button is
    hidden, and the ribbon component is not mounted at all (DOM inspection confirms no
    `FilterRibbon` element).
15. Performance: with the ribbon open during a typical 14 MHz SSB session, total
    panadapter-related frame time stays under ~4 ms on the maintainer's reference
    hardware; the ribbon's mini-panadapter issues a single WebGL draw call per frame and
    allocates zero objects per frame in the render hot path.

## 9. Open questions

- **Default preset for Zeus's current 150..2850 passband**: does the PRD preserve Zeus's
  wider low-cut as a new "VAR1 pre-seed" on first connect, or reset operators to Thetis F6?
  Maintainer call. Default implementation: preserve Zeus's 150/2850 as VAR1 for SSB on
  first run, and select VAR1 on that first run only.
- **CW pitch readout**: should the panel expose `cw_pitch` to the operator (Setup
  control), or wait for a broader "DSP advanced" PRD? Default: do not expose in this PRD.
- **Step sizes for nudge**: 10 Hz SSB feels right; should it be 50 Hz for DIGx to avoid
  misery? Default: 50 Hz step for DIGL/DIGU.
- **Display span behavior**: if operator narrows the filter below one pixel-per-Hz at
  current zoom, do we auto-zoom? Default: no, keep zoom operator-controlled.

## 10. Implementation phasing

- **Phase 1** (this PRD's PR) — wire contract + hub/REST + backend store + minimal
  compact filter panel (§3.1) with fixed presets only, no drag handles, no OOB coloring,
  no advanced toggle button.
- **Phase 2** — main panadapter overlay (shaded + edge lines, no drag), waterfall overlay.
- **Phase 3** — drag handles on the main panadapter writing back to VAR*.
- **Phase 4** — advanced filter ribbon (§3.2): ribbon layout, 10 kHz mini-panadapter,
  preset grid, CUSTOM button, close, edge + whole-passband drag, performance budget
  (§3.2.2) met. Advanced-toggle button added to the compact panel in this phase.
  Ribbon is desktop-flex-only; mobile layout unchanged.
- **Phase 5** — OOB coloring on both the main panadapter overlay and the ribbon
  mini-panadapter, gated on band-planning PRD v1 shipping and the BandPlan context being
  populated.

Each phase is a separately-mergeable PR. The maintainer can stop after any phase without
leaving half-finished UX visible.
