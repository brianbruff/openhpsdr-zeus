# Zeus Meters Catalog

This document enumerates all receive (RX) and transmit (TX) meter readings supported by **Thetis** — the authoritative reference implementation for HPSDR Protocol-1 and Protocol-2 radios — and surveys the current state of Zeus end-to-end meter telemetry.

The Meters Panel v1 will support **all Thetis RX + TX readings**. This catalog exists so the planner and developers know, for each meter:

- **(a) WDSP call** — what P/Invoke to make (`GetRXAMeter` / `GetTXAMeter` + meter type enum)
- **(b) Unit & range** — how to display it (dBm, dB, W, %, etc.)
- **(c) Zeus wire status** — whether it's already on the wire end-to-end, computed in the server but not streamed, or missing entirely
- **(d) Board notes** — HL2-specific quirks, ADC availability, etc.

---

## RX Meters

Thetis exposes **6 RX meter modes** via `Console/enums.cs:MeterRXMode`:

| Meter | WDSP Call | Unit | Range | Display Type | Zeus Status | Notes |
|-------|-----------|------|-------|--------------|-------------|-------|
| **Signal Strength (peak)** | `GetRXAMeter(ch, RXA_S_PK)` | dBm | −140…0 | Digital | On wire (`RxMeterFrame.RxDbm`) | Peak signal detector; `RxMeterFrame` broadcasts at 5 Hz. Applied calibration offset: HL2 +0.98 dB, ANAN-7000/8000 +4.84 dB, G2 −4.48 dB. |
| **Signal Strength (avg)** | `GetRXAMeter(ch, RXA_S_AV)` | dBm | −140…0 | Digital | On wire (`RxMeterFrame.RxDbm`) | Averaged signal level; same wire field as peak, operator switches in UI. |
| **ADC Input (peak)** | `GetRXAMeter(ch, RXA_ADC_PK)` | dBFS | −100…0 | Digital | **Not on wire** | Raw ADC input peak, before DSP filtering. Useful for gain-staging. Both ADC_L and ADC_R multiplex into single meter call on Protocol-1; requires second RXA channel open to read stereo ADC (ADC2_L, ADC2_R). |
| **ADC Input (avg)** | `GetRXAMeter(ch, RXA_ADC_AV)` | dBFS | −100…0 | Digital | **Not on wire** | Averaged ADC input level. |
| **ADC2 Input (peak)** | `GetRXAMeter(ch, RXA_ADC_PK)` on 2nd RX channel | dBFS | −100…0 | Digital | **Not on wire** | Second ADC path (if available on board). |
| **ADC2 Input (avg)** | `GetRXAMeter(ch, RXA_ADC_AV)` on 2nd RX channel | dBFS | −100…0 | Digital | **Not on wire** | Second ADC averaged input. |
| **AGC Gain** | `GetRXAMeter(ch, RXA_AGC_GAIN)` | dB | −80…+60 | Digital | **Not on wire** | Automatic gain control insertion loss / gain. Positive = gain (AGC is boosting), negative = loss. |
| **AGC Envelope (peak)** | `GetRXAMeter(ch, RXA_AGC_PK)` | dBm | −140…0 | Digital | **Not on wire** | Signal after AGC peak detector. |
| **AGC Envelope (avg)** | `GetRXAMeter(ch, RXA_AGC_AV)` | dBm | −140…0 | Digital | **Not on wire** | Signal after AGC averaging. |

### RX Notes

- **Single-channel RX:** Hermes / ANAN-class radios in Protocol-1 mode expose one RXA WDSP channel (typically channel 0). The `ADC_L` / `ADC_R` distinction in Thetis reflects **stereo ADC hardware**, not two software channels — both read from the same RXA meter call.
- **Dual RX:** When a second RX is enabled (e.g. sub-RX on ANAN), Zeus must open a second RXA channel (typically channel 2) to access `ADC2_L` / `ADC2_R` independently. This is a **multi-channel WDSP state dependency**.
- **HL2 calibration:** HL2 meters include a +0.98 dB calibration offset per-board (applied in `WdspDspEngine.GetRxaSignalDbm`). This matches Thetis' hardware-specific defaults; individual units may drift by ±0.5 dB.
- **AGC readings:** Available only when AGC is enabled on the RXA channel. When AGC is off, `GetRXAMeter(RXA_AGC_GAIN)` returns 0 and the envelope meters report the same as `RXA_S_*`.

---

## TX Meters

Thetis exposes **13 TX meter modes** via `Console/enums.cs:MeterTXMode`. The TX path in Thetis (and now Zeus) is a linear pipeline:

```
Microphone → EQ → Leveler (gain stage) → CFC (Crest Factor Compression) → COMP (Compressor) → ALC (Automatic Level Control) → Power Amp Output
```

Plus two measurement points outside this chain:

- **Power meters** (Forward / Reverse / SWR) — measured at the antenna port via sensor/coupler on the radio hardware
- **ALC Gain Reduction** — the compressor's insertion loss in dB

| Meter | WDSP Call | Unit | Range | Display Type | Zeus Status | Notes |
|-------|-----------|------|-------|--------------|-------------|-------|
| **Microphone (peak)** | `GetTXAMeter(ch, TXA_MIC_PK)` | dBFS | −60…0 | Bar/dial | **Not on wire** | Raw microphone input peak. |
| **Microphone (avg)** | `GetTXAMeter(ch, TXA_MIC_AV)` | dBFS | −60…0 | Bar/dial | In wire (TxMetersV2Frame.MicAv) | Averaged microphone level. Part of extended telemetry. |
| **EQ Output (peak)** | `GetTXAMeter(ch, TXA_EQ_PK)` | dBFS | −60…0 | Bar/dial | In wire (TxMetersFrame.EqPk, TxMetersV2Frame.EqPk) | Signal after equalizer. Peak useful for clipping detection. |
| **EQ Output (avg)** | `GetTXAMeter(ch, TXA_EQ_AV)` | dBFS | −60…0 | Bar/dial | In wire (TxMetersV2Frame.EqAv) | Averaged EQ output. |
| **Leveler Output (peak)** | `GetTXAMeter(ch, TXA_LVLR_PK)` | dBFS | −60…0 | Bar/dial | In wire (TxMetersFrame.LvlrPk, TxMetersV2Frame.LvlrPk) | Signal after the gain/leveling stage. |
| **Leveler Output (avg)** | `GetTXAMeter(ch, TXA_LVLR_AV)` | dBFS | −60…0 | Bar/dial | In wire (TxMetersV2Frame.LvlrAv) | Averaged leveler output. |
| **Leveler Gain Reduction** | `GetTXAMeter(ch, TXA_LVLR_GAIN)` | dB reduction | 0…40 | Digital | In wire (TxMetersV2Frame.LvlrGr) | How much the leveler is cutting. Returned as negative dB gain by WDSP; Zeus negates to positive "reduction" scale. |
| **CFC Output (peak)** | `GetTXAMeter(ch, TXA_CFC_PK)` | dBFS | −60…0 | Bar/dial | In wire (TxMetersV2Frame.CfcPk) | Crest Factor Compressor output peak. |
| **CFC Output (avg)** | `GetTXAMeter(ch, TXA_CFC_AV)` | dBFS | −60…0 | Bar/dial | In wire (TxMetersV2Frame.CfcAv) | CFC averaged output. |
| **CFC Gain Reduction** | `GetTXAMeter(ch, TXA_CFC_GAIN)` | dB reduction | 0…20 | Digital | In wire (TxMetersV2Frame.CfcGr) | CFC compression amount. |
| **Compressor Output (peak)** | `GetTXAMeter(ch, TXA_COMP_PK)` | dBFS | −60…0 | Bar/dial | In wire (TxMetersV2Frame.CompPk) | Output after dynamic range compressor. |
| **Compressor Output (avg)** | `GetTXAMeter(ch, TXA_COMP_AV)` | dBFS | −60…0 | Bar/dial | In wire (TxMetersV2Frame.CompAv) | Averaged compressor output. |
| **ALC Output (peak)** | `GetTXAMeter(ch, TXA_ALC_PK)` | dBFS | −60…0 | Bar/dial | In wire (TxMetersFrame.AlcPk, TxMetersV2Frame.AlcPk) | Signal after automatic level control. |
| **ALC Output (avg)** | `GetTXAMeter(ch, TXA_ALC_AV)` | dBFS | −60…0 | Bar/dial | In wire (TxMetersV2Frame.AlcAv) | Averaged ALC output. |
| **ALC Gain Reduction** | `GetTXAMeter(ch, TXA_ALC_GAIN)` | dB reduction | 0…80 | Digital | In wire (TxMetersFrame.AlcGr, TxMetersV2Frame.AlcGr) | How much ALC is attenuating the signal. |
| **Final Output (peak)** | `GetTXAMeter(ch, TXA_OUT_PK)` | dBFS | −60…0 | Bar/dial | In wire (TxMetersFrame.OutPk, TxMetersV2Frame.OutPk) | Signal delivered to PA input (after all DSP stages). |
| **Final Output (avg)** | `GetTXAMeter(ch, TXA_OUT_AV)` | dBFS | −60…0 | Bar/dial | In wire (TxMetersV2Frame.OutAv) | Averaged final output. |
| **Forward Power** | Hardware sensor (not WDSP) | W | 0…(board-dependent) | Bar/dial | In wire (TxMetersFrame.FwdWatts, TxMetersV2Frame.FwdWatts) | Antenna port forward power measured via RF coupler. Requires Protocol-1 0x04 feedback or Protocol-2 TX feedback stream. |
| **Reverse Power** | Hardware sensor (not WDSP) | W | 0…(board-dependent) | Bar/dial | In wire (TxMetersFrame.RefWatts, TxMetersV2Frame.RefWatts) | Antenna port reflected power. |
| **SWR** | Derived: SWR = (Fwd + Ref) / (Fwd − Ref) | ratio | 1.0…∞ | Digital | In wire (TxMetersFrame.Swr, TxMetersV2Frame.Swr) | Standing-wave ratio. Clamped to ≤ 50:1 to avoid division issues at low power. |
| **Microphone Input (for SWR button)** | `GetTXAMeter(ch, TXA_MIC_PK)` | dBFS | −60…0 | Digital | In wire (TxMetersFrame.MicDbfs) | Raw mic peak, used to label SWR button in Thetis. Same as **Microphone (peak)** above. |

### TX Notes

- **Silence sentinel:** When TXA is not processing (MOX off), WDSP returns approximately −400 dBFS for all level meters (the sentinel). The frontend should treat this as "stage bypassed" (see `docs/lessons/dev-conventions.md`). The CFC and COMP stages sit at the sentinel until explicitly engaged by the operator; `TxMetersV2Frame` preserves these sentinel values so the UI can style them as "inactive".
- **Gain reduction sign convention:** WDSP's `GetTXAMeter(TXA_LVLR_GAIN)`, `GetTXAMeter(TXA_CFC_GAIN)`, and `GetTXAMeter(TXA_ALC_GAIN)` return negative dB when the stage is reducing. Zeus negates these before serializing so downstream consumers see a monotonic "how much are we cutting?" scale (≥ 0).
- **TxMetersFrame vs. TxMetersV2Frame:**
  - **TxMetersFrame (0x11)** — 37 bytes, broadcasts at 10 Hz during MOX. Carries forward/reverse/SWR/mic + peak readings for EQ/Leveler/ALC/Output. Sufficient for basic monitoring.
  - **TxMetersV2Frame (0x16)** — 81 bytes, same 10 Hz cadence. Extends v1 with average readings for every stage, plus CFC and COMP (omitted in v1). Allows the operator to judge both level (average) and clipping (peak) simultaneously.
  - Both are compatible additive extensions — a client that knows only 0x11 will ignore 0x16 frames.
- **Power meters** (Forward / Reverse / SWR) are **not WDSP** — they come from the radio's RF coupler feedback stream (Protocol-1 or Protocol-2). These are available end-to-end on any board with a coupler (all ANAN, Hermes, HL2 with PA).

---

## Zeus Current Telemetry

### RX Telemetry

**Currently on wire:**
- `RxMeterFrame` (0x14) — single float, RX signal in dBm, broadcast at 5 Hz from `DspPipelineService`. Covers both peak and average via UI switching.

**Computed in server but not on wire:**
- AGC gain, AGC envelope (peak/avg), ADC input (peak/avg, both channels), ADC2 input (peak/avg).

### TX Telemetry

**Currently on wire:**

- **TxMetersFrame (0x11)** — 37 bytes:
  - Forward power, reverse power, SWR, mic dBFS
  - EQ peak, Leveler peak, ALC peak, ALC gain reduction, Output peak
  - Broadcast at 10 Hz during MOX.

- **TxMetersV2Frame (0x16)** — 81 bytes (extended):
  - All v1 fields (forward/reverse/SWR)
  - Mic peak, Mic avg
  - EQ peak, EQ avg
  - Leveler peak, Leveler avg, Leveler gain reduction
  - CFC peak, CFC avg, CFC gain reduction
  - Compressor peak, Compressor avg
  - ALC peak, ALC avg, ALC gain reduction
  - Output peak, Output avg
  - Broadcast at 10 Hz during MOX (when engine is live and TXA is open).

- **PsMetersFrame (0x18)** — 15 bytes:
  - PureSignal feedback level, correction depth, calibration state, correcting flag, max TX envelope.
  - Broadcast at ~10 Hz when PureSignal is enabled. Not part of the standard meter panel (separate PureSignal UI).

**Not computed at all (wire or server):**
- Individual Microphone peak (TxMetersFrame carries only the raw peak for the SWR button label, not as a meter reading in its own right; TxMetersV2Frame adds it).

---

## Multi-Channel WDSP Dependencies

Several meters require opening additional RXA or TXA channels beyond the primary receiver:

| Dependency | Current Zeus State | Note |
|------------|-------------------|------|
| **ADC2_L / ADC2_R** (second RX ADC) | **Not open** | Requires opening a second RXA channel (typically channel 2) when sub-RX is enabled. Sub-RX is not yet implemented in Zeus. |
| **Dual-RX AGC** (second RX AGC) | **Not open** | Like ADC2, requires second RXA. |
| **TXA CFC/COMP reduction** | **Open but may not be engaged** | Zeus opens TXA for TX, so these meter calls work. However, the stages themselves (CFC, COMP) are operator-engaged via the TX panel; when off, they sit at the silence sentinel (≈ −400 dBFS), which the frontend interprets as "bypassed". |

---

## Board-Specific Considerations

| Board | Notes |
|-------|-------|
| **Hermes** | Full support for all WDSP meters. ADC2 available if sub-RX enabled. |
| **ANAN-7000 / ANAN-8000** | Full support. RX meter calibration offset: +4.84 dB. ADC2 available if sub-RX enabled. |
| **ANAN-200D** | Full support. RX meter calibration offset: +0.98 dB (same as HL2). |
| **Hermes Lite 2 (HL2)** | Full support. **RX meter calibration: +0.98 dB**. **No sub-RX** (single RXA only, so ADC2_L / ADC2_R are aliases of ADC_L / ADC_R). CFC and COMP available in DSP but operator control may be limited in firmware. No forward/reverse/SWR measurement on early HL2 units without PA. |
| **Orion / Orion Mk II** | Full support for WDSP meters. TX power feedback via Protocol-2 stream. |
| **G2 MkII (Saturn)** | Full support for WDSP meters. RX meter calibration offset: −4.48 dB. Protocol-2 RX streaming. |

---

## Recommended Implementation Batches

### First Batch (v1.0 — Core Monitoring)

**Rationale:** These 10 readings cover ~90% of typical HF operation — signal awareness, safe TX level, PA headroom, and basic gain-staging.

**RX:**
1. Signal Strength (peak) — already on wire via `RxMeterFrame`

**TX:**
2. Forward Power — already on wire
3. SWR — already on wire
4. Microphone (already on wire as MicDbfs in frame, needs UI exposure)
5. ALC Gain Reduction — already on wire
6. Output Peak — already on wire

**TX (extended, TxMetersV2Frame):**
7. EQ Output (peak/avg) — for EQ tuning feedback
8. Leveler Output (peak/avg) — to confirm leveler is working
9. ALC Output (peak/avg) — to see final stage before PA
10. Output Average — to judge sustained level vs. peak

**Implementation effort:** Wire `TxMetersV2Frame` (0x16) fully; expose current `RxMeterFrame` in the UI. No new WDSP calls needed — all metrics already computed in `TxStageMeters`. **Pre-existing wire format** (P1.4 onwards on feature branch).

### Second Batch (v1.1 — Detailed TX Staging & AGC)

**Rationale:** Deeper monitoring for operators tuning TX chain or diagnosing AGC behaviour.

**RX:**
1. ADC Input (peak/avg) — gain-staging reference
2. AGC Gain — to see how much AGC is active
3. AGC Envelope (peak/avg) — signal after AGC shaping

**TX:**
4. Microphone (peak) — catch mic clipping the signal meter doesn't show
5. CFC Output (peak/avg) — crest factor compression effect
6. CFC Gain Reduction — how much CFC is working
7. Compressor (peak/avg) — dynamic range compression effect
8. Reverse Power — SWR calculation confidence
9. Leveler Gain Reduction — leveler insertion loss (complementary to output)

**Implementation effort:** Some calls already on wire (`TxMetersV2Frame`); RX requires new WDSP calls and a new RxMetersV2Frame wire format. **Pre-existing architecture** for TX; RX additions follow the same pattern.

### Third Batch (Future — Advanced / Board-Specific)

- **ADC2_L / ADC2_R** — requires sub-RX support (not in v1)
- **Dual-RX AGC** — same prerequisite
- **CFC/COMP control UI** — currently read-only metrics; would need TX panel extensions to engage/disengage stages
- **Per-band TX calibration** — forward power measurement calibration per band (driver feature, not meter)

---

## Wire Format Summary

| Frame | ID | Bytes | Rate | Condition | Content |
|-------|-----|-------|------|-----------|---------|
| `RxMeterFrame` | 0x14 | 5 | 5 Hz | RX engine live | RX signal (dBm) |
| `TxMetersFrame` | 0x11 | 37 | 10 Hz | MOX active, TXA open | Fwd/Rev/SWR/Mic + stage peaks + ALC reduction |
| `TxMetersV2Frame` | 0x16 | 81 | 10 Hz | MOX active, TXA open | All v1 fields + stage averages + CFC/COMP |
| `PsMetersFrame` | 0x18 | 15 | 10 Hz | PS enabled | PS calibration state + correction depth |

All TX frames intentionally omit the 16-byte `WireFormat` header (seq/ts/type) to reduce 10 Hz wire overhead during key-down. The client treats the latest value as authoritative.

---

## WDSP API Reference

### RXA Meters

```c
double GetRXAMeter(int channel, rxaMeterType meter);

enum rxaMeterType {
  RXA_S_PK,       // 0 — Signal peak
  RXA_S_AV,       // 1 — Signal average
  RXA_ADC_PK,     // 2 — ADC peak
  RXA_ADC_AV,     // 3 — ADC average
  RXA_AGC_GAIN,   // 4 — AGC gain (dB, ≤0 when cutting)
  RXA_AGC_PK,     // 5 — AGC envelope peak
  RXA_AGC_AV,     // 6 — AGC envelope average
  RXA_METERTYPE_LAST
};
```

### TXA Meters

```c
double GetTXAMeter(int channel, txaMeterType meter);

enum txaMeterType {
  TXA_MIC_PK,      // 0
  TXA_MIC_AV,      // 1
  TXA_EQ_PK,       // 2
  TXA_EQ_AV,       // 3
  TXA_LVLR_PK,     // 4
  TXA_LVLR_AV,     // 5
  TXA_LVLR_GAIN,   // 6 — dB gain (negative when reducing)
  TXA_CFC_PK,      // 7
  TXA_CFC_AV,      // 8
  TXA_CFC_GAIN,    // 9 — dB gain (negative when reducing)
  TXA_COMP_PK,     // 10
  TXA_COMP_AV,     // 11
  TXA_ALC_PK,      // 12
  TXA_ALC_AV,      // 13
  TXA_ALC_GAIN,    // 14 — dB gain (negative when reducing)
  TXA_OUT_PK,      // 15
  TXA_OUT_AV,      // 16
  TXA_METERTYPE_LAST
};
```

---

## References

- **Thetis meter UI:** `/Users/bek/Data/Repo/github/Thetis/Project Files/Source/Console/enums.cs` — `MeterRXMode`, `MeterTXMode`
- **Thetis DSP:** `/Users/bek/Data/Repo/github/Thetis/Project Files/Source/Console/dsp.cs` — `WDSP.rxaMeterType`, `WDSP.txaMeterType`, `CalculateRXMeter`, `CalculateTXMeter`
- **Zeus WDSP bridge:** `/Users/bek/Data/Repo/github/OPENHPSDR-Zeus.Worktrees/feature_meters/Zeus.Dsp/Wdsp/WdspDspEngine.cs` — meter calls
- **Zeus TX telemetry:** `/Users/bek/Data/Repo/github/OPENHPSDR-Zeus.Worktrees/feature_meters/Zeus.Dsp/TxStageMeters.cs`, `/Users/bek/Data/Repo/github/OPENHPSDR-Zeus.Worktrees/feature_meters/Zeus.Contracts/TxMetersFrame.cs`, `TxMetersV2Frame.cs`
- **Zeus RX telemetry:** `/Users/bek/Data/Repo/github/OPENHPSDR-Zeus.Worktrees/feature_meters/Zeus.Contracts/RxMeterFrame.cs`
- **HL2 calibration:** `/Users/bek/Data/Repo/github/OPENHPSDR-Zeus.Worktrees/feature_meters/docs/lessons/wdsp-init-gotchas.md`
- **WDSP headers:** `/Users/bek/Data/Repo/github/OPENHPSDR-Zeus.Worktrees/feature_meters/native/wdsp/wdsp.h`

