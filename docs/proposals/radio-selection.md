# Proposal: Radio selection — discovery plus manual endpoint entry

**Status:** Draft — awaiting maintainer review
**Author:** AI agent survey, for Brian (EI6LF) review
**Scope:** Extend the startup connect dialog so operators can connect to a radio by manually entering IP, port, and protocol, while keeping LAN auto-discovery as the default. Persist manual endpoints and the last-used choice so reconnects on a known radio are one click.

---

## 1. Background: what Zeus ships today

The startup flow is entirely discovery-driven. When a fresh browser session opens Zeus with no radio connected, `ConnectPanel` (`zeus-web/src/components/ConnectPanel.tsx`) polls `GET /api/radios` every 10 s and renders a list of radios that answered a UDP broadcast on port 1024. The operator clicks **Connect** on a row, the frontend picks `POST /api/connect` for P1 or `POST /api/connect/p2` for P2 based on the discovery `details.protocol` chip, and the session begins.

Key facts:

- **Discovery** — `Zeus.Protocol1/Discovery/RadioDiscoveryService.cs`: UDP broadcast to :1024, 63-byte probe header `0xEF 0xFE 0x02`, 1500 ms timeout, sent 3× with 50 ms gap on macOS. Deduped by MAC. Result mapped to `RadioInfo` with `Details["protocol"]` = `"P1"` or `"P2"`.
- **Connect request** — `Zeus.Contracts.Dtos.ConnectRequest { Endpoint, SampleRate, PreampOn?, Atten? }`. `Endpoint` is a string; `RadioService.TryParseEndpoint` already accepts either `"192.168.1.20"` (defaults port to 1024) or `"192.168.1.20:1024"`.
- **Protocol selection** — auto-detected from discovery metadata. The backend has two REST routes; the frontend chooses based on the discovered chip. There is no user-visible protocol control.
- **Persistence** — client settings live in a Zustand store at `zeus-web/src/state/tx-store.ts` using the `persist` middleware (`localStorage['zeus-tx']`), currently holding `drivePercent`, `micGainDb`, `levelerMaxGainDb`. `ConnectPanel` tracks a volatile `lastConnectedEndpoint` to float the previous radio to the top — this is **not** persisted across page reloads.
- **Protocol 2** — `Zeus.Protocol2/` is implemented but **experimental and RX-only**. Sample rate is forced to 48 kHz server-side; TX is stubbed.

### 1.1 Gaps this proposal closes

1. **Radios on a different subnet** — broadcast discovery only reaches the local broadcast domain. An operator whose radio sits behind a managed switch, VLAN, VPN, or static route cannot connect today.
2. **Radios that don't answer discovery during a session** — HL2 power-cycle scenarios, long ARP TTLs, and the class of symptoms documented in `memory/project_resume_radio_ehostunreach.md`. The operator knows the IP, but the only way in is to wait for discovery to re-advertise.
3. **Non-default ports** — Thetis and some gateware builds accept non-1024 data ports. No UI way to pick one today.
4. **Reconnect friction** — `lastConnectedEndpoint` is in-memory. A page reload forces rediscovery; if discovery misses, there's no fallback path.
5. **Protocol override** — a P1-capable board that also advertises P2 is auto-routed by discovery metadata. An operator who wants to force P1 (fully featured) over an experimental P2 advertisement has no way to do so.

## 2. Non-goals

- **Not a replacement for discovery.** Discovery remains the default path and stays visually primary on the dialog.
- **Not a network diagnostic tool.** No ping, no ARP probe, no port scan. If the endpoint doesn't respond, the backend's existing connect error surfaces verbatim.
- **Not a multi-radio session manager.** One active radio at a time, as today. The persisted list is a *convenience* for reconnecting, not a roster.
- **Not a change to wire format.** `ConnectRequest` already carries everything we need. No `Zeus.Contracts` churn.

## 3. Proposed design

### 3.1 UI — `ConnectPanel` gets a second mode

The existing DISCOVER RADIO panel gains a segmented control (or equivalent single-hue amber toggle) at the top:

```
[ DISCOVER ]   [ MANUAL ]
```

**Discover mode** (default): unchanged from today. Scanning banner, discovered-radios list, last-connected floated to top, per-row Connect button.

**Manual mode**: a small form with

- **IP address** — text input, IPv4 validated client-side (`/^(\d{1,3}\.){3}\d{1,3}$/`), server-side `TryParseEndpoint` is the authoritative validator.
- **Port** — number input, default `1024`, range 1–65535.
- **Protocol** — radio group or select: `Protocol 1` (default) / `Protocol 2 (experimental, RX only)`. The P2 option is labelled with the same warning string `ConnectPanel` uses today.
- **Sample rate** — select: `48 / 96 / 192 / 384 kHz`, default `192 kHz`. Greyed out and forced to `48 kHz` when Protocol 2 is selected (mirrors the server-side forcing in `Program.cs:300`).
- **Save for next time** — checkbox, default on. Controls whether the entry is written to the persisted list on successful connect.
- **Connect** — primary button. Calls `POST /api/connect` or `/api/connect/p2` with `Endpoint = "<ip>:<port>"`.

Below the form, a **Saved endpoints** list shows previously-saved manual entries with one-click reconnect and a small ✕ to remove. Each row shows `label (or IP:port)`, protocol badge, and last-used timestamp.

**Which mode is shown on open?**

- First ever visit: **Discover**.
- Subsequent visits: whichever mode the operator last used.

### 3.2 Frontend state — extend `tx-store` (or split)

Add a persisted slice — either extend `useTxStore` or introduce `useConnectStore` at `zeus-web/src/state/connect-store.ts`. A separate store is cleaner since the concerns don't overlap (TX audio vs. connection endpoints).

```typescript
// zeus-web/src/state/connect-store.ts
export type ProtocolChoice = 'P1' | 'P2';

export interface SavedEndpoint {
  id: string;                 // uuid
  label?: string;             // optional friendly name
  ip: string;                 // "192.168.1.20"
  port: number;               // 1024
  protocol: ProtocolChoice;
  sampleRate: 48000 | 96000 | 192000 | 384000;
  lastUsedUtc: string;        // ISO-8601
}

interface ConnectState {
  mode: 'discover' | 'manual';
  savedEndpoints: SavedEndpoint[];
  lastConnectedId?: string;   // into savedEndpoints, survives reloads
  // actions
  setMode: (m: 'discover' | 'manual') => void;
  saveEndpoint: (e: Omit<SavedEndpoint, 'id' | 'lastUsedUtc'>) => string;
  removeEndpoint: (id: string) => void;
  touchEndpoint: (id: string) => void;
}
```

Persisted via Zustand's `persist` middleware into `localStorage['zeus-connect']`, matching the convention established by `zeus-tx`. `partialize` keeps only the four fields — `mode`, `savedEndpoints`, `lastConnectedId`, and `manualFormDefaults` (see §3.4).

### 3.3 Backend — no changes required

`ConnectRequest`, `POST /api/connect`, and `POST /api/connect/p2` already accept an arbitrary endpoint string. `RadioService.TryParseEndpoint` already handles `"IP"` and `"IP:port"`. Protocol is dispatched purely by which REST route the frontend calls.

This means the PR is frontend-only unless we also decide to persist the endpoint list server-side (see §6 item 3).

### 3.4 Form defaults and "last used"

When the operator switches to Manual mode, the form is prefilled from the most-recently-used saved endpoint, falling back to `{ ip: '', port: 1024, protocol: 'P1', sampleRate: 192000 }`. After a successful connect with **Save for next time** on:

1. If an endpoint with the same `(ip, port, protocol)` exists, update its `sampleRate`, `label`, `lastUsedUtc` — do not duplicate.
2. Otherwise append a new entry.
3. Set `lastConnectedId` to that entry.

Sort order in the **Saved endpoints** list: `lastUsedUtc` descending. Mirrors the "LAST" float behaviour of the discover list.

### 3.5 Error surfacing

Connect failures already return structured errors from the server (`RadioService.ConnectAsync` wraps parse and socket failures as `400`/`500`). The manual form renders the error message inline beneath the Connect button in the same amber-on-dark style as other form errors. No new error vocabulary.

Specific cases worth confirming land cleanly in the UI without code changes:

- Malformed IP → `TryParseEndpoint` → `"Invalid endpoint 'x.y.z'"`.
- Right format but no host → socket timeout → existing connect timeout error.
- Right host, wrong protocol (P2 selected against P1-only gateware) → probably a silent stall at today's timeout. Worth a manual-smoke test during review; see §5.

## 4. Load-bearing invariants

Per `CLAUDE.md` and the conventions in `docs/lessons/`:

- **Single-hue amber** (`#FFA028` with alpha). The new segmented control, saved-endpoints rows, and form inputs all use existing panel styling. No new colours, no new iconography palette. See `docs/lessons/dev-conventions.md`.
- **No visual-design or UX default changes** beyond what the feature itself introduces. The discover panel's layout, scan cadence (10 s), and row format stay exactly as they are.
- **Backend wire format is off-limits.** No fields added to `ConnectRequest`, no new hub methods, no new DTOs in `Zeus.Contracts` unless §6 item 3 is approved.
- **LocalStorage schema under `zeus-connect`** is new but additive — no migration concerns.
- **Never log stored endpoints** in a way that leaks to server traces. They are an LAN detail, not a secret, but the QRZ lesson applies: no raw request/response bodies of stored state written to console or server logs.
- **P2 still forced to 48 kHz.** The UI disables the sample-rate select when P2 is chosen; it does not attempt to negotiate P2 at other rates. The server-side forcing in `Program.cs` remains the authoritative guard.

## 5. Testing

- **Unit (frontend)** — `connect-store.test.ts`: saveEndpoint dedupes on `(ip, port, protocol)`, touchEndpoint updates `lastUsedUtc`, `partialize` round-trips through `JSON.stringify`.
- **Component (frontend)** — `ConnectPanel.test.tsx`: mode toggle, form validation, disabled state when P2 is selected, Saved endpoints list rendering and one-click reconnect.
- **Manual smoke** —
  1. Manual connect to a live radio on the same subnet. Verify P1, 192 kHz path matches behaviour of discover-click.
  2. Manual connect with wrong IP → error surfaces in form.
  3. Manual connect to a P2-capable board with P2 selected, then disconnect and reconnect with P1 selected. Confirm both paths work and the saved-endpoint list has one entry per `(ip, port, protocol)` tuple.
  4. Reload the page. Saved endpoints persist, last-used is pre-filled, last-used mode is restored.
  5. Discovery still fires while Manual mode is active (or is paused — see §6 item 2).
- **No backend test changes.** The endpoint-parsing path has coverage already.

## 6. Items requiring maintainer decision (red-light per `CLAUDE.md`)

Per the autonomous-agent boundaries, these are not autonomously resolvable:

1. **UX: tab toggle vs. expander.** Segmented `[DISCOVER] [MANUAL]` is the proposal. Alternatives: Manual as a collapsed "Advanced…" disclosure under the discover list; Manual as a separate modal. Maintainer call — any of these are cheap to implement.
2. **Discover polling while Manual is active.** Keep polling in the background (so the user can toggle back and see fresh results), or pause polling and resume on toggle? Proposal: keep polling — it's already a 10 s cadence and the bandwidth is negligible.
3. **Where saved endpoints live — client-side `localStorage` or server-side LiteDB?** Proposal: `localStorage`, matching `zeus-tx`. Server-side (via existing LiteDB used for QRZ/band memory) would let the same list appear on any browser pointed at a given backend — an operator preference, consistent with how band memory already behaves. This is a scope decision; either is defensible.
4. **Default protocol when the operator has never used Manual.** Proposal: `Protocol 1`. P2 is experimental and RX-only; making it the default would surprise most users.
5. **Default port value.** Proposal: `1024`. Any known deployments using a different data port that should be the default instead?
6. **Friendly labels** — is the `label?` field worth the UI complexity on day one, or defer until operators ask? Proposal: include it as optional, no placeholder — low cost, nice-to-have.
7. **"Forget on disconnect" semantics.** If an operator unticks **Save for next time** and connects anyway, the endpoint is used once and not written. Confirm this matches intent, vs. always remembering the last-successful endpoint regardless of the checkbox.
8. **Input validation strictness.** Strict IPv4 only, or accept hostnames too (e.g. `hermes.local`)? Backend `IPEndPoint.Parse` path doesn't resolve hostnames, so allowing them would need server-side DNS resolution — this is a scope expansion and probably a separate proposal.

## 7. Phased plan

| Phase | Scope | Rough effort |
|---|---|---|
| **1** | `connect-store` with persistence. Segmented toggle in `ConnectPanel`. Manual form with validation. Connect path wired to existing REST endpoints. No saved-endpoints list yet. | ~2 days |
| **2** | Saved endpoints list: render, reconnect, remove, dedupe, `lastUsedUtc` sort. Last-used-mode restore on page load. | ~2 days |
| **3** | Polish: labels, keyboard focus order, error styling, manual smoke tests on real hardware. | ~1 day |
| **4 (deferred)** | Server-side sync of saved endpoints (if §6 item 3 goes that way) — new LiteDB collection, `GET/PUT /api/connect/endpoints`, migration from localStorage. | separate proposal |

## 8. Why an issue *and* this doc

The issue tracks the user-visible ask and discussion. This doc is the versioned record that PRs can reference. Phase 1 PR description points back here; any deviation from §3 design either updates this doc or explains in the PR why it diverged.
