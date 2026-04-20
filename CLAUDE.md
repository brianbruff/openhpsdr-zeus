# Zeus — Project Context for AI Agents

## Project Goal

Zeus is a cross-platform, web-frontend HPSDR client for original-protocol (Protocol 1) radios — Hermes, Mercury/Penelope/Metis, ANAN-class boards, and similar. It replaces the Windows-only **Thetis** client with a .NET 8 backend (`Zeus.Server`) and a Vite + React frontend, keeping **WDSP** as the DSP engine via P/Invoke.

**Reference implementation:** Thetis (C# / WinForms). This is the *sole* authoritative source for protocol and DSP behavior. **Do not cite `deskhpsdr`** — different language, different bindings, different defaults.

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

When uncertain, implement the minimal fix and note in the PR description that design decisions need maintainer review. The maintainer (Brian, EI4HQ) is the sole authority on visual design, UX, and defaults.

## Load-Bearing Invariants

Before touching DSP, protocol, or layout code, skim these — they have bitten us before:

- **`docs/lessons/wdsp-init-gotchas.md`** — WDSP RXA channels MUST open at `state=0` and flip via `SetChannelState(id, 1, 0)` *after* the worker is live. A `-400` meter reading means the xmeter thread didn't run. This ordering is load-bearing; do not reorder init without reading the lesson.
- **`docs/lessons/dev-conventions.md`** — port allocation (backend **6060**, Vite dev **5173**), panadapter amber (`#FFA028`), getUserMedia on LAN IP quirks.
- **`docs/rca/`** — per-incident post-mortems. Read the relevant one before "fixing" a symptom that matches.

## Debugging Discipline

- **Log boundary-call arguments before blaming library internals.** When something WDSP-shaped misbehaves, the first suspect is the values our C# P/Invoke passed in — not a WDSP bug. Log the inputs at the P/Invoke seam, then read the library source.
- **Verify against Thetis source, not docs.** Protocol 1 documentation is incomplete and occasionally wrong; Thetis is the ground truth.
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
