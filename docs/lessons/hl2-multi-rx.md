# HL2 multi-RX — two different DDC layouts share one wire format

## The trap

The Hermes Lite 2 has **up to 4 DDCs** but Zeus uses them in two
mutually-exclusive ways, and a future "Phase 2" PureSignal-plus-multi-RX
attempt will silently break PS if it doesn't preserve this distinction:

1. **PS 4-DDC layout** (`PsEnabled && Mox`, set by
   `Protocol1Client.TxLoopAsync`):

   - DDC0 → RX1 audio (operator's listening freq, stays alive during TX)
   - DDC1 → RX2 (or unused)
   - DDC2 → **PS RX feedback** (post-PA coupler tap, NCO pinned to TX freq) — pscc `rx`
   - DDC3 → **PS TX reference** (TX-DAC loopback, NCO pinned to TX freq) — pscc `tx`

   This is the layout WDSP `calcc` (PureSignal calibration) needs to converge.
   DDC2 and DDC3 are **not** user receivers; their NCOs follow the *transmit*
   frequency, not whatever VFO the operator wants for those slices.

2. **Multi-slice layout** (`!PsEnabled && multiSliceEnabled`, set in the
   same packet build):

   - DDC0..DDC{N-1} → user RX 0..N-1, NCOs at the per-slice VFO
     (`_vfoAHz`, `_vfoBHz`, `_vfoCHz`, `_vfoDHz`)
   - N is clamped 1..4 in `RadioService.SetMultiSlice`

   This is what the operator-facing multi-panadapter UI drives.

Both layouts write `(nddc - 1)` into the same bits — C0=0x00 / C4 [5:3] —
and the EP6 IQ frame slot layout `(6N + 2)` bytes per slot is identical.
The host-side meaning of DDC2 and DDC3 is the only thing that differs, and
that meaning lives in **how the NCO frequencies are assigned**, not in any
wire bit.

## What Phase 1 does

`RadioService.SetMultiSlice` refuses to enable multi-slice while PS is
armed, logs a warning, and returns `Enabled=false` on the response. So
Phase 1 never has to merge the two layouts — only one is ever in flight.

`Protocol1Client.TxLoopAsync` enforces the same precedence on the wire
side: if `psOn && isHl2 && moxOn`, the layout is forced to the 4-DDC PS
shape regardless of any multi-slice flags on `_multiSliceEnabled`. This
is the belt-and-braces second check; the first is the policy gate above.

The single-DDC path (neither PS nor multi-slice) stays bit-exact identical
to v0.6.x, which is why the Phase 1 PR can claim "no behavioural change
for operators who don't toggle multi-slice on."

## What Phase 2 will need to be careful about

When somebody writes "PS + multi-RX coexistence" the right model is:

- Reserve DDC2 and DDC3 for PS as **feedback channels with NCO=TX freq**.
- Allocate user-RX slices on DDC0 and DDC1 only.
- Effective `MaxReceivers` while PS is armed is therefore **2**, not 4.
- The Add-Panel modal and the multi-slice control should clamp to this
  reduced ceiling whenever PS is armed.

If you instead write the obvious-but-wrong code that allocates user RX
on DDC2 (which is what the multi-slice layout does today), PS feedback
samples and operator-RX IQ stream will overlap on the same DDC and `calcc`
will never converge. The convergence failure looks like the PURESIGNAL
panel sticking in COLLECT forever — which is the same symptom as the HL2
hw_peak miscalibration trap (`hl2-ps-hwpeak-calibration`), so be careful
to rule out the layout mismatch before chasing hw_peak.

## References

- `Zeus.Protocol1/Protocol1Client.cs:204-340` — PS 4-DDC layout and pscc
  feedback dispatch.
- `Zeus.Protocol1/Protocol1Client.cs:715-770` — the precedence check in
  `TxLoopAsync` that picks PS over multi-slice.
- `Zeus.Protocol1/PacketParser.cs:467-526` — EP6 slot layout
  `(6N + 2)` bytes per slot, common to both layouts.
- `Zeus.Server.Hosting/RadioService.cs:1330-1363` — `SetMultiSlice`
  policy gate (refuses to enable while PS is armed).
- `docs/references/protocol-1/hermes-lite2-protocol.md:478-485` —
  C0=0x00 / C4 [5:3] wire bits for the DDC count.
- `docs/lessons/hl2-ps-hwpeak-calibration.md` — the convergence-failure
  symptom that you should rule out *before* chasing this layout-mismatch
  trap (and vice versa).
