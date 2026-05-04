# Lesson — TX chain gain staging (browser mic → WDSP TXA → HL2)

**Status:** open question. Documents what we observed during the
`feature/audio_mic_levels` work and what's still unresolved as of
2026-04-29. Update this file as we close out the Thetis A/B comparison.

## What this lesson is about

Zeus feeds WDSP TXA from the browser's `getUserMedia` capture, which has
totally different gain staging from the Hermes-protocol mic samples that
Thetis sees. This lesson captures three concrete things we learned:

1. The **mic-gain slider must allow attenuation**, not boost only.
2. The **TX-chain meter readings tell the gain-staging story** the moment
   you have PK + AV side-by-side with zone bands. Trust the meters.
3. There is **still an open question** about whether the chain can
   produce HL2 rated power on a continuous tone without clipping —
   pending a head-to-head Thetis A/B test.

## 1. Mic-gain range — match Thetis (−40..+10 dB), do not be boost-only

The original Zeus mic-gain slider was 0..+20 dB. That is wrong for any
operator with a hot browser mic — `getUserMedia({ autoGainControl: false })`
routinely peaks **−6..−15 dBFS** at the worklet, so a +5 dB boost lands
you near 0 dBFS into TXA on every voiced syllable. With WDSP ALC's
default thresholds, the chain has nowhere to go and OUT_PK pegs at
0 dBFS — which the EP2 packer converts to int16=32767 (full-scale rail).
Splatter on the air follows.

Thetis ships **MicGainMin = −40, MicGainMax = +10** (`console.cs:19151`,
`:19163`); the slider value is converted via
`Audio.MicPreamp = 10^(db/20)` (`console.cs:28805–28815`) and pushed to
`WDSP.SetTXAPanelGain1`. **The negative half is the important half** —
without it the operator can't back a hot mic off, only push it harder.

Zeus matches Thetis's range (`MicGainSlider.tsx`, `ZeusEndpoints.cs`
`/api/mic-gain`). Default stays 0 dB (= unity, no behaviour change for
operators who never moved the slider).

## 2. The meters tell the staging story — read them in this order

The TX stage meter panel renders each WDSP stage with PK (instantaneous
peak, white tick on top) over AV (sustained level, solid colour fill)
against permanent green/yellow/red zone bands. Read **top-to-bottom**
during a normal voice keydown:

| Stage   | Healthy reading on speech         |
|---------|-----------------------------------|
| MIC     | PK pulses to **−10..−6 dBFS** on plosives, AV around −20..−15 |
| EQ      | bypassed by default — em-dash     |
| LVLR    | PK roughly == MIC PK (≤ +5 dB above on quiet syllables) |
| CFC     | bypassed by default — em-dash     |
| COMP    | bypassed by default — em-dash     |
| **ALC** (PK + GR pair) | PK never above 0 dBFS; GR flickers to 6–10 dB on loudest peaks |
| OUT     | PK touching **−6..−3 dBFS** briefly, AV around −15..−12 |

Symptoms that map to specific bugs:

| Meter pattern                                  | Likely cause |
|-----------------------------------------------|--------------|
| MIC PK > −3 dBFS sustained                     | mic slider too high — back off |
| LVLR PK > MIC PK by more than ~5 dB            | leveler max-gain too aggressive |
| ALC PK pegged at 0 dBFS with GR < 1 dB         | input is exactly at ALC threshold — the chain has nowhere to limit. Symmetric splatter on the panadapter follows. |
| OUT PK = 0 dBFS sustained                      | float→int16 conversion saturates at the EP2 packer; expect on-air IMD |
| OUT PK = −10 dBFS, PWR meter ≈ 0 W on tone    | open question — see §3 |

If you can read those rows at a glance, you can debug a TX issue without
attaching a logic analyser.

## 3. Open: does Thetis produce rated power on a continuous tone?

During this work we observed:

- With MIC slider at **+5 dB** and DRV at 100 %, OUT_PK hit 0 dBFS,
  forward power read ~5.3 W on HL2, and the panadapter showed
  **wide splatter** (the float-1.0 / int16-rail clipping signature).
- Backing MIC to **−9 dB**, OUT_PK landed at −10 dBFS, ALC was actively
  pulling 3 dB of GR, the splatter dropped dramatically — and forward
  power on the same continuous whistle dropped to **~0 W** (HL2's fwd
  sensor is below its noise floor at that amplitude).

The math is consistent with a quadratic amplitude → power mapping
(0.316² ≈ 0.10 → ~0.5 W out of a 5 W radio), so 0 W on the meter at
OUT_PK = −10 dBFS is *probably* normal. **What is unclear** is whether
Thetis on the same HL2 produces clean rated power on a continuous tone
of that level, or whether Thetis users also see the same trade-off and
just don't notice because they whistle for a second, not 10.

To resolve, do a head-to-head comparison on the same HL2 with the same
mic, recording for each platform:

- MIC slider position (dB)
- Mic peak reading on whatever pre-WDSP meter the platform exposes
- Post-ALC peak reading (Thetis: MultiMeter "ALC" reading; Zeus: ALC PK on the stage panel)
- DRIVE %
- Forward watts during a sustained whistle
- On-air spectrum (panadapter screenshot or SDR receiver dump)

If Thetis produces e.g. 5 W with OUT_PK around −3 dBFS and a clean
spectrum, then Zeus is missing a gain stage somewhere between WDSP TXA
output and the EP2 IQ packer. Candidates to audit in that case:

- `Zeus.Server.Hosting/RadioDriveProfile.cs` — `EncodeDriveByte` HL2
  4-bit quantisation
- `Zeus.Server.Hosting/RadioService.cs` — `RecomputePaAndPush` PA-gain
  composition
- `Zeus.Protocol1/ControlFrame.cs` — IQ float→int16 conversion in
  `BuildDataPacket` (this is where the rail saturation happens; if Thetis
  applies a pre-conversion trim we don't, we'll see it here)

If Thetis behaves identically — chain backed off → low power, chain
pushed → splatter — then the conclusion is simply that whistles aren't
representative. SSB on voice has 6–10 dB of peak/average headroom that
ALC exploits; a continuous tone has none, so the chain has to choose
between rated power and clean output. That's a documentation issue, not
a code bug.

**This entry stays open until the A/B is run.** Add a `## Resolution`
section once we know which path it is.
