# Lessons learned

Durable engineering notes for anyone working on Zeus. Unlike `docs/prd/` (product specs) and `docs/rca/` (per-incident post-mortems), this folder captures **load-bearing invariants** that aren't self-evident from the code — the kind of thing that would cost a future developer days if they tripped over it cold.

Add a lesson when:
- An RCA surfaced a gotcha that's specific to a library/API and could easily recur.
- A convention (color, port, protocol field, state flow) was chosen deliberately and deviating would break something.
- The rationale for a design choice lives in external reference code (Thetis) that a reader wouldn't know to look at.

| # | Topic | Covers |
|---|---|---|
| [wdsp-init-gotchas](wdsp-init-gotchas.md) | WDSP channel / meter init ordering | `OpenChannel` state flag, `SetChannelState` transition semantics, the `-400` meter sentinel, `ch[]` global lifecycle |
| [dev-conventions](dev-conventions.md) | Running Zeus locally | Port allocations, Chrome's getUserMedia on LAN IP, Vite dev vs static-served, panadapter amber color |
| [hl2-drive-byte-quantization](hl2-drive-byte-quantization.md) | HL2 TX power capped at 1–2 W | HL2 honours only the top 4 bits of the drive byte; piHPSDR's 40.5 dB calibration rounds drive to nibble 0x3; how to recognise, calibrate, and eventually fix at the `ComputeDriveByte` seam |
| [tx-chain-staging](tx-chain-staging.md) | Browser mic → WDSP TXA → HL2 gain staging | Why the mic-gain slider needs the negative half (Thetis range −40..+10 dB), how to read the TX stage meters to spot float-1.0 IQ rail clipping vs healthy ALC, and the open question about Thetis A/B for rated power on continuous tones |
| [hl2-multi-rx](hl2-multi-rx.md) | HL2 has two mutually-exclusive multi-DDC layouts | The PS 4-DDC layout (DDC2/3 = feedback, NCO=TX) and the multi-slice 1..4-DDC layout (DDC0..N-1 = user RX) share the same wire bits but differ in NCO assignment; Phase 1 refuses to combine them, and Phase 2 PS+multi-RX has to reserve DDC2/3 for PS or `calcc` will never converge |
