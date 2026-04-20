# Lessons learned

Durable engineering notes for anyone working on Nereus. Unlike `docs/prd/` (product specs) and `docs/rca/` (per-incident post-mortems), this folder captures **load-bearing invariants** that aren't self-evident from the code — the kind of thing that would cost a future developer days if they tripped over it cold.

Add a lesson when:
- An RCA surfaced a gotcha that's specific to a library/API and could easily recur.
- A convention (color, port, protocol field, state flow) was chosen deliberately and deviating would break something.
- The rationale for a design choice lives in external reference code (Thetis) that a reader wouldn't know to look at.

| # | Topic | Covers |
|---|---|---|
| [wdsp-init-gotchas](wdsp-init-gotchas.md) | WDSP channel / meter init ordering | `OpenChannel` state flag, `SetChannelState` transition semantics, the `-400` meter sentinel, `ch[]` global lifecycle |
| [dev-conventions](dev-conventions.md) | Running Nereus locally | Port allocations, Chrome's getUserMedia on LAN IP, Vite dev vs static-served, panadapter amber color |
