# Radio Discovery — Current Approach & Push-Based Alternative

Status: **decision recorded — keep current polling approach.** This doc captures
why, so the question doesn't get re-litigated every six months.

## Current implementation

Discovery is **stateless HTTP polling**, fresh UDP broadcast per request.

### Frontend
- `zeus-web/src/components/ConnectPanel.tsx:197` — `setInterval` calls
  `fetchRadios()` every **10 s** while disconnected (`DISCOVERY_INTERVAL_MS`).
- Polling stops on connect; resumes on disconnect.
- `zeus-web/src/lib/client.ts:565` — `fetchRadios()` is a plain `GET /api/radios`.

### Backend
- `Zeus.Server.Hosting/ZeusEndpoints.cs:63-121` — each `GET /api/radios`:
  1. Spawns P1 + P2 discovery tasks in parallel, 1500 ms timeout each.
  2. P1 (`Zeus.Protocol1/Discovery/RadioDiscoveryService.cs:68-146`) and P2
     (`Zeus.Protocol2/Discovery/RadioDiscoveryService.cs:68-148`) each send 3
     probes 50 ms apart on every active interface's broadcast address.
  3. Listens for replies, dedupes by MAC, returns combined sorted list.
- **No background daemon. No cache. No state.** Each call is a fresh broadcast.

### SignalR (`StreamingHub`)
Already carries DSP/audio frames, meters, alerts, wisdom status, band-plan
events, VST host events. **No discovery events today** — discovery never
touches the hub.

## The push-based alternative we considered

Move discovery to a long-running `IHostedService` that broadcasts results via
the existing `StreamingHub`:

1. Add `OnDiscoveryUpdate(RadioInfoDto[])` to the hub.
2. `DiscoveryDaemonService` runs P1+P2 broadcast every 5–10 s **only while
   at least one client has the Connect screen open** (otherwise it floods the
   LAN forever).
3. Frontend subscribes to the hub event; `/api/radios` becomes a one-shot for
   first paint and a manual "Rescan" button.

## Why we are NOT doing this

### Real but marginal wins
- First-paint latency drops from ≤10 s → ~5 s on the Connect screen. Saves a
  few seconds, once per session.
- Busy-flag changes (another Zeus grabbing the radio) propagate ~5 s sooner.
- One fewer `setInterval` to manage in React.

### Wins that *sound* good but aren't real
- **"Less network traffic"** — false. A 5 s daemon tick **increases** total
  UDP broadcasts versus a 10 s frontend poll. Polling wins on bandwidth.
- **"More modern architecture"** — aesthetic, not functional. The hub is
  already there; this just routes one more event through it.
- **"Detects radio dropping mid-session"** — discovery does not run while
  connected. Mid-session drops are a *connection-health* problem (RX-timeout
  watchdog on the protocol client), not a discovery problem. Push-based
  discovery does not fix it.

### Costs
- New `IHostedService` with lifecycle gating (must scan only while a client
  is on the Connect screen, else continuous broadcast).
- New hub event, client subscription, reconnect handling.
- New failure modes — daemon stuck, broadcast lost, frontend missed the
  update — all of which the dumb poll handles by definition (next tick
  re-resolves).
- Architecture change → red-light per `CLAUDE.md` (new hosted service,
  new hub contract, maintainer review required).
- **Risk to the HL2 power-cycle lesson.** The "radio went silent → cycle
  power" workflow depends on observing *absence*. Any caching or diff
  suppression could mask the very signal operators rely on. See
  `project_hl2_discovery_powercycle` in maintainer auto-memory.

## Decision

**Keep the 10 s poll.** It is stateless, self-healing, cheap on a LAN, and
correct for the problem. The migration would not pay back its complexity
budget for a single-operator desktop app.

## Smaller wins worth considering instead

If discovery UX feels rough, these are cheaper and deliver more felt benefit
per line of code:

- **Server-side cache (~2 s TTL) on `/api/radios`** — eliminates the 1500 ms
  blocking broadcast on every poll without changing transport. Must be short
  enough to preserve the HL2 absence-detection signal.
- **"Rescan now" button** in `ConnectPanel.tsx` — one click triggers a fresh
  fetch instead of waiting up to 10 s for the next interval.
- **Mid-session drop detection** — separate problem; belongs in the P1/P2
  client RX watchdog, not discovery. Surface as a hub event when it lands.

## References

- `Zeus.Server.Hosting/ZeusEndpoints.cs:63-121` — discovery endpoint.
- `Zeus.Protocol1/Discovery/RadioDiscoveryService.cs` — P1 broadcast logic.
- `Zeus.Protocol2/Discovery/RadioDiscoveryService.cs` — P2 broadcast logic.
- `Zeus.Server.Hosting/StreamingHub.cs` — existing hub (no discovery events).
- `zeus-web/src/components/ConnectPanel.tsx:75-229` — frontend poll loop.
- `zeus-web/src/lib/client.ts:503,565` — `fetchRadios` + `normalizeRadios`.
