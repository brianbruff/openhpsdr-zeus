# Zeus — Project Context for AI Agents

## Project Goal

Zeus is a cross-platform, web-frontend HPSDR client for original-protocol (Protocol 1) radios — Hermes, Mercury/Penelope/Metis, ANAN-class boards, and similar. It replaces the Windows-only **Thetis** client with a .NET 8 backend (`Zeus.Server`) and a Vite + React frontend, keeping **WDSP** as the DSP engine via P/Invoke.

**Reference implementation:** Thetis (C# / WinForms). This is the *sole* authoritative source for protocol and DSP behavior.

## Autonomous-Agent Boundaries

AI agents opening PRs against this repo may autonomously fix:

**Green-light (just do it, open a PR):**
- **Bugs with a clear root cause** — null refs, missing guards, off-by-one, persistence/wiring bugs where the fix is obvious from the symptom
- **Build / CI fixes** — missing NuGet refs, csproj typos, dotnet version bumps, workflow YAML breakage, Vite / npm config fixes
- **Protocol / WDSP compliance fixes** — where the Zeus behavior diverges from Thetis and Thetis source confirms Zeus is wrong. *Exception:* if the fix changes a default that an operator will feel (TX power cap, filter bandwidth, AGC curve, meter scaling), that is red-light — see below.
- **Docs and lessons updates** — additions to `docs/lessons/`, `docs/rca/`, `README.md`. Renames and restructuring are red-light.

**Red-light (flag for maintainer review, do NOT merge without approval):**
- **Visual design** — colors, fonts, layout, spacing, typography. Zeus has a single-hue amber convention (`#FFA028` with varying alpha, no rainbow gradients). Do not propose palette changes.
- **UX behavior** — what a click/drag/scroll does, keyboard shortcuts, panadapter/waterfall axis direction, VFO tuning feel. "Wrong scroll direction" reports are almost always a missed waterfall horizontal-shift, not an axis bug — see `docs/lessons/`.
- **Architecture** — new threads, new dependencies, new NuGet/npm packages, changes to the Zeus.Contracts wire format, signal-routing restructures.
- **Default values** — anything an operator will notice on first connect: TX power, filter widths, AGC, meter calibration, default band/mode, color palette. One bug report is not evidence that the default is wrong for everyone.
- **Feature scope creep** — if the issue says "fix meter," fix the meter. Don't add a new meter, refactor the meter pipeline, or rename the meter types.

When uncertain, implement the minimal fix and note in the PR description that design decisions need maintainer review. The maintainer (Brian, EI6LF) is the sole authority on visual design, UX, and defaults.

## Load-Bearing Invariants

Before touching DSP, protocol, or layout code, skim these — they have bitten us before:

- **`docs/lessons/wdsp-init-gotchas.md`** — WDSP RXA channels MUST open at `state=0` and flip via `SetChannelState(id, 1, 0)` *after* the worker is live. A `-400` meter reading means the xmeter thread didn't run. This ordering is load-bearing; do not reorder init without reading the lesson.
- **`docs/lessons/dev-conventions.md`** — port allocation (backend **6060**, Vite dev **5173**), panadapter amber (`#FFA028`), getUserMedia on LAN IP quirks.
- **`docs/lessons/hl2-drive-byte-quantization.md`** — HL2 honours only the top 4 bits of the TX drive byte. Touching `RadioService.RecomputePaAndPush`, `ControlFrame.WriteUsbFrame`, or any PA-calibration code without reading this will produce a radio that silently makes 20% of rated power.
- **`docs/references/`** — vendor protocol PDFs + per-radio capability matrix. **If a board-specific doc exists (e.g. `docs/references/protocol-1/hermes-lite2-protocol.md`), read it before inferring behaviour from Thetis or piHPSDR.** The HL2 drive-byte quantisation bug cost two days because this folder wasn't consulted.
- **`docs/rca/`** — per-incident post-mortems. Read the relevant one before "fixing" a symptom that matches.

## Radio-Specific Behaviour — use the abstractions

Zeus supports several HPSDR radios on one codebase (Hermes, HL2, ANAN-10/100/100D/200D, Orion, G2 MkII). The same protocol wire format does NOT mean identical behaviour — different boards honour different fields, use different drive-byte resolutions, and publish different PA gains. **Go through the existing per-board abstractions. Do not hard-code board-agnostic math in the drive / PA / TX path.**

Extant seams:

- **`Zeus.Server/RadioDriveProfile.cs`** — `IRadioDriveProfile.EncodeDriveByte(...)`. HL2 quantises to its 4-bit drive register here; every other board uses the 8-bit default. **Add new board quirks by implementing `IRadioDriveProfile` and extending `RadioDriveProfiles.For(...)` — do not special-case inside `RecomputePaAndPush`.**
- **`Zeus.Server/PaDefaults.cs`** — per-board PA-gain and rated-watts seeds, dispatched on `HpsdrBoardKind`. New boards slot in at the `TableFor` switch.
- **`RadioService.ConnectedBoardKind`** — the authoritative "what am I talking to?" for everything downstream.

Anti-pattern to watch for (the one KB2UKA's PA-menu refactor fell into): adding a calibration or encoding step that's correct for the radio in front of you (e.g. ANAN G2) and untested on other boards. Drive / TX / PA changes must be sanity-checked against HL2 at minimum; if a change *can't* be tested on HL2 locally, flag it explicitly in the PR so the maintainer can bench-test before merge.

## Debugging Discipline

- **Log what's on the wire, not what you think is on the wire.** `Protocol1Client.TxLoopAsync` already prints `p1.tx.rate pkts=... drv=... peak=...` at 1 Hz during MOX/TUN; when TX power looks wrong, read that log before theorising. Two days of bandpass / amplitude / rate phantoms on the HL2 bug ended with one line showing `drv=48`, which was the whole answer.
- **Log boundary-call arguments before blaming library internals.** When something WDSP-shaped misbehaves, the first suspect is the values our C# P/Invoke passed in — not a WDSP bug. Log the inputs at the P/Invoke seam, then read the library source.
- **Verify against Thetis source, not docs — but read the board-specific reference doc FIRST.** Protocol 1 documentation is incomplete and occasionally wrong; Thetis is the ground truth for ANAN-class radios. For HL2, the truth lives in `docs/references/protocol-1/hermes-lite2-protocol.md` and in `mi0bot/openhpsdr-thetis` (the HL2-specific Thetis fork), NOT in `ramdor/Thetis`.
- **Don't flip axes unilaterally.** If a panadapter or waterfall "feels backwards," investigate the horizontal-shift path before inverting frequency direction.

## Build & Run

Backend + frontend run independently during dev:

```bash
# backend (listens on :6060)
dotnet run --project Zeus.Server

# frontend (Vite dev server on :5173, proxies /api and /hub to :6060)
npm --prefix Zeus.Web run dev
```

Full details, dependency list, and native WDSP build in `README.md` and `native/`. Do not duplicate them here.

## Commits & PRs

- **Never mention Anthropic or Claude** in commit messages. This is a hard rule — see `/Users/bek/CLAUDE.md`.
- Conventional prefixes (`feat:`, `fix:`, `docs:`, `refactor:`) are preferred but not strictly enforced; match the style of recent `git log` output.
- Ensure the solution builds (`dotnet build Zeus.slnx`) and any existing tests pass before opening a PR.
- For worktrees, use the sibling layout `OPENHPSDR-Nereus.Worktrees/<branch_with_underscores>/`.

## Architecture Snapshot

- **`Zeus.Contracts`** — wire format shared between server and web (frames, DTOs, enums). Changes here are red-light.
- **`Zeus.Protocol1`** — HPSDR original-protocol UDP client, discovery, packet parsing, TX IQ ring.
- **`Zeus.Dsp`** — DSP engine abstraction (`IDspEngine`), synthetic and WDSP implementations, TX-stage meters.
- **`Zeus.Server`** — ASP.NET host, SignalR `StreamingHub`, radio / DSP / TX pipeline services, discovery.
- **`Zeus.Web`** (frontend) — Vite + React, connects to the hub, renders panadapter/waterfall/VFO/meters.

When in doubt about where code belongs, match the existing project's single responsibility rather than introducing a new one.
