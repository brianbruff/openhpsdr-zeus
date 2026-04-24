# HL2 drive-byte quantisation — only the top 4 bits are honoured

Load-bearing invariant for anyone touching `RadioService.ComputeDriveByte`,
`ControlFrame.WriteUsbFrame`, or PA calibration on a Hermes-Lite 2. Reading
this before debugging "HL2 makes too little power" saves ~two days of
chasing amplitude / packet-rate / bandpass phantoms.

## TL;DR

The HL2 uses **only bits [31:28] of the TX drive-level register** — a 4-bit
scale, not the 8-bit scale every other HPSDR radio (Hermes, ANAN, Orion,
G2) exposes. If the computed drive byte is 48, the HL2 sees `0x3 / 0xF`
= 20% of max drive and produces ~20% of rated power, regardless of how
correct the rest of the TX chain is. piHPSDR's generic
`pa_calibration = 40.5` is tuned for 8-bit radios; on HL2 it lands the
drive byte in the 48–80 range forever, capping output at 1–2 W.

For this HL2 / filter-board combination, `paGainDb ≈ 26` pushes the
computed drive byte to 253 (upper nibble 0xF) and reaches rated output.
Per-unit calibration is still expected — 26 is a starting point, not a
law of physics.

## The symptom

- Zeus TX power topping out at 1–2 W on 20 m where the same HL2 +
  antenna produces 5–7 W on deskHPSDR / piHPSDR.
- Measured power scales as roughly `byte^0.86` against `driveByte` —
  not the `byte^2` a class-AB amplifier should produce. Evidence it's
  quantisation, not hardware compression.
- `p1.tx.rate` log (added to `Protocol1Client.TxLoopAsync`) shows
  `drv=48` even when `pa.recompute` reports `byte=48` as "correct" per
  the `ComputeDriveByte` formula with `gainDb=40.5, maxWatts=5, pct=100`.

## The cause

From `docs/references/protocol-1/hermes-lite2-protocol.md` line 51:

    | 0x09 | [31:24] | Hermes TX Drive Level (only [31:28] used)

`ComputeDriveByte` (`Zeus.Server/RadioService.cs`) does the piHPSDR /
Thetis math: target watts → dBm → subtract per-band PA gain → back to
volts across 50 Ω, normalised against 0.8 V full-scale, then
`byte = round(norm * 255)`. This produces one of 256 distinct values,
all of which Zeus faithfully writes to C1 of the DriveFilter C&C at
`ControlFrame.cs`. The HL2 then **ignores the bottom nibble** of that
byte before scaling the TXG stage.

Worked example at `gainDb=40.5, maxWatts=5, pct=100`:

    target  = 5 W
    source  = 5 / 10^(40.5/10) = 4.46e-4 W
    volts   = sqrt(4.46e-4 × 50) = 0.1493 V
    norm    = 0.1493 / 0.8 = 0.187
    byte    = round(0.187 × 255) = 48     = 0x30   = 0b00110000
                                              ^^^^
                            upper nibble = 0x3 = 3 of 15 (20 %)

The byte *looks* sensible on a 0–255 scale. The HL2 only reads the 3.

## How to recognise it

Key TUN, then watch `p1.tx.rate` at 1 Hz. It prints the byte that was
just sent:

    p1.tx.rate pkts=381 ... drv=253    ← upper nibble 0xF = 100 %, good
    p1.tx.rate pkts=381 ... drv=48     ← upper nibble 0x3 = 20 %, capped
    p1.tx.rate pkts=381 ... drv=96     ← upper nibble 0x6 = 40 %, capped

Rule of thumb: upper nibble = `drv >> 4`. Power ≈ `(nibble / 15) × rated`.

## The workaround (today's fix)

Per-operator PA calibration in the Settings → PA Settings panel. For
HL2 on `maxWatts = 5`, target a `paGainDb` that makes the math produce
a byte ≥ 240 at `pct=100`:

    byte ≥ 240 → volts ≥ 0.753 → source ≥ 0.01133 W
    gainDb ≤ 10·log10(5 / 0.01133) = 26.5 dB

`gainDb = 26` flat worked on the reference HL2 (EI6LF, 24 Apr 2026 —
produced 7.1 W on 20 m vs a deskHPSDR baseline of 6.6 W on the same
hardware).

`PaDefaults.Hl2FlatGainDb` is left at 40.5 (piHPSDR's published
default). The 26 dB value is per-unit and lives in the operator's
LiteDB, not in code.

## The forever-fix (not yet implemented)

`ComputeDriveByte` should quantise to the HL2's actual 4-bit scale when
the connected board is HL2. Proposed shape — keep the same watts math,
then round to the nearest nibble-step at the end:

```csharp
byte driveByte = (byte)(Math.Round(norm * 15.0) * 16);   // 0x00, 0x10, 0x20 ... 0xF0
```

This makes the slider honest (every visible step corresponds to a real
HL2 power step) and keeps piHPSDR's 40.5 dB default working — the
quantiser just rounds the computed byte up to the next nibble, so the
operator sees power at the first slider position above 6 % instead of
waiting until they've crossed the nibble boundary.

Gate the quantiser on `board == HermesLite2`; other HPSDR radios use
the full 8 bits and shouldn't lose resolution.

## References

- `docs/references/protocol-1/hermes-lite2-protocol.md:51` — the one
  line that would have saved two days.
- `Zeus.Server/RadioService.cs` — `ComputeDriveByte`.
- `Zeus.Protocol1/ControlFrame.cs` — `LastPeakAbs` / `LastDriveByte`
  instrumentation; `Protocol1Client.TxLoopAsync`'s `p1.tx.rate` log
  surfaces them at 1 Hz.
- `Zeus.Server/PaDefaults.cs` — `Hl2FlatGainDb = 40.5` (piHPSDR's
  generic default, not right for Zeus's 256-step drive model on HL2).
- Working-tune branch tag: `working_herpes_tune`, commit `857988e`.

## Debugging heuristic

When HL2 output doesn't match a reference client, **log what's on the
wire before theorising about why it's wrong**. Zeus's
`Protocol1Client.TxLoopAsync` already prints packet rate + IQ peak +
drive byte at 1 Hz during MOX/TUN; that log answered "drv=48, that's
the whole problem" in one keydown. Two days of bandpass / amplitude /
packet-rate theorising preceded looking at it.
