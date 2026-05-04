# HL2 PureSignal — `hw_peak` must match the observed TX envelope

Load-bearing invariant for anyone debugging "PS arms but never converges
on HL2". Saves a session of chasing wire-level red herrings (we tried
that — see git history).

## TL;DR

**WDSP `calcc` collects TX-envelope samples into 16 amplitude bins and
will not transition `LCOLLECT → LCALC` until every bin has `spi`
samples.** If the operator's actual TX envelope peak is below the
configured `hw_peak`, the upper bins never fill, calcc loops in
COLLECT, hits its 4-second timeout, resets, and loops forever. PS
arms, the panel reports `Cal state COLLECT`, `Feedback bar 0/256`,
`Correction —`, and nothing improves no matter how long you transmit.

The fix is **operator-tuned `hw_peak`** matched to the observed envelope
peak — same as mi0bot's Thetis. The PURESIGNAL panel surfaces the live
peak as **Observed peak** (mi0bot's `txtGetPSpeak`); the operator dials
**HW peak** to match it.

## Symptom signature in `bt*.output` backend log

A stalled run looks like this:

    wdsp.setPsEnabled enabled=True
    wdsp.psState 255->1   info4=0 info6=0x0000 info13=0 info14=0
    wdsp.psState 1->2     info4=0 info6=0x0000 info13=0 info14=0
    wdsp.psState 2->4     info4=0 info6=0x0000 info13=0 info14=0
    p1.ps.fb DDC2(rx) peak=0.0598 mean=0.0239 | DDC3(tx) peak=0.1907 ...
    p1.ps.fb DDC2(rx) peak=0.0601 mean=0.0229 | DDC3(tx) peak=0.1907 ...
    [...20+ seconds of feedback samples flowing, state stays at 4...]
    wdsp.psState 4->1     info4=0 info6=0x0000 info13=0 info14=0   ← MOX dropped

State 4 = `LCOLLECT`. State **never reaches 6 (`LCALC`)**, `info[4]`
never leaves 0, `info[14]` never flips to 1. PS-feedback samples *are*
flowing (DDC2 = post-coupler feedback, DDC3 = TX exciter reference) —
the wire path is healthy. The problem is purely the WDSP-internal
binning gate.

A converging run looks like this:

    wdsp.setPsEnabled enabled=True
    wdsp.psState 255->1
    wdsp.psState 1->2
    wdsp.psState 2->4
    wdsp.psState 4->6  info4=80 info6=0x0000 info13=0 info14=1   ← LCALC fires
    wdsp.psState 6->4  info4=80 info6=0x0000 info13=0 info14=1
    wdsp.psState 4->7  info4=80 info6=0x0000 info13=0 info14=1   ← LDELAY
    wdsp.psState 7->4  info4=80 info6=0x0000 info13=0 info14=1
    [...continuous cycling, info[14] latched to 1 = CorrectionsBeingApplied...]

When state is cycling 3↔4↔5↔6↔7 with `info14=1`, panel shows
`Cal state COLLECT · correcting` and `Correction` reads a real dB value.

## Why `0.233` is the HL2 default — and when to override

Per `clsHardwareSpecific.cs:311-312` in mi0bot:

```csharp
case HPSDRHW.HermesLite:
    return 0.233;
```

The 0.233 value is a **blanket per-board default**, not a per-drive-level
calibration. mi0bot expects the operator to override `_PShwpeak` based on
their actual TX setup — `pbWarningSetPk.Visible = _PShwpeak !=
HardwareSpecific.PSDefaultPeak` (`PSForm.cs:830`) shows a warning
indicator whenever the field deviates from the hardware default.

Empirical numbers from the bench HL2 + internal coupler at 28.400 MHz:

| Drive % | Mag (2-Tone) | Observed peak | Right `hw_peak` |
|---|---|---|---|
| 21 | 0.4 | 0.190 – 0.225 | 0.18 – 0.22 |

At drive=21% the actual envelope peak is **below the 0.233 default**
→ bin 15 (0.94..1.0 normalized) never fills → calcc stalls. Setting
`hw_peak ≈ 0.18` (matched to the observed peak, slightly below) fills
all bins → calcc cycles → Correction settles around −10 dB → IMD3 drops
~20 dBc below the main tones in the panadapter.

Higher drive levels eventually push the envelope past 0.233 on their
own, at which point the default works. There is **no auto-calibrate
button** in mi0bot or Zeus — the workflow is intentionally manual.

## Operator workflow (mi0bot-faithful)

1. Open Settings → PURESIGNAL.
2. Arm PS, arm 2-Tone (or any known excitation), hold ~5 seconds.
3. Note the live **Observed peak** value.
4. Type that value (or just below) into the **HW peak** field, Tab to
   commit.
5. PS converges within 1–2 seconds — `Cal state COLLECT · correcting`,
   `Correction` shows a real dB number.
6. (Optional) Increase drive — once envelope peak rises past the new
   `hw_peak`, raise `hw_peak` to match for the best bin coverage.

## What this lesson is NOT about

- **The wire format.** HL2 PS feedback is delivered through the existing
  EP6 stream via the dedicated ADC1 mapping (`docs/references/protocol-1/
  hermes-lite2-protocol.md`, "PureSignal feedback path" section). The wire
  path was already working before this lesson was written; do not modify
  `Zeus.Protocol1/ControlFrame.cs` PS-related encoders without reading
  that doc and the associated mi0bot `networkproto1.c` source first. We
  burned hours retrying TX-side `0x0a` C4 overrides before realising the
  problem was purely the WDSP binning gate.
- **The HL2 default `hw_peak`.** Stays at `0.233` to match mi0bot
  exactly. Do not lower it on the assumption "low drive is the common
  case" — it would silently hide PS misconfiguration from operators who
  push the radio harder.
- **`PsAutoAttenuateService`.** Currently P2-only (gates on `p2-null`
  during MOX on HL2). Wiring it for P1 + HL2 is a separate, larger
  engineering task involving a TX-side step-attenuator wire path on
  register `0x0a` C4 during XmitBit (see mi0bot `networkproto1.c:1086`
  and `console.cs:10947-10948`). Not addressed here.

## Authoritative sources

- mi0bot WDSP binning gate:
  `Project Files/Source/wdsp/calcc.c:708, 729-748, 762-771`
- mi0bot observed-peak read pattern:
  `Project Files/Source/Console/PSForm.cs:624` — `GetPSMaxTX(_txachannel, ptr)`
- mi0bot HL2 default peak:
  `Project Files/Source/Console/clsHardwareSpecific.cs:311-312`
- mi0bot warning-indicator pattern (not yet ported):
  `Project Files/Source/Console/PSForm.cs:830`
- mi0bot auto-attenuate target (152.293):
  `Project Files/Source/Console/PSForm.cs:747`
