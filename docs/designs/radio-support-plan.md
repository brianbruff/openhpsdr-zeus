# Plan: Complete Radio Board Support

Pairs with `docs/references/protocol-1/thetis-board-matrix.md` (the spec)
and `docs/designs/radio-support-audit.md` (the gap list).

**Goal:** bring Zeus to behavioural parity with MW0LGE Thetis for every
HPSDR radio he supports — Metis (G1) / Hermes / ANAN-10 / 10E / 100 /
100B / 100D / 200D / OrionMkII / 7000DLE / 8000DLE / G2 / G2-1K /
ANVELINA-PRO3 / Red Pitaya / G2E. Hermes-Lite 2 stays on its existing
mi0bot-sourced path; no changes here.

## Status legend

- 🟢 **green** — agent-autonomous, low risk, self-PR.
- 🟡 **yellow** — affects an operator-felt default or surfaces UI; agent
  implements, maintainer reviews before merge.
- 🔴 **red** — architecture-class or visual-design; needs maintainer
  alignment *before* code lands. Per `CLAUDE.md`.

## Already shipped on `feature/radio_support`

| SHA | Commit |
|---|---|
| `d3cb1b1` | docs(boards): catalog every board MW0LGE Thetis supports |
| `dff5afd` | docs(boards): audit current Zeus per-board seams + identify gaps |
| `83364ec` | feat(boards): recognise ANAN-G2E (HermesC10, wire 0x14) on discovery |
| `1bcbd7d` | feat(boards): wire ANAN-G2E (HermesC10) through PA + meter dispatch |

Wire-byte mapping is now complete for every value Apache Labs / OpenHPSDR
documents. The remaining work is *behavioural depth* — exposing more
per-board facts to dispatch, plus closing the two architectural seams
the audit flagged.

---

## Phase 1 — Wire-mapping completeness 🟢

Already done. Verify-only follow-up:

- **1.1** Add a parametric test (xUnit `[Theory]`) covering every
  enum value in `RadioCalibrations.For` so a dispatch regression
  is impossible to land silently.
- **1.2** Add discovery parser tests for *every* recognised wire byte
  (0x00, 0x01, 0x02, 0x04, 0x05, 0x06, 0x0A, 0x14) on both protocols.
  Some are missing today.
- **1.3** End-to-end smoke test: a Metis (G1) discovery yields
  `HpsdrBoardKind.Metis` → `HermesGains` → 10 W → `RadioCalibration.Hermes`.

**Risk:** none. Pure regression-pinning.
**Effort:** 2–3 hours.

---

## Phase 2 — Per-board capabilities surface 🟢

Today Zeus dispatches on `HpsdrBoardKind` for PA gain, max watts, and
bridge calibration. Thetis exposes much more (`thetis-board-matrix.md`):

- `RxAdcCount` — 1 (Hermes-class) or 2 (DDC family).
- `MkiiBpf` — second-generation Apache marker.
- `AdcSupplyMv` — 33 (Hermes-class) or 50 (high-power).
- `LrAudioSwap` — Hermes-family quirk.
- `HasVolts` / `HasAmps` — voltmeter/ammeter telemetry presence.
- `HasAudioAmplifier` — on-board headphone amp (P2 only).
- `HasSteppedAttenuationRx2` — RX2 attenuator vs gain-reduction.
- `SupportsPathIllustrator` — UI panel gating.

Introduce a single `Zeus.Contracts.BoardCapabilities` record keyed off
`HpsdrBoardKind`; populate from the matrix doc; surface through the
`RadioConnectedFrame` so the web reads it once at connect.

**Tasks:**
1. Define `BoardCapabilities` record in `Zeus.Contracts`.
2. Implement `BoardCapabilities.For(HpsdrBoardKind)` — table-driven
   from `thetis-board-matrix.md`.
3. Add to `RadioConnectedFrame` payload.
4. Parametric tests for every enum value (uses Phase 1's test pattern).

**No behaviour change** — facts only. Unlocks Phase 5 without committing
to the UI work yet.

**Risk:** none (additive).
**Effort:** half-day.

---

## Phase 3 — Wire-0x0A collision: operator override 🟡

Wire byte `0x0A` aliases six radios with materially different PA
calibration:

| Variant | Bridge V | Ref V | Offset | Max W | Today |
|---|---|---|---|---|---|
| ANAN-G2 (Saturn FPGA) | 0.12 | 5.0 | 32 | 100 | **default** |
| ANAN-G2-1K | 0.12 | 5.0 | 32 | 1000 | inherits G2 |
| ANAN-7000DLE | 0.12 | 5.0 | 32 | 100 | inherits G2 |
| ANVELINA-PRO3 | 0.12 | 5.0 | 32 | 100 | inherits G2 |
| Red Pitaya | 0.12 | 5.0 | 32 | 100 | inherits G2 |
| **ANAN-8000DLE** | **0.08** | 5.0 | 18 | 200 | **~30 % low FWD** |
| Apache OrionMkII (orig) | 0.08 | 5.0 | 18 | 100 | shares 8000D constants |

The bucket `RadioCalibration.OrionMkIIAnan8000` already exists in
`Zeus.Contracts/RadioCalibration.cs` — just unwired.

**Two paths considered:**

- **Path A — operator override (recommended).** Per-radio, MAC-keyed
  variant selection; default unchanged (G2). UI dropdown in the radio
  picker. Reliable, explicit, no firmware-version reverse-engineering.
- **Path B — firmware-version sniffing.** Apache firmware versions don't
  reliably identify the board model; this becomes a brittle lookup table
  that needs maintenance per firmware release. Not recommended.

**Tasks (Path A):**
1. Add `OrionMkIIVariant` enum to `Zeus.Contracts`:
   `G2` (default), `G2_1K`, `Anan7000DLE`, `Anan8000DLE`, `OrionMkII_Original`,
   `AnvelinaPro3`, `RedPitaya`.
2. Extend `PreferredRadioStore` to persist variant by MAC.
3. `RadioCalibrations.For(board, variant)` — variant only consulted when
   board is `OrionMkII`.
4. `PaDefaults.GetMaxPowerWatts(board, variant)` — distinguishes 100 W
   vs 200 W vs 1 kW.
5. UI: small dropdown next to the radio name in the picker; defaults to
   "Auto (G2)".
6. Test coverage per variant + persistence round-trip.

**Default preserved:** unset variant → G2 (today's behaviour).

**Why yellow not red:** this changes a default *only when the operator
opts in*. The default remains G2 across the board.

**Risk:** medium — touches operator-facing settings.
**Effort:** ~1 day.
**Maintainer review needed for:** UI placement of the variant dropdown,
naming of the variants, default copy ("Auto (G2)" vs "Unspecified" etc.).

---

## Phase 4 — P1/P2 enum unification 🔴

Today:

- `Zeus.Protocol1.Discovery.HpsdrBoardKind` covers `{Metis, Hermes,
  Griffin, Angelia, Orion, HermesLite2, OrionMkII, HermesC10}`.
- `Zeus.Protocol2.Discovery.HpsdrBoardKind` covers `{Atlas, Hermes,
  HermesII, Angelia, Orion, HermesLite2, OrionMkII, HermesC10}`.
- Server-side dispatch consumes only the P1 enum. P2-only wire IDs
  (`Atlas` 0x00, `HermesII` 0x02) cannot reach server dispatch.

**Three approaches:**

- **Option A — promote to a single canonical enum in `Zeus.Contracts`.**
  Both discovery parsers emit the contract enum. Protocol-specific
  enums get deleted (or kept as `[Obsolete]` aliases). Cleanest. Breaks
  any external consumer of `Zeus.Protocol1.Discovery.HpsdrBoardKind`
  (none expected outside the repo).
- **Option B — projection function.** Keep both enums; add server-side
  union enum + conversion functions. Three enums to maintain in lockstep.
- **Option C — collapse to raw wire byte server-side.** Loses compile-time
  exhaustiveness checks on switches. Not recommended.

**Recommendation:** Option A.

**Migration of persisted state:** `PaSettingsStore` and
`PreferredRadioStore` keys today serialise the enum as `byte`. The
contract enum will preserve the same wire-byte values, so int-on-disk
stays the same; no data migration needed. **Verify** with a one-shot
startup test.

**Tasks (Option A):**
1. Define `Zeus.Contracts.HpsdrBoardKind` covering the union:
   `Metis (0x00), Hermes (0x01), Hermes2 (0x02), Angelia (0x04),
   Orion (0x05), HermesLite2 (0x06), OrionMkII (0x0A),
   HermesC10 (0x14), Unknown (0xFF)`.
   - Decide naming for 0x00: today P1 calls it `Metis`, P2 calls it
     `Atlas` — same wire byte, different conceptual ancestry. Suggest
     `Metis` as canonical (the Apache Labs documentation favours it).
   - Decide naming for 0x02: P1 `Griffin` vs P2 `HermesII` — same byte.
     Suggest `Hermes2` as canonical (matches what Apache calls it).
2. Both `ReplyParser.MapBoard` switches emit `Zeus.Contracts.HpsdrBoardKind`.
3. Mark `Zeus.Protocol1.Discovery.HpsdrBoardKind` and
   `Zeus.Protocol2.Discovery.HpsdrBoardKind` `[Obsolete]` with redirect
   guidance, then delete after one release cycle.
4. Update every dispatch site:
   `RadioCalibrations.For`, `PaDefaults.TableFor`,
   `PaDefaults.GetMaxPowerWatts`, `RadioDriveProfiles.For`,
   `BoardCapabilities.For`, `RadioService.ConnectedBoardKind`,
   `RadioService.EffectiveBoardKind`, `ControlFrame` HL2 guards.
5. Test: P2 discovery returns 0x00 → server picks Hermes-class default
   bucket (currently impossible).

**Risk:** high. Affects every dispatch site and any persisted state.
**Effort:** ~1 day, including migration verification.
**Maintainer alignment needed before starting:**
- Naming of 0x00 (`Metis` vs `Atlas`) and 0x02 (`Hermes2` vs `Griffin`
  vs `HermesII`).
- Acceptance of breaking change to `Zeus.Protocol1.Discovery.HpsdrBoardKind`
  (likely fine — internal symbol).

---

## Phase 5 — Per-board UI conditional rendering 🔴 (visual-design)

Once `BoardCapabilities` lands (Phase 2), the web can conditionally
render:

- Volt/amp meter (gated on `HasVolts` / `HasAmps`).
- RX2 attenuator UI mode: stepped vs gain-reduction
  (`HasSteppedAttenuationRx2`).
- Audio-amp control surface (`HasAudioAmplifier`).
- Path Illustrator panel (`SupportsPathIllustrator`).

Per `CLAUDE.md`: visual design / UX is red-light. I implement the
*data-driven gating logic* but the panel layout, control placement, and
default visibility belong to the maintainer.

**Tasks:**
1. `RadioConnectedFrame` already carries capabilities (Phase 2).
2. React store reads capabilities; existing panels gain visibility
   guards that return `null` when the capability is false.
3. Maintainer review on layout for any newly-revealed panels — most
   already exist, so the diff is a few `if (caps.hasX)` lines, not new
   UI components.

**Risk:** low engineering, requires design review.
**Effort:** ~half-day engineering + maintainer review.

---

## Phase 6 — PureSignal `hw_peak` per-board 🔴

Thetis `PSDefaultPeak` (matrix doc, "PureSignal default `hw_peak`"):

- P1 (any board): `0.4072`
- P2 Saturn / G2: `0.6121`
- P2 anything else: `0.2899`

Zeus today:
- HL2: `0.233` (deliberately tuned — keep, see
  `docs/lessons/hl2-ps-hwpeak-calibration.md`).
- Non-HL2: not sourced from Thetis matrix; PS code path for non-HL2 has
  `TODO(ps-p1)` markers (audit §5).

**Why red:** `hw_peak` is operator-felt — wrong value silently traps the
PS state machine in COLLECT (per HL2 lesson). Any change risks regressing
the HL2 path if the dispatch isn't tight.

**Tasks:**
1. Add `BoardCapabilities.PsDefaultHwPeak(protocol)` returning the
   matrix value, with HL2 hard-overridden to `0.233`.
2. Wire into PS init so non-HL2 boards get sensible defaults instead of
   inheriting whatever Zeus uses today.
3. Resolve the `TODO(ps-p1)` Protocol-1 PS path (currently deferred —
   audit §5).

**Risk:** medium-high. Operator-felt default.
**Effort:** half-day + maintainer review.
**Gated on:** Phase 4 (enum unification) so the lookup is unambiguous.

---

## Recommended sequencing

| Order | Phase | Type | When |
|---|---|---|---|
| 1 | Phase 1 — verification tests | 🟢 | next session |
| 2 | Phase 2 — `BoardCapabilities` | 🟢 | next session, same PR or split |
| 3 | Phase 3 — 8000D override | 🟡 | after maintainer aligns on UI placement + variant naming |
| 4 | Phase 4 — enum unification | 🔴 | after maintainer aligns on naming (0x00 / 0x02) |
| 5 | Phase 6 — PS hw_peak | 🔴 | after Phase 4 |
| 6 | Phase 5 — UI conditional rendering | 🔴 | after Phase 2 + maintainer design review |

**Agent-autonomous chunk:** Phases 1 + 2 (~half-day total). Everything
beyond needs at least one maintainer alignment beat first.

---

## Open questions for the maintainer

1. **0x0A variant default.** Stay on G2, or switch to "Unspecified"
   forcing the operator to choose on first connect? (Recommend stay-on-G2.)
2. **0x00 / 0x02 canonical naming.** `Metis` or `Atlas`? `Hermes2`,
   `Griffin`, or `HermesII`?
3. **Persisted state migration.** Acceptance that the byte values in
   `pa_settings` / `preferred_radio` rows stay the same — no rewrite —
   is a soft compatibility guarantee?
4. **Phase 5 panel layout.** Conditional panels that today are always
   shown will start hiding on the wrong board class. Comfortable with
   that, or want a "show all" override for testing?
5. **Phase 6 PS scope.** Resolve the `TODO(ps-p1)` Protocol-1 PS path as
   part of Phase 6, or keep it deferred?
