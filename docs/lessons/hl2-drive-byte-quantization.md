# HL2 drive-byte quantisation — superseded

**This lesson has been superseded.** See
[`hl2-drive-model.md`](./hl2-drive-model.md) for the current ground truth
on how Zeus drives the HL2.

The original lesson described the symptom (HL2 produces ~1 W at piHPSDR's
published `PaGainDb = 40.5` default) as a quantisation problem and
recommended a per-operator calibration around `PaGainDb ≈ 26 dB` to work
around it. That advice is now obsolete — it was treating the symptom,
not the cause.

The cause is that HL2's PA drive model is fundamentally different from
the piHPSDR / Thetis dB model every other HPSDR radio uses. HL2 is
**percentage-based**, as the mi0bot openhpsdr-thetis fork implements.
`PaGainDb` on HL2 is now an output percentage (0..100); HF defaults are
100, 6 m is 38.8.

The `PaGainDb = 26` workaround no longer applies. If you see it in old
LiteDB state on the reference HL2 it should be reset to 100 — press
**Reset to Hermes Lite 2 defaults** in the PA Settings panel.

## Quick reference (old → new interpretation)

| Stored `PaGainDb` | Old (dB model, pre-fix) | New (% model, current) |
|---|---|---|
| 40.5 | piHPSDR generic default → 1 W | 40.5 % output → nibble 0x6 → 40 % |
| 26   | EI6LF calibration → 7 W       | 26 % output → nibble 0x4 → 27 %   |
| 100  | (out of old clamp range)      | Full rated → nibble 0xF → 100 %   |
| 38.8 | (unlikely before)             | 6 m soft-cap → nibble 0x6 → 40 %  |

See [`hl2-drive-model.md`](./hl2-drive-model.md) for the full derivation
and the mi0bot Thetis references.
