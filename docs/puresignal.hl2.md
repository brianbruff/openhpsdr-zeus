# PureSignal on the Hermes Lite 2 — Working Log

**Goal.** Get PureSignal collecting and converging reliably on the Hermes Lite 2 in
Openhpsdr-Zeus. Keep this file as a forward-going log so we never end up at the
same ground floor again.

**Authoritative reference.** The HL2 PureSignal flow is **mi0bot's Thetis fork**
at `/Users/bek/Data/Repo/github/OpenHPSDR-Thetis/`. The canonical write-up is
`OpenHPSDR-Thetis/ps.md`; the runtime code lives in that repo's `Project Files/`
tree. Do not infer HL2 PS behaviour from `ramdor/Thetis`, from KB2UKA PR diffs,
or from piHPSDR — only mi0bot.

**Bench invariants** (operator: Brian, EI6LF):
- MOX on **28.400 MHz** (antenna is resonant there).
- ALWAYS un-MOX after any test step. No idling in TX.
- During PS COLLECT, drive / TUN slider may need raising to put real envelope on
  the air; tester should bump drive until forward power is non-zero before
  declaring "collection failed".
- Verify forward power AND PS collection together — neither alone counts as a
  pass.

**Working method.** Read mi0bot first, audit Zeus second, bench-test third. No
guessing; if a behaviour is unclear, go back to mi0bot source — the exact code
is there. Append findings under "Session log" with the date.

---

## Authoritative HL2 PureSignal sequence (from mi0bot)

> All citations are absolute paths under
> `/Users/bek/Data/Repo/github/OpenHPSDR-Thetis/`. The companion summary at
> `OpenHPSDR-Thetis/ps.md` is the maintainer-curated overview and matches the
> source unless noted otherwise; where ps.md and the source disagree, the
> source wins and I call it out.

### A. Channel selection — which "channel" PS talks to

`PSForm` resolves the TX channel id once and uses it for every `puresignal.*`
call:

- `PSForm.cs:331` — `private static int _txachannel = WDSP.id(1, 0);`
  Per `ps.md:800-803`, that resolves to **WDSP channel id 10**
  (`cmaster.CMsubrcvr * cmaster.CMrcvr = 2 * 5 = 10`). The auto-cal,
  auto-att, and `SetPS*` calls all pass this id verbatim.

### B. One-time native bring-up (ChannelMaster + WDSP defaults)

In order, on console boot before PS-A is ever toggled:

1. `clsHardwareSpecific.cs:300-329` (`PSDefaultPeak`):
   `HPSDRHW.HermesLite + RadioProtocol.USB → 0.233`. (P1 generic = 0.4072,
   P2 generic = 0.2899, Saturn = 0.6121.)
2. `cmaster.cs:420-469` (`CMCreateCMaster`) — boots ChannelMaster, sets:
   - `SetPSRxIdx(0, 0)` — txid 0 binds RX feedback to router Stream 0.
   - `SetPSTxIdx(0, 1)` — txid 0 binds TX feedback to router Stream 1.
   - `puresignal.SetPSFeedbackRate(txch, 192000)`.
   - `puresignal.SetPSHWPeak(txch, 0.2899)` — **a hard-coded P2 value**
     that gets overwritten as soon as the model is identified and
     `PSDefaultPeak` is applied (cross-checked against `ps.md:552-559`,
     §4.4 "one-time PS setup").
3. `cmaster.cs:483-550` (`CMLoadRouterAll`) — on every model change. HL2
   takes the **HERMES / ANAN-10 / ANAN-100 four-DDC branch** (case at
   `cmaster.cs:611-635`). The non-loopback HL2 router table is
   `FOUR_DDC_Function = {1×8, 0,0,0,0,0,2,0,2, 1×8}` and
   `FOUR_DDC_Callid = {0×8, 0,0,0,0,0,1,0,1, 1×8}` with `nstreams = {1,2,1}`
   — i.e. DDC0 → RX1 (port 1035), DDC2+DDC3 paired → PS feedback (port
   1036), DDC1 → RX2 (port 1037). Router state index 5 / 7 (i.e.
   `MOX|PS` = 1 alone, and `MOX|DIV|PS` = 1+1+1) is where the PS callid
   bit flips from `0` to `1`, switching DDC2/DDC3 into the PS engine.
4. `cmaster.cs:488-493` — when `Audio.console.ReduceEthernetBW` is set on
   HL2, `CMLoadRouterAll` re-maps `HERMESLITE → ANAN10E` so the router
   collapses to a 2-DDC layout for WAN use. PS still works but routes
   feedback through DDC0+DDC1 instead. **This is a real behavioural fork
   to be aware of when the user toggles "Reduce Ethernet BW".**

### C. The PS thread — `PSLoop` and its two timers

- `PSForm.cs:137-149` — `startPSThread()` spawns `"PureSignal Thread"`,
  `ThreadPriority.AboveNormal`, `IsBackground = true`.
- `PSForm.cs:189-222` — `PSLoop()`:
  - When `_power && !IsDisposed && IsHandleCreated`: sleep **10 ms**, run
    `timer1code()` every tick, run `timer2code()` every 10th tick (~100 ms).
  - When `m_bQuckAttenuate` is set, `timer2code()` runs every 10 ms too.
  - When power is off or the form is closing: sleep 100 ms, run nothing.

### D. `PSEnabled` — the wire-side routing toggle

`PSForm.cs:234-269` (`PSEnabled` setter) is the single bridge between the
PS engine state and the UDP wire. It is **not** PS-A; it is flipped from
inside the command state machine.

When set to `true`, in this exact order:

```
console.UpdateDDCs(console.RX2Enabled);                        // re-program DDCs
NetworkIO.SetPureSignal(1);                                    // master PS bit
NetworkIO.SendHighPriority(1);                                 // flush HP packet
console.UpdateAAudioMixerStates();
unsafe { cmaster.LoadRouterControlBit((void*)0, 0, 0, 1); }    // route feedback to PS
console.radio.GetDSPTX(0).PSRunCal = true;                     // gate WDSP pscc()
```

When set to `false`, the same five calls are reversed with `0` payloads.
**`PSRunCal = false` is the last step that gates the WDSP `pscc()` entry
point** — the Zeus equivalent must clear this when PS turns off, or the
engine will keep accepting samples from the prior session.

### E. PS-A (front-panel) → `AutoCalEnabled` → `_autoON`

- `PSForm.cs:271-289` — `AutoCalEnabled` setter:
  - `true` → `_autoON = true; console.PSState = true;`
  - `false` → `_OFF = true; console.PSState = false;`
- These two flags (`_autoON`, `_OFF`) are the input edges to the command
  state machine in `timer1code()`. Setting `AutoCalEnabled = true` does
  **not** itself send any wire-side traffic; it only sets a flag that the
  next `timer1code()` tick reads.

### F. Command state machine (`timer1code()`)

`PSForm.cs:629-726`. Every 10 ms tick: `GetPSInfo` first (already at
`PSForm.cs:617-625`), then exactly one transition. The eight states and
their `SetPSControl(reset, mancal, automode, turnon)` payloads — direct
from source:

| State (line)                                | SetPSControl  | PSEnabled side-effect                  | Exit edges                             |
|---------------------------------------------|---------------|----------------------------------------|----------------------------------------|
| `OFF` (632-643)                             | `(1,0,0,0)`   | force `false`                          | `_restoreON`→7, `_autoON`→1, `_singlecalON`→3 |
| `TurnOnAutoCalibrate` (644-649)             | `(1,0,1,0)`   | force `true`                           | unconditional → 2                      |
| `AutoCalibrate` (650-657)                   | _none_        | _no change_                            | `_OFF`→6, `_restoreON`→7, `_singlecalON`→3 |
| `TurnOnSingleCalibrate` (658-665)           | `(1,1,0,0)`   | force `true`; clears `_autoON`         | unconditional → 4                      |
| `SingleCalibrate` (666-676)                 | _none_        | _no change_                            | `_OFF`→6, `_restoreON`→7, `_autoON`→1, `CorrectionsBeingApplied`→5 |
| `StayON` (677-700)                          | _none_        | force `false` (drops feedback routing) | `_OFF`→6, `_restoreON`→7, `_autoON`→1, `_singlecalON`→3, single-cal retry up to 5× if not in green window |
| `TurnOFF` (701-716)                         | `(1,0,0,0)`   | force `true` briefly                   | only returns to `OFF` when `!CorrectionsBeingApplied && State == LRESET` |
| `IntiateRestoredCorrection` (717-725)       | `(0,0,0,1)`   | force `true`; clears `_autoON`         | `State == LSTAYON` → 5                 |

Two non-obvious behaviours:

- **`StayON` drops `PSEnabled` to false** (line 678). Corrections continue
  to be applied by IQC inside WDSP, but the wire-side feedback DDCs are
  un-routed. Restored corrections also land in `StayON`.
- **The `OFF`→`OFF`→… "wait for engine to drain" handshake** in `TurnOFF`
  (line 714) gates the OFF transition on the WDSP engine actually reaching
  `LRESET` and dropping `info[14]`. A naive Zeus implementation that just
  sends `(1,0,0,0)` once will pass through OFF on the next tick before the
  engine has actually reset, which can leave stale correction state in
  IQC.

### G. Auto-attenuate state machine — the "3-state dance"

`PSForm.cs:728-811`, three states cycled on each tick (one transition per
tick).

```
Monitor (0) ──[needs recal]──> SetNewValues (1) ──> RestoreOperation (2) ──> Monitor (0)
```

#### G.1 `Monitor` (`PSForm.cs:734-780`)

The trigger condition is exactly:

```csharp
_autoattenuate &&
puresignal.CalibrationAttemptsChanged &&
((Model != HERMESLITE && puresignal.NeedToRecalibrate(SetupForm.ATTOnTX)) ||
 (Model == HERMESLITE && puresignal.NeedToRecalibrate_HL2(SetupForm.ATTOnTX)))
```

- `CalibrationAttemptsChanged` (`PSForm.cs:1117-1119`):
  `_info[5] != _oldInfo[5]` — the cumulative `cor.cnt` has just ticked.
- `NeedToRecalibrate_HL2` (`PSForm.cs:1134-1137`):
  `FeedbackLevel > 181 || (FeedbackLevel <= 128 && nCurrentATTonTX > -28)`.
  The non-HL2 predicate uses `> 0` instead of `> -28`. **HL2 must allow
  TX-att to go negative** (down to -28 dB, the LNA region).

When the trigger fires:

1. If `console.ATTOnTX` is currently false, force it true via the property
   setter (`PSForm.cs:740`). This grabs ownership of the TX step att.
2. Compute `ddB`:
   - If feedback level is "OK" (`<= 256`):
     `ddB = 20 * log10(FeedbackLevel / 152.293)` (`PSForm.cs:747`).
     - **Non-HL2 NaN/inf clamps**: `NaN→31.1`, `<-100→-100`, `>+100→+100`
       (`PSForm.cs:752-755`).
     - **HL2 NaN/inf clamps** (`PSForm.cs:756-761`): `NaN→10.0`,
       `<-100→-10.0`, `>+100→10.0`. The HL2 path **clamps both extremes
       to ±10 dB** rather than running to ±100 dB. Comment in source:
       `// MI0BOT: Handle the Not A Number situation`.
   - If feedback level is **not** OK (i.e. `> 256`):
     - HL2: `ddB = 10.0` (`PSForm.cs:765-766`).
     - Non-HL2: `ddB = 31.1`.
3. `_deltadB = Math.Round(ddB, MidpointRounding.AwayFromZero)`
   (`PSForm.cs:772`).
4. Snapshot the *current* command state into save slots
   (`PSForm.cs:774-775`):
   `_save_autoON = (_cmdstate == eCMDState.AutoCalibrate) ? 1 : 0`,
   `_save_singlecalON = (_cmdstate == eCMDState.SingleCalibrate) ? 1 : 0`.
5. **Freeze the engine**: `puresignal.SetPSControl(_txachannel, 1, 0, 0, 0)`
   (`PSForm.cs:778`).

Then advance to `SetNewValues`.

#### G.2 `SetNewValues` (`PSForm.cs:781-806`)

```csharp
int oldAtten = console.SetupForm.ATTOnTX;
int newAtten;
if (Model == HERMESLITE)
    newAtten = oldAtten + _deltadB;             // HL2: signed, can go negative
else
    newAtten = Math.Max(0, oldAtten + _deltadB);

if (oldAtten != newAtten) {
    console.SetupForm.ATTOnTX = newAtten;       // applies via SetTxAttenData
    if (m_bQuckAttenuate) Thread.Sleep(100);    // give the radio time to act
}
```

Setting `SetupForm.ATTOnTX` is what eventually calls `console.ATTOnTX`
setter, which in turn calls `NetworkIO.SetTxAttenData(31 - txatt)` on HL2
(`console.cs:19594-19623`, specifically the `31 - txatt` inversion at line
19610). On non-HL2 the wire takes `txatt` directly.

Then advance to `RestoreOperation`.

#### G.3 `RestoreOperation` (`PSForm.cs:807-810`)

```csharp
puresignal.SetPSControl(_txachannel, 0, _save_singlecalON, _save_autoON, 0);
```

This **resumes** whichever flavour of cal was running before the freeze.
Then back to `Monitor`.

> Note on the `Mox` setter (`PSForm.cs:341-349`): every console-driven
> MOX edge calls `puresignal.SetPSMox(_txachannel, value)` directly,
> independent of the command state machine. The native engine uses this
> to gate `LMOXDELAY → LSETUP → LCOLLECT` transitions in `calcc.c` (see
> §I).

### H. `hw_peak` / `hw_scale` handling

- `SetPSHWPeak` (`calcc.c:1015-1023`): `a->hw_scale = 1.0 / peak`.
- `GetPSHWPeak` (`calcc.c:1025-1031`): returns `1.0 / a->hw_scale`.
- HL2 default peak: **`0.233`** (`clsHardwareSpecific.cs:311-312`).
- Initial transient default written by `CMCreateCMaster`: `0.2899`
  (`cmaster.cs:458`). This is overwritten as soon as the radio is
  identified and the model-specific value is applied.
- UI binding (`PSForm.cs:815-831`): `txtPSpeak.TextChanged` calls
  `puresignal.SetPSHWPeak(_txachannel, _PShwpeak)`, then
  `UpdateWarningSetPk()` shows `pbWarningSetPk` whenever the textbox value
  differs from `HardwareSpecific.PSDefaultPeak`. **mi0bot considers any
  deviation from 0.233 on HL2 a misconfiguration worth a yellow flag**;
  there is no auto-calibration of `peak` itself in mi0bot.
- "Default" button: `psdefpeak(HardwareSpecific.PSDefaultPeak)`
  (`PSForm.cs:371-381`) — writes the per-radio default back into the UI
  textbox, which fires the change event above.
- `GetPSMaxTX` is read every 10 ms in `timer1code()` (`PSForm.cs:621-627`)
  and bound to `txtGetPSpeak`. Comparison of `SetPk` vs `GetPk` is the
  only mechanism mi0bot exposes for verifying peak is correct. **mi0bot
  does not do an auto-tuning of `peak`** — the operator reads `GetPk` on
  the air and matches `SetPk` to it manually.

What `peak` actually drives in the engine, from `calcc.c`:

- `calcc.c:701-739` (`LCOLLECT`): for each TX I/Q sample,
  `env = sqrt(I² + Q²); env *= a->hw_scale;` if `env <= 1.0` → bin index =
  `env * a->ints`, deposit sample into `txs[]` / `rxs[]` for that bin.
  Samples with `env > 1.0` are **silently dropped** from the spline.
- `calcc.c:747-748`: state advances to `MOXCHECK` only when
  `a->ctrl.full_ints == a->ints` — every bin must reach `spi` samples.
- `calcc.c:751-760`: if `count >= 4 * rate` (~4 sec at 192 kHz) without
  filling all bins, the per-bin counters are flushed and collection
  restarts — this is the "stuck in COLLECT" symptom from a `peak`
  mismatch.

So **the failure mode for a wrong `peak` on HL2 is mechanical**:
`hw_scale` mis-normalises the TX envelope so most samples land in a
narrow band of bins (some bins never reach `spi`), `full_ints` never
reaches `ints`, the state machine re-floods every 4 s, and `cor.cnt`
never increments. The engine looks "active" (state cycles, MOX is set)
but never produces a calibration. The mi0bot remedy is operator-tuning
of `txtPSpeak` until `GetPk ≈ SetPk` and the green window is reached.

> Cross-check against the working-log memory note: ps.md does not document
> the "0.233 too high at drive=21%, ~0.18 worked" observation — that is
> bench-empirical from the prior Zeus session, not from mi0bot source.
> Bench-tester should re-confirm under current Zeus state.

### I. Native engine state machine (`calcc.c`) — what each PS state means

`PSForm.cs:1164-1180` enumerates the engine states (mirrored from
`calcc.c`). The transitions, from `calcc.c:640-833`:

| State (calcc.c line) | What it does                                                    | Exit condition                                  |
|----------------------|-----------------------------------------------------------------|-------------------------------------------------|
| `LRESET` (~640-650)  | reset all counters; load fresh control bits                     | unconditional → `LWAIT`                         |
| `LWAIT` (652-665)    | idle; wait for MOX                                              | `mox && solidmox` → `LMOXDELAY`                 |
| `LMOXDELAY` (666-677)| count `moxsamps` worth of post-PTT settling                     | once `moxcount ≥ moxsamps` → `LSETUP`           |
| `LSETUP` (678-700)   | zero per-bin sample counters; clear dog count                   | unconditional → `LCOLLECT`                      |
| `LCOLLECT` (701-761) | bin TX/RX I/Q samples by `env*hw_scale` index                   | `full_ints == ints` → `MOXCHECK`; `info[13] ≥ 6` → `LRESET`; `count ≥ 4*rate` → flush+stay |
| `MOXCHECK` (762-772) | sanity check MOX is still asserted                              | → `LCALC` (if MOX still on)                     |
| `LCALC` (773-801)    | release `Sem_CalcCorr`; wait for builder thread; install spline | success → `LDELAY`; sanity fail twice → `LRESET`|
| `LDELAY` (802-821)   | wait `waitsamps` (loop delay) before next collection            | `automode` → `LSETUP`/`LWAIT`; else → `LSTAYON` |
| `LSTAYON` (822-826)  | corrections held; engine idle                                   | `reset` / `automode` / `mancal` flag → `LRESET` |
| `LTURNON` (827-833)  | turn-on path used by `PSRestoreCorr`                            | unconditional → `LSTAYON`                       |

`solidmox` is set inside `LWAIT` when MOX first arrives (`calcc.c:660-664`)
and is **only cleared by `SetPSMox(ch, 0)`** (`calcc.c:907-910`). This is
why the host *must* call `SetPSMox(_txachannel, false)` on the un-MOX
edge — otherwise the engine thinks MOX is still solid and never returns
to `LWAIT` cleanly.

### J. HL2 DDC table during PS-active TX

`console.cs:8185-8265` is the HERMES/HL2 case in `UpdateDDCs`. The PS-on
TX branch is at `console.cs:8246-8263`:

```csharp
else // transmitting and PS is ON
{
    P1_DDCConfig = 6;
    DDCEnable = DDC0;       // 0x01
    SyncEnable = DDC1;      // 0x02 — DDC1 sync-paired to DDC0
    if (hpsdr_model == HPSDRModel.HERMESLITE) {  // MI0BOT: HL2 can work at a high sample rate
        Rate[0] = rx1_rate;
        Rate[1] = rx1_rate;
    } else {
        Rate[0] = ps_rate;
        Rate[1] = ps_rate;
    }
    cntrl1 = 4;             // ADC routing word: PS feedback path enabled
    cntrl2 = 0;
}
```

Three HL2-specific facts:

1. `P1_DDCConfig = 6` is the PS-active TX selector for the 4-DDC family
   (Hermes / HL2 / ANAN-10 / ANAN-100). Other families use different
   selectors — Orion-class uses `P1_DDCConfig = 3` with `DDC0+DDC2`.
2. **HL2 keeps DDC0/DDC1 at the user's RX1 sample rate during PS-MOX**,
   not at `ps_rate`. The non-HL2 P1 path drops them to `ps_rate`
   (typically 192 kHz). The mi0bot rationale (in-source comment): "HL2
   can work at a high sample rate".
3. `cntrl1 = 4` is the ADC control word that wires the feedback path on
   the HL2 firmware. The wire-out sequence after the switch is at
   `console.cs:8343-8350` (verified): `EnableRxs → EnableRxSync →
   SetDDCRate(0..3) → SetADC_cntrl1 → SetADC_cntrl2 → CmdRx →
   Protocol1DDCConfig`.

Crosswalk to ps.md §4.1 (line 442-451) — the table and source agree.

### K. Feedback path on HL2 — internal coupler, no UI selector

mi0bot's PSForm exposes **no Internal/External feedback-source selector**
on any model. The feedback comes in over the same DDC2/DDC3 stream pair
the router was loaded with in `CMLoadRouterAll` (§B). On HL2:

- HL2 has a single ADC (`clsHardwareSpecific.cs` HL2 case, ps.md §4.5
  citing `SetRxADC(1)`). Every DDC ultimately reads ADC0.
- During PS-MOX, the firmware routes the on-board sampler / coupler
  output into ADC0 → DDC2 → router stream 0 (PSRX) and into DDC3 →
  router stream 1 (PSTX) per the FOUR_DDC table (`cmaster.cs:611-635`).
- There is **no software toggle in mi0bot for "internal" vs "external"
  coupler on HL2** — the path is hard-wired. ps.md §4 also describes a
  single coupler path with no selector.
- The Zeus repo's MEMORY note "HL2 has internal coupler — keep PS
  Internal/External feedback-source radio on HL2" is a Zeus UI policy,
  not something mi0bot mirrors. It does not affect the wire-side
  behaviour expected here.

Implication for Zeus: the auditor should check that Zeus is not waiting
on or routing through some "external coupler enabled" branch that has no
mi0bot equivalent, while still keeping the UI selector as Brian
specified.

### L. HL2 quirks that *must* round-trip into Zeus (consolidated)

From the mi0bot source, ranked by how often a wrong implementation will
silently break PS on HL2:

1. **TX att inversion + negative range.** `NetworkIO.SetTxAttenData(31 - txatt)`
   with valid range `-28..+31` (`console.cs:19609-19610`). Get this wrong
   and either (a) you have full attenuation when you wanted none (radio
   "dead"), or (b) auto-att tries to walk to -28 and clips at 0 →
   feedback level never lands in (128, 181].
2. **`SetPSMox(_txachannel, false)` on un-MOX.** Without this the native
   engine's `solidmox` bit stays set (`calcc.c:907-910`) and subsequent
   PS-MOX cycles can wedge.
3. **`PSEnabled` setter must do all five steps in order**
   (`PSForm.cs:240-263`). In particular `LoadRouterControlBit(0,0,0,1)`
   *and* `PSRunCal = true` *and* `SendHighPriority(1)`. Skipping any of
   the three is a different failure mode (no feedback DDC traffic / no
   pscc() entry / firmware doesn't see the new state).
4. **`P1_DDCConfig = 6, cntrl1 = 4` for HL2 PS-MOX**
   (`console.cs:8248-8262`). And keep DDC0/1 at `rx1_rate`, not
   `ps_rate`.
5. **Auto-att HL2 NaN/inf clamps to ±10 dB**, not ±100 dB
   (`PSForm.cs:756-761`). This prevents the auto-att from slamming the
   step attenuator to its rails when feedback is briefly garbage.
6. **`NeedToRecalibrate_HL2` widens the lower bound to `> -28`**
   (`PSForm.cs:1134-1137`). Other-model predicate uses `> 0`.
7. **`PSDefaultPeak = 0.233` for HL2 P1**
   (`clsHardwareSpecific.cs:311-312`). Surface a warning when the user
   changes it. mi0bot does not auto-tune this; if Zeus does, that is a
   Zeus addition with no mi0bot reference behaviour to fall back on.
8. **`StayON` drops `PSEnabled`** (`PSForm.cs:678`) — feedback DDCs
   un-route while corrections continue to be applied by IQC. If Zeus
   keeps the feedback path live in StayON, behaviour will diverge from
   mi0bot.
9. **`TurnOFF` waits for `LRESET + !info[14]` before returning to OFF**
   (`PSForm.cs:714`). A one-shot OFF that does not poll the engine state
   leaves stale IQC corrections.
10. **`ReduceEthernetBW` re-maps HERMESLITE → ANAN10E** in both
    `UpdateDDCs` (`console.cs:8013-8016`) and `CMLoadRouterAll`
    (`cmaster.cs:488-493`). PS still works via DDC0+DDC1 in this mode.

### M. Things the source did NOT settle that bench-tester / auditor must verify

- The exact Zeus `peak` value to ship for HL2: mi0bot ships 0.233. The
  Zeus working-log claims 0.18 worked at drive=21% in a prior session.
  ps.md does not corroborate that. The mi0bot mechanism (§H) makes the
  symptom of a wrong `peak` precise, but does **not** justify a default
  other than 0.233 — that is a bench observation, not a spec change.
- ps.md mentions the MOX delay default as 0.2 s (`ps.md:230`,
  `ps.md:859`). The PSForm code path uses `udPSMoxDelay.Value` which is
  restored from user prefs; auditor should grep `udPSMoxDelay` in
  `PSForm.designer.cs` to verify the literal control default if it
  matters.
- ps.md §3.4 (line 280) describes `chkShow2ToneMeasurements` and
  `checkLoopback`; both are convenience UI features, not part of the
  PS-arm sequence, and I have not verified them against source.
- The `SetPSFeedbackRate` call on `cmaster.cs:410` only fires for
  RedPitaya (`HPSDRModel.REDPITAYA`); for HL2 the rate set in
  `CMCreateCMaster` (`cmaster.cs:457`, value 192000) is the value the
  engine sees. ps.md §9 footnote (line 948) states this; source confirms.

### N. HL2 PS-arm wire-byte sequence (mi0bot exact)

> All `ChannelMaster/*.c` paths are absolute under
> `/Users/bek/Data/Repo/github/OpenHPSDR-Thetis/Project Files/Source/`.
> Headline result: **Protocol 1 has no separate "high-priority" UDP packet.**
> Every PS-relevant register goes out via the in-band 5-byte (C0,C1,C2,C3,C4)
> control word riding inside the streaming USB-over-UDP frames on UDP port
> `RemotePort` (the metis port the radio is bound to, normally 1024). The
> host's `SendHighPriority(1)` call is, on P1, effectively a no-op — proven
> below.

#### N.1 The HL2 frame the firmware sees

`networkproto1.c:855-1187` is `WriteMainLoop_HL2`. Per call, it builds two
512-byte USB frames inside a 1024-byte buffer, then handed to
`MetisWriteFrame(0x02, FPGAWriteBufp)` (line 1184).

`MetisWriteFrame` (`networkproto1.c:212-233`) prepends an 8-byte EF FE 01
EP+seqnum header and `sendto`s the 1032-byte UDP datagram to `RemotePort`
(line 230). Endpoint `0x02` is the audio/IQ TX stream — the same packet
that normally carries TX I/Q and audio carries the C&C bytes inside its
512-byte frame headers.

Each 512-byte frame is structured:
```
byte 0..2  : 0x7F 0x7F 0x7F           sync
byte 3..7  : C0 C1 C2 C3 C4           5 control bytes (1 of 19 rounds)
byte 8..511: 504 bytes of LR audio + IQ payload (HL2: 4-DDC interleaved)
```

The host streams these frames at the audio/IQ rate. `out_control_idx`
(`networkproto1.c:27`, file-static) cycles `0 → 18 → 0` across successive
frames (`networkproto1.c:1166-1169`). With ~63 IQ samples per frame at
48 kHz, one round per ~1.3 ms — full 19-round cycle ≈ 25 ms.

**Implication for Zeus.** The PS-arm "wire sequence" is not a single
packet to send; it is a steady-state set of register values that mi0bot
mutates in-place, and the radio re-reads them every ~25 ms as the round
robin sweeps through.

#### N.2 The 5 PS-relevant control rounds in `WriteMainLoop_HL2`

Round numbers are `out_control_idx` values. C0 is always pre-OR'd with
`XmitBit` (LSB) at `networkproto1.c:882`, then OR'd with the round-specific
high bits.

| Round | C0 (no XmitBit) | What it carries                                                                                                  | mi0bot line range |
|-------|-----------------|------------------------------------------------------------------------------------------------------------------|-------------------|
| 0     | `0x00`          | C1 sample-rate-bits; C4 = `(ANT)\|0x04 duplex \|((nddc-1)<<3) bits[5:3] \|(diversity<<7)`                         | `networkproto1.c:934-956` |
| 4     | `0x1C`          | C1=`P1_adc_cntrl & 0xFF`, C2=`(P1_adc_cntrl>>8) & 0x3F`, C3=`adc[0].tx_step_attn & 0x1F`                          | `networkproto1.c:1001-1007` |
| 11    | `0x14`          | C2 bit 6 = `puresignal_run`. **C4 (during XmitBit)** = `(adc[0].tx_step_attn & 0x3F) \| 0x40` — HL2 6-bit TX att with "enable larger range" bit. C4 (during !XmitBit) = `(adc[0].rx_step_attn & 0x3F) \| 0x40`. | `networkproto1.c:1077-1089` |
| 16    | `0x24`          | C2 bit 6 = `puresignal_run` (mirror of round 11)                                                                  | `networkproto1.c:1137-1146` |

Frequency rounds 2, 3, 5, 6 (DDC0, DDC1, DDC2, DDC3 freqs respectively):
- Rounds 5/6 (`networkproto1.c:1009-1030`) hard-code DDC2 and DDC3
  frequencies to `prn->tx[0].frequency` for HL2 (`nddc != 5`). This is
  **always** done for HL2, regardless of `puresignal_run` — i.e. DDC2/DDC3
  are always tuned to TX freq when `nddc=4`.
- Rounds 2/3 (`networkproto1.c:968-996`) only re-tune DDC0/DDC1 to TX
  freq when `nddc==2 && XmitBit==1 && puresignal_run` — that branch is
  for the "Hermes-II" 2-DDC PS layout, **not HL2** (HL2 uses 4-DDC layout
  per cmaster.cs:611-635 and console.cs:8189). So on HL2 with PS armed,
  DDC0 stays on RX1 frequency and DDC1 stays on RX2 frequency.

#### N.3 Decoded byte values for HL2 PS-MOX (the steady state)

When mi0bot has PS-A on and the operator keys MOX on HL2, the round-0 and
round-11 frames the radio sees look like this (single-band, no diversity,
no antenna selector, single ANT1):

**Round 0 frame (C0=0x01 with XmitBit=1):**
```
byte 3 (C0) = 0x01                       ; XmitBit=1, no high bits
byte 4 (C1) = SampleRateIn2Bits & 0x03   ; e.g. 0x02 for 192 kHz on DDC0
byte 5 (C2) = (cw.eer & 1) | (oc_output << 1)
byte 6 (C3) = preamp/dither/random/RxOut bits (band-state dependent)
byte 7 (C4) = 0x1C                       ; ANT=00, duplex=1, nddc-1=3<<3, div=0
```
The critical byte is **C4 = 0x1C** (`networkproto1.c:953-955`):
- `0x04` duplex bit (line 953)
- `(4 - 1) << 3 = 0x18` — number of DDCs to run (line 954). For PS this MUST be `(nddc-1) = 3`.
- `(P1_en_diversity << 7) = 0` for HL2 PS (no diversity).

**Round 11 frame (C0=0x15 with XmitBit=1):**
```
byte 3 (C0) = 0x15                       ; 0x14 | XmitBit
byte 4 (C1) = preamp/mic-trs/mic-bias/mic-ptt bits
byte 5 (C2) = (line_in_gain & 0x1F) | 0x40   ; bit 6 = puresignal_run
byte 6 (C3) = user_dig_out & 0x0F
byte 7 (C4) = (tx_step_attn & 0x3F) | 0x40   ; HL2 large-range TX att, with enable
```
The critical byte is **C2 bit 6 = 1** (`networkproto1.c:1083`,
`puresignal_run << 6`).

**Round 16 frame** mirrors `puresignal_run` into C2 bit 6 of a different
C0 (`0x24`), but for HL2 with no BPF2 board this round contributes
nothing else useful (`networkproto1.c:1143`).

#### N.4 Where the host writes those bytes (the C# call → C value path)

```
PSForm.cs:246  NetworkIO.SetPureSignal(1)
  → netInterface.c:782-790  prn->puresignal_run = 1   (stored, no immediate send)
  → next round-11 fire (≤ ~25 ms): C2 bit 6 = 1

PSForm.cs:245  console.UpdateDDCs(rx2_enabled)
  ↓
  console.cs:8246-8262  P1_DDCConfig=6, DDCEnable=DDC0, SyncEnable=DDC1, cntrl1=4
  console.cs:8343-8350:
    NetworkIO.EnableRxs(DDC0)
      → netInterface.c:1200-1226   sets prn->rx[i].enable bits;
                                   recomputes nreceivers (P1).
                                   Does NOT send.
    NetworkIO.EnableRxSync(0, DDC1)
      → netInterface.c:1229-1235   sets prn->rx[0].sync = 0x02.
                                   Does NOT send.
    NetworkIO.SetDDCRate(0..3, rx1_rate)
      → netInterface.c:1247-1301   stores prn->rx[i].sampling_rate;
                                   for id==0 sets SampleRateIn2Bits.
                                   Does NOT send.
    NetworkIO.SetADC_cntrl1(4)
      → netInterface.c:917-930     stores prn->rx[0..3].rx_adc bits.
                                   Does NOT send.
                                   ** ON HL2 (P1) THIS GOES TO A REGISTER
                                      THAT IS NEVER WRITTEN TO THE WIRE
                                      — see N.5 below. **
    NetworkIO.SetADC_cntrl2(0)    similar — dead on P1.
    NetworkIO.CmdRx()
      → network.c:838-951          if (RadioProtocol == ETH) sendPacket(...,1025);
                                   ** EARLY-EXITS for P1. NO WIRE EFFECT. **
    NetworkIO.Protocol1DDCConfig(6, 0, 4, 4)
      → netInterface.c:1238-1244   P1ddcconfig=6; P1_en_diversity=0;
                                   nddc=4; P1_rxcount=4.
                                   Does NOT send.
                                   ** This is the call that sets nddc=4,
                                      causing round-0 C4 bits[5:3] = 3
                                      AND switching the host parser to
                                      MetisReadThreadMainLoop_HL2's
                                      "case 4" demux at networkproto1.c:545-549. **

PSForm.cs:247  NetworkIO.SendHighPriority(1)
  → netInterface.c:1340-1350  prn->sendHighPriority = 1; CmdHighPriority();
  → network.c:691-836         CmdHighPriority body sendPacket(...,1027);
                              ** ALSO has if-guard for ETH, see N.5. **
```

#### N.5 What mi0bot does NOT actually send to HL2 (negative result)

This is the most important finding for the bench symptom (DDC2 rx
peak=0, ~1900 blocks in). Several apparent "wire" calls are silently
no-ops on Protocol 1:

1. **`CmdRx()` is P2-only.** `network.c:948-950`:
   ```c
   if (listenSock != INVALID_SOCKET && RadioProtocol == ETH)
       sendPacket(listenSock, packetbuf, BUFLEN, 1025);
   ```
   On HL2 (`RadioProtocol == USB`), the call returns without sending.
   The DDC enable bitmask, sync mask, and per-DDC sample rates set via
   `EnableRxs/EnableRxSync/SetDDCRate` are stored in driver state but
   **never reach the HL2 firmware as a UDP packet**.
2. **`CmdHighPriority()` is P2-only.** `network.c:691-836`: same pattern
   — the packet send at the end is gated on `RadioProtocol == ETH`.
   `SendHighPriority(1)` on P1 is therefore (a) a no-op at the wire and
   (b) a flag set that lets `SetPttOut` invoke `CmdHighPriority` (which
   itself early-exits on P1, `netInterface.c:335-336`).
3. **`SetADC_cntrl1(cntrl1=4)` is P2-only on the wire.** The bytes are
   stored in `prn->rx[i].rx_adc` (`netInterface.c:921-924`) and only
   read inside `CmdRx` (which doesn't fire on P1). For HL2's actual
   wire round 4, the C1/C2 bytes come from `P1_adc_cntrl`
   (`networkproto1.c:1003-1004`), set via the **separate**
   `SetADC_cntrl_P1` setter (`netInterface.c:961-964`). That setter is
   wired in `console.cs:7109-7112` (`UpdateRXADCCtrlP1`) and only
   invoked by the `RXADCCtrl_P1` property setter (`console.cs:15575-15583`),
   which the **PS path never touches**. The default `rx_adc_ctrl_P1 = 4`
   (`console.cs:15574`) is set at startup and stays there.

   **Net consequence**: on HL2, the only PS-routing register that
   actually leaves the host on the wire is round-0 `nddc-1` bits and
   round-11 `puresignal_run` bit. **The HL2 firmware does its own
   internal routing of DDC2/DDC3 to the on-board PA-feedback tap when
   it sees `nddc=4` and `puresignal_run=1` and `XmitBit=1`** — there is
   no host-side "switch ADC routing" packet because there is nothing to
   switch (HL2 has only one ADC; the firmware re-tunes its internal
   feedback selector autonomously).

4. The `SetPureSignal` body itself comments out an immediate send
   (`netInterface.c:787-788`):
   ```c
   //if (listenSock != INVALID_SOCKET)
   //    CmdHighPriority();
   ```
   So the bit lands in `prn->puresignal_run` and waits for the next
   round-11 sweep (≤ ~25 ms latency at the audio packet rate).

#### N.6 Ordering — the answer to the team-lead's question

> "Does mi0bot send `puresignal_run` BEFORE or AFTER the DDC config?
> Before or after `cntrl1=4` on 0x1c?"

**Strictly speaking, the question doesn't have a "send order" because
nothing is sent in response to those calls.** The calls all mutate driver
state. The wire order is determined by the round-robin C&C cycle, which
the host does not control beyond setting `out_control_idx = 2` on a
TX/RX edge (`networkproto1.c:872-877`). Each round fires once per ~1.3
ms; the full 0..18 cycle takes ~25 ms.

Within that cycle, the natural wire order on a PS-arm + MOX-on edge is:
1. Round 0 (`nddc-1=3`, duplex=1 in C4) — tells firmware to expect 4-DDC streaming.
2. Rounds 1, 2, 3 (TX, DDC0, DDC1 frequencies).
3. Round 4 (`P1_adc_cntrl` C1+C2) — but for HL2 this stays at default 4 forever.
4. Rounds 5, 6 (DDC2, DDC3 frequencies = TX freq).
5. Rounds 7-10 (DDC4-6 freqs, drive level, BPF/LPF).
6. Round 11 (`puresignal_run` in C2 bit 6, TX step att in C4).
7. Rounds 12-18 (other state).

The **TX/RX edge** (`XmitBit=1` set by `SetPttOut(1)`,
`netInterface.c:328-338`) flips C0's LSB to 1 on the very next frame, AND
forces `out_control_idx = 2` if `nddc==2` (only relevant for Hermes-II,
not HL2). For HL2 there is no "jump to a particular round" on MOX.

So the answer is: **mi0bot relies on the steady-state of the C&C
round-robin to drive HL2 PS arming — it does not enforce an ordered
sequence of packets.** As long as `nddc=4`, `puresignal_run=1`, and
`XmitBit=1` are all set in driver state at the moment the next 0/11
sweep fires, the radio will see the right bytes within ≤ 25 ms.

#### N.7 What HL2 needs to actually emit DDC2 IQ

Distilled from the source for direct translation into Zeus:

1. **`nddc = 4` in driver state.** Pre-PS-arm: typically `nddc = 1`
   (RX-only, single-DDC streaming). When PS arms with MOX, the host
   must call `Protocol1DDCConfig(6, 0, 4, 4)` so `nddc = 4` ⇒ Round-0
   C4 bits [5:3] = `0b011` (`networkproto1.c:954`).
2. **`puresignal_run = 1` in driver state**, via `SetPureSignal(1)`
   (`netInterface.c:782-790`) ⇒ Round-11 C2 bit 6 = 1
   (`networkproto1.c:1083`). Mirrored in Round-16 C2 bit 6
   (`networkproto1.c:1143`) — informational only on HL2 with no BPF2.
3. **`XmitBit = 1`**, via `SetPttOut(1)` (`netInterface.c:328-338`) ⇒
   C0 LSB = 1 in every round (`networkproto1.c:882`).
4. **Host parser must demux 4 DDCs.** `MetisReadThreadMainLoop_HL2`
   `networkproto1.c:540-555`:
   ```c
   case 4:
       xrouter(0, 0, 1035, spr, prn->RxBuff[0]);   // DDC0 → RX1
       twist(spr, 2, 3, 1036);                      // DDC2+DDC3 → PS port
       xrouter(0, 0, 1037, spr, prn->RxBuff[1]);   // DDC1 → RX2
       break;
   ```
   The `nddc` global drives both the **wire byte** sent (round 0) AND
   the **payload parser** that splits the per-frame interleaved IQ into
   `prn->RxBuff[0..3]`. If host `nddc != 4` while firmware is streaming
   4 DDCs, demux is wrong; if firmware is not streaming 4 DDCs (because
   round-0 bits weren't right) then DDC2/DDC3 in the buffer will be
   garbage / zeros. **This is the bench symptom: feedback frames flow
   at the right rate, but DDC2 IQ is zero.**
5. **DDC2 / DDC3 sample rate.** `UNRESOLVED FROM MI0BOT SOURCE.` The
   HL2 round-4/round-0 C&C bytes do not carry per-DDC sample-rate
   words — only the C1 in round 0 carries the **DDC0** rate
   (`SampleRateIn2Bits`, `networkproto1.c:935`). DDC2/DDC3 sample rate
   is presumably either:
   - locked to DDC0's rate (so when HL2 PS-MOX keeps DDC0 at `rx1_rate`
     it implicitly keeps DDC2/DDC3 there too — consistent with the
     console.cs:8251-8254 comment "HL2 can work at a high sample rate"
     applying to all 4 DDCs), OR
   - hard-coded by HL2 firmware to a fixed PS rate when
     `puresignal_run=1`.

   I cannot determine which from `OpenHPSDR-Thetis/Project Files/Source`
   alone; this is HL2 firmware behaviour. Bench-tester / fixer should
   read `docs/references/protocol-1/hermes-lite2-protocol.md` (which
   the project CLAUDE.md flags as load-bearing) for the firmware's
   actual answer.

#### N.8 Cross-check against Zeus' current `Protocol1Client.SetPsEnabled`

(Read-only observation against the auditor's notes in §"Zeus
implementation map" above. Not an audit — flagging only what the spec
implies.)

The auditor's notes say `Protocol1Client.SetPsEnabled` is at
`Zeus.Protocol1/Protocol1Client.cs:451-491` and `SnapshotState` at
`:501-546` ("`numRxMinus1=3` only when `psOn && isHl2 && moxOn`"). That
is exactly the round-0 `(nddc-1)<<3` byte described above — Zeus'
`numRxMinus1` is mi0bot's `(nddc - 1)`. **If `numRxMinus1` is not 3 at
the moment the next C&C round-0 frame is built, the HL2 firmware will
not stream DDC2/DDC3 IQ**, and the DDC2 RX peak will be zero.

The fixer should verify three things in `Protocol1Client.SnapshotState`
and the C&C frame builder:
1. `numRxMinus1` becomes 3 (not 0/1) the instant `psOn && isHl2 && moxOn`
   becomes true. The gate must hold steady for the entire MOX duration,
   not flip back to 1 during round 4 (the ADC-routing round) or any
   other intra-cycle event.
2. The PS-bit equivalent of mi0bot's `puresignal_run` is being written
   into the wire byte that maps to Round 11 C2 bit 6 — i.e. the
   `0x14`-base C0 round on the HL2 wire format. The auditor's
   `WriteAttenuatorPayload` at `Zeus.Protocol1/ControlFrame.cs` cites
   `(31 - db) | 0x40` for HL2 TX-step-attn — that aligns with mi0bot's
   round-11 C4 `(att & 0x3F) | 0x40` (`networkproto1.c:1086`). So the
   round-11 frame builder is the right place to look for the
   PS-bit-in-C2-bit-6.
3. There is no host-side `SetADC_cntrl1` translation that Zeus expects
   to be effective on the wire. mi0bot proves this is dead on P1; if
   Zeus is sending an actual UDP packet for `cntrl1=4` and waiting for
   the radio to acknowledge, that packet is unnecessary noise (and
   Zeus' own log line `"p1.ps.fb DDC2(rx) peak=0"` proves the firmware
   isn't honouring it as a routing signal anyway).

#### N.9 UNRESOLVED items the source cannot settle

- **DDC2/DDC3 sample-rate setting on HL2 wire.** The HL2 round 0 C1
  carries only DDC0's sample-rate bits (2 bits). DDC2/DDC3 rate during
  PS-MOX is not in any C&C byte I can find in `WriteMainLoop_HL2`.
  Either it tracks DDC0's rate implicitly (most likely) or HL2 firmware
  hard-codes it. Need `docs/references/protocol-1/hermes-lite2-protocol.md`
  to confirm.
- **Whether HL2 firmware needs a discrete "start streams" packet on the
  PS-arm edge** in addition to the steady-state C&C bytes. mi0bot's
  `MetisReadThreadMainLoop_HL2` already runs continuously from connect
  time (`networkproto1.c:418-580`); it doesn't issue a "restart
  streams" packet on PS arm. If Zeus tears down and re-establishes the
  RX socket / read thread when PS arms, that might introduce a window
  where the firmware drops the new `nddc` value before the parser is
  ready. Source can't prove either way; bench-tester should verify the
  Zeus state machine doesn't restart the RX thread on PS arm.
- **`P1_adc_cntrl` value during PS-MOX on HL2.** Default is `4`
  (`console.cs:15574`), set at startup, never touched by PS code path.
  Round-4 C1=0x04, C2=0x00 in the steady state. Whether HL2 firmware
  even reads round-4 bytes is not visible from this source tree — for
  ANAN-class radios it does (ADC routing), for HL2 with one ADC it is
  presumably ignored. Flag as `UNRESOLVED — likely ignored by HL2
  firmware`.

### Inbound 4-DDC packet layout (mi0bot)

> Important context: **mi0bot Thetis does NOT parse inbound HL2 packets in
> C#.** The C# `NetworkIO` layer hands the raw UDP receive off to
> `ChannelMaster.dll`, and the parsing happens in C inside
> `MetisReadThreadMainLoop_HL2` (`networkproto1.c:418-580`). The
> `[DllImport("ChannelMaster.dll")]` declarations in
> `Project Files/Source/Console/HPSDR/NetworkIOImports.cs` only export the
> setters/getters and the `xrouter`/cmaster callback hook the engine uses
> to deliver demuxed IQ. So the side-by-side compare for Zeus is against C,
> not C#.

#### Inb.1 The UDP-frame envelope (HL2-side, all in C)

`MetisWriteFrame` and `MetisReadDirect` use the same metis envelope.
Inbound, `MetisReadDirect` (`networkproto1.c:141-209`) reads into an
`indgram { unsigned char readbuf[1074]; }`. A "data frame" check is
`rc == 1032` (`networkproto1.c:169`). Layout of an inbound 1032-byte
metis IQ packet (matches outbound):

```
byte 0..7   : EF FE 01 EP seq[3] seq[2] seq[1] seq[0]
              (EP for IQ-up = 0x06; the HL2 endpoint ID for inbound
               sample data — see networkproto1.c:171-198 for the
               endpoint discriminator)
byte 8..519 : USB sub-frame 0 (512 bytes)
byte 520..1031 : USB sub-frame 1 (512 bytes)
```

After header strip the 1024 bytes go to `FPGAReadBufp`
(`networkproto1.c:459 MetisReadDirect(FPGAReadBufp)`). Each sub-frame is
processed independently in the for-loop at `networkproto1.c:466-579`:

```c
for (frame = 0; frame < 2; frame++) {
    bptr = FPGAReadBufp + 512 * frame;
    if ((bptr[0] == 0x7f) && (bptr[1] == 0x7f) && (bptr[2] == 0x7f)) {
        for (cb = 0; cb < 5; cb++)
            ControlBytesIn[cb] = bptr[cb + 3];
        // ... parse the 5 inbound C&C bytes ...
        // then unpack samples starting at bptr[8]
    }
}
```

So **each 512-byte sub-frame** has the structure:

```
bptr[0..2]   = 0x7F 0x7F 0x7F                 sync
bptr[3..7]   = C0 C1 C2 C3 C4                 5 inbound C&C bytes
                                              (status/PTT/ADC overload)
bptr[8..501] = 494 bytes of interleaved IQ + mic samples (for nddc=4)
bptr[502..511] = unused / padding
```

Two USB sub-frames per UDP packet means **2 × spr** samples per packet
per DDC.

#### Inb.2 Inbound C&C decode (status bytes; not strictly needed for PS but worth knowing)

`networkproto1.c:471-521`:

```c
for (cb = 0; cb < 5; cb++)
    ControlBytesIn[cb] = bptr[cb + 3];

if (ControlBytesIn[0] & 0x80) {              // I2C response (HL2)
    // i2c read data path — used by IoBoardHl2.cs
} else {
    prn->ptt_in   =  ControlBytesIn[0] & 0x1;
    prn->dash_in  = (ControlBytesIn[0] << 1) & 0x1;     // (sic — this is the
                                                        //  HL2 mi0bot encoding)
    prn->dot_in   = (ControlBytesIn[0] << 2) & 0x1;
    switch (ControlBytesIn[0] & 0xf8) {
    case 0x00: prn->adc[0].adc_overload = ControlBytesIn[1] & 0x01;        break;
    case 0x08: prn->tx[0].exciter_power = ...; prn->tx[0].fwd_power = ...; break;
    case 0x10: prn->tx[0].rev_power = ...;     prn->user_adc0 = ...;       break;
    case 0x18: prn->user_adc1 = ...;           prn->supply_volts = ...;    break;
    case 0x20: prn->adc[0..2].adc_overload = ...;                          break;
    }
}
```

ADC-overload bit at `case 0x00 → ControlBytesIn[1] & 0x01` is the bit
the auditor's notes show Zeus reading via
`Protocol1Client.cs:?` — the auditor should confirm the same offset
(byte 4 of the sub-frame, low bit) is used.

#### Inb.3 IQ sample extraction — the exact byte stride for `nddc=4`

The unpack loop is at `networkproto1.c:523-538` (HL2 path). This is
**THE** code to compare Zeus against:

```c
spr = 504 / (6 * nddc + 2);                     // samples per ddc per sub-frame
for (iddc = 0; iddc < nddc; iddc++)
{
    for (isample = 0; isample < spr; isample++)
    {
        int k = 8 + isample * (6 * nddc + 2) + iddc * 6;
        prn->RxBuff[iddc][2 * isample + 0] = const_1_div_2147483648_ *
            (double)(bptr[k + 0] << 24 |
                     bptr[k + 1] << 16 |
                     bptr[k + 2] << 8);
        prn->RxBuff[iddc][2 * isample + 1] = const_1_div_2147483648_ *
            (double)(bptr[k + 3] << 24 |
                     bptr[k + 4] << 16 |
                     bptr[k + 5] << 8);
    }
}
```

For `nddc = 4`:

- **Sample-tuple stride** = `6 * 4 + 2 = 26 bytes` (4 DDCs × 6 IQ bytes
  + 2 mic bytes).
- **Samples per sub-frame** `spr = 504 / 26 = 19`.
- **Per UDP packet (2 sub-frames):** `2 × 19 = 38 samples per DDC`.
- **Within one sample-tuple, the 26 bytes are laid out as:**

  ```
  offset  bytes  contents
  ─────── ─────  ──────────────────────────────────────
  0..2    3      DDC0 I (24-bit, MSB-first)
  3..5    3      DDC0 Q (24-bit, MSB-first)
  6..8    3      DDC1 I
  9..11   3      DDC1 Q
  12..14  3      DDC2 I               <-- PS RX feedback
  15..17  3      DDC2 Q               <-- PS RX feedback
  18..20  3      DDC3 I               <-- PS TX loopback
  21..23  3      DDC3 Q               <-- PS TX loopback
  24..25  2      mic sample (16-bit, MSB-first; see Inb.4)
  ```

- **Sub-frame absolute byte offsets** (start at `bptr[8]`, so add 8):

  ```
  Sample 0:
    DDC0_I   bptr[ 8.. 10]    DDC0_Q   bptr[11.. 13]
    DDC1_I   bptr[14.. 16]    DDC1_Q   bptr[17.. 19]
    DDC2_I   bptr[20.. 22]    DDC2_Q   bptr[23.. 25]
    DDC3_I   bptr[26.. 28]    DDC3_Q   bptr[29.. 31]
    mic       bptr[32.. 33]
  Sample 1: base 8 + 26 = 34
    DDC0_I   bptr[34.. 36]    DDC0_Q   bptr[37.. 39]
    DDC1_I   bptr[40.. 42]    DDC1_Q   bptr[43.. 45]
    DDC2_I   bptr[46.. 48]    DDC2_Q   bptr[49.. 51]
    DDC3_I   bptr[52.. 54]    DDC3_Q   bptr[55.. 57]
    mic       bptr[58.. 59]
  ...
  Sample 18: base 8 + 18*26 = 476
    DDC0_I   bptr[476..478]   ...   DDC3_Q   bptr[497..499]
    mic       bptr[500..501]
  ```

  Total used: 502 bytes (8 header + 494 payload). 10 bytes padding.

**Interleaving model.** Sample-major, then DDC-minor. All 4 DDCs' I/Q for
time-step 0 come first (with the mic sample tail), then all 4 DDCs' I/Q
for time-step 1. **Not** per-DDC blocks. This is unambiguous from the
indexing `k = 8 + isample * stride + iddc * 6`.

#### Inb.4 24-bit endianness and sign

```c
(double)(bptr[k + 0] << 24 | bptr[k + 1] << 16 | bptr[k + 2] << 8)
```

`bptr` is `unsigned char*` (`networkproto1.c:423 calloc(1024, sizeof(unsigned char))`).
After the byte-shifts:

- `bptr[k+0] << 24` — most significant byte at bits [31:24].
- `bptr[k+1] << 16` — middle byte at bits [23:16].
- `bptr[k+2] << 8` — least significant byte (of the 24-bit sample) at bits [15:8].
- Bits [7:0] are zero.

Because the implicit promotion of `unsigned char` is to `int` (signed),
and the explicit `(double)` cast applies to the OR'd `int` value, the
result is **signed 32-bit**: a 24-bit MSB-first sample left-justified
into the upper 24 bits of a signed int. The implicit cast through `int`
sign-extends from bit 31 (which is bit 23 of the original 24-bit
sample). Then `* (1.0 / 2147483648.0)` (= `1.0 / 2^31`,
`network.h:452`) normalises into `[-1.0, +1.0)`.

So:
- **Endianness on the wire: big-endian (MSB-first), 3 bytes per
  component.**
- **Sign: 24-bit two's complement, sign bit is `bptr[k+0] & 0x80`,
  carried into bit 31 by the `(int)` promotion of the OR-expression.
  Trailing 8 LSBs are zero (no padding sample data).**
- **Normalisation: divide by `2^31`, not `2^23`.** The "true" 24-bit
  range is `[-2^23, +2^23)`, but the left-shift by 8 puts the sample at
  `[-2^31, +2^31)` (with the bottom 8 bits zero), so `/2^31` is the
  correct fixed-point divisor.

#### Inb.5 Demux + routing (where samples actually go to the PS engine)

Once `prn->RxBuff[0..3]` hold `spr` samples each (per sub-frame), the
HL2 read loop dispatches by `nddc`:

```c
// networkproto1.c:540-555
switch (nddc) {
case 2:  twist(spr, 0, 1, 1035);                                   break;
case 4:  xrouter(0, 0, 1035, spr, prn->RxBuff[0]);   // DDC0 → RX1
         twist(spr, 2, 3, 1036);                      // DDC2+DDC3 → PS
         xrouter(0, 0, 1037, spr, prn->RxBuff[1]);   // DDC1 → RX2
         break;
case 5:  twist(spr, 0, 1, 1035); twist(spr, 3, 4, 1036);
         xrouter(0, 0, 1037, spr, prn->RxBuff[2]);                 break;
}
```

`twist()` (`networkproto1.c:259-270`) interleaves two streams into
`RxReadBufp` as 4-tuples then routes them:

```c
void twist(int nsamples, int stream0, int stream1, int port) {
    for (i=0, j=0; i < 2*nsamples; i+=2, j+=4) {
        RxReadBufp[j+0] = RxBuff[stream0][i+0];   // I of stream0 (= DDC2 → PS RX I)
        RxReadBufp[j+1] = RxBuff[stream0][i+1];   // Q of stream0 (= DDC2 → PS RX Q)
        RxReadBufp[j+2] = RxBuff[stream1][i+0];   // I of stream1 (= DDC3 → PS TX I)
        RxReadBufp[j+3] = RxBuff[stream1][i+1];   // Q of stream1 (= DDC3 → PS TX Q)
    }
    xrouter(0, 0, port, 2 * nsamples, RxReadBufp);
}
```

So for HL2 PS-MOX (`nddc=4`), the buffer that ends up at the cmaster
"port 1036" callback (= the PS engine's pscc input) is laid out as:

```
[DDC2_I, DDC2_Q, DDC3_I, DDC3_Q, DDC2_I, DDC2_Q, DDC3_I, DDC3_Q, ...]
```

— `2 * spr` 4-tuples per sub-frame call (2*19 = 38 doubles per
sub-frame, 152 doubles total per UDP packet, since each "sample" emits
4 doubles).

Mapping in PS-engine terms:
- `stream 2 = DDC2 = PS RX feedback` (PA-output sampler)
- `stream 3 = DDC3 = PS TX loopback` (TX DAC reference)
- The router `(state index) (MOX|PS) → PS callid bit` table is the
  HL2 FOUR_DDC table at `cmaster.cs:611-635`. With PS armed AND MOX
  asserted, the callid bit at indexes 5/7 fires the PS-engine callback;
  otherwise the data goes to a NULL callback and is dropped.

#### Inb.6 Mic / TX-IQ-loopback byte (for completeness)

`networkproto1.c:558-572`:

```c
for (isamp = 0; isamp < spr; isamp++) {
    int k = 8 + nddc * 6 + isamp * (2 + nddc * 6);
    mic_decimation_count++;
    if (mic_decimation_count == mic_decimation_factor) {
        mic_decimation_count = 0;
        prn->TxReadBufp[2*mic_sample_count + 0] = const_1_div_2147483648_ *
            (double)(bptr[k+0] << 24 | bptr[k+1] << 16);
        prn->TxReadBufp[2*mic_sample_count + 1] = 0.0;
        mic_sample_count++;
    }
}
```

For `nddc=4` the per-sample mic offset is `k = 8 + 24 + isamp*26 =
32 + 26*isamp`. So sample 0's mic byte-pair is `bptr[32..33]`, sample 1's
is `bptr[58..59]`, etc. — confirming the layout in Inb.3. **The mic
sample is 16-bit MSB-first** (only two bytes, shifted to bits [31:16]),
and Q is forced to 0 (mic is real-valued).

#### Inb.7 Side-by-side check items for the auditor

When `Zeus.Protocol1.Protocol1Client.HandlePs4DdcPacket` is being
audited, verify each of the following matches mi0bot's C exactly:

1. **`spr = 19` per sub-frame for nddc=4.** Some implementations
   compute `spr = 504 / (numDdc * 6 + 2)`; others hard-code. Either is
   fine as long as the result is 19.
2. **The two sub-frames are processed independently** with identical
   demux. Skipping the second sub-frame halves the IQ rate (would show
   as a 96 kHz cadence, not 192 kHz).
3. **Sub-frame base offsets are 0 and 512** within the 1024-byte
   payload (after stripping the 8-byte metis header).
4. **Per-sample base inside a sub-frame: `8 + isample * 26`.** Forgetting
   the `+8` (control-byte offset) drops the first sample into the C&C
   bytes — would produce wildly wrong DDC0 values but might still look
   like "data" if the auditor only checks DDC2.
5. **Per-DDC offset within a sample-tuple: `iddc * 6`.** A `* 8` typo
   (mistakenly counting mic bytes per DDC) would cause DDC2 to read
   from inside DDC3's slot or past the end of the tuple — likely
   producing zeros for DDC2 if it overruns into the unused
   `bptr[502..511]` padding.
6. **24-bit MSB-first sign extension**: byte0 `<< 24`, byte1 `<< 16`,
   byte2 `<< 8`, then signed-int promotion + divide by `2^31`. **A
   `<< 16, << 8, << 0` shift (right-aligning into 24 bits) without
   sign-extending would silently zero out negative samples** — this is
   a subtle bug that produces non-zero peak only when the signal is
   positive-going. If Zeus's `peak` is always 0, this is unlikely to be
   the bug; but if Zeus shows asymmetric peaks (positive only), it is.
7. **DDC2/DDC3 are at sample-tuple offsets 12 and 18, not 0/6 (which
   are DDC0/DDC1)**. A common implementation mistake: assume DDC0/DDC1
   "go away" when nddc switches from 2 to 4 and DDC2/DDC3 take their
   slots. mi0bot does NOT do that — DDC0/DDC1 stay in their offsets and
   DDC2/DDC3 are appended after them.
8. **The whole loop only runs when the inbound EP discriminator says
   "IQ data"** (the metis EP byte at offset 3 of the UDP packet). For
   HL2 inbound IQ this is `EP = 0x06`. If the auditor sees Zeus running
   the parser on a non-IQ EP frame (e.g. discovery, ack), `bptr[0..2]`
   won't be `0x7F 0x7F 0x7F` and the `if` at `networkproto1.c:469`
   would skip — but if Zeus checks differently it might run garbage.

**Most likely cause of `DDC2_peak = 0 AND DDC3_peak = 0` while block
counter ticks at 192 kHz cadence:** the firmware is *not* streaming
DDC2/DDC3 (i.e. Round-0 C4 bits[5:3] are not = `3` on the wire — see
§N.6/N.7 above). A pure parser bug would more typically produce
*nonsense values* in DDC2/DDC3, not exact zeros for both, unless the
parser is reading from the unused padding region (`bptr[502..511]`,
which calloc'd to zero). Inb.7 item 5 ("`*8` instead of `*6` per DDC")
would produce that exact symptom — worth a quick check before
concluding the bug is upstream of the parser.

### HL2 register 0x0a and 0x2b semantics on PS-arm

> **Bottom line up front:** mi0bot writes register `0x0a` bit 22 only
> (the PureSignal-run bit), with a duplicate at `0x12` C2 bit 6.
> mi0bot does **NOT** write to register `0x2b` at all — searched
> exhaustively across `OpenHPSDR-Thetis/Project Files/Source/` for the
> literal `0x2b`, the C0 value `0x56` (= `0x2b << 1`), the strings
> `predist`, `subindex`, `sub_index`, `SetPSPredist`, `PSPredist`,
> `extended_addr`, `ExtAddr`, `ExtRegister`. **Zero hits in source.**
> If Zeus's commit `1e53807 feat(p1): HL2 PureSignal register encoders
> (0x0a[22], 0x2b[19:16])` writes register `0x2b`, that write is beyond
> mi0bot — and is the prime suspect for the "PA output dies on PS arm"
> symptom.

#### Reg.1 Bit-numbering convention (so we agree on which bit is which)

HL2 / HPSDR Protocol 1 registers are 32-bit, addressed by `C0[7:1]`
(C0 LSB is the TX/RX bit). The four data bytes that follow are mapped:

```
C1 = data[31:24]       (sent first after C0)
C2 = data[23:16]
C3 = data[15:8]
C4 = data[7:0]         (sent last)
```

Under this convention:
- **`0x0a[22]`** is **C2 bit 6** of the C0=`0x14` (=`0x0a << 1`) frame.
  Calculation: bit 22 lives in C2 (bits[23:16]); within C2 the bit
  position is `22 - 16 = 6`.
- **`0x2b[19:16]`** is **C2 bits [3:0]** of the C0=`0x56` (=`0x2b << 1`)
  frame.
- **`0x2b[31:24]`** is **C1 bits [7:0]** of the same frame.

This matches `docs/references/protocol-1/hermes-lite2-protocol.md:366-367`
and `:393-400` and the PR #119 review note that PS-arm bit is **C2 bit
6, not C3 bit 6**.

#### Reg.2 Register 0x0a bit 22 in mi0bot — exact write site

mi0bot writes `0x0a[22]` inside `WriteMainLoop_HL2` case 11
(`networkproto1.c:1077-1089`):

```c
case 11: //Preamp control 0x0a
    C0 |= 0x14;                                              // address 0x0a
    C1 = (rx[0].preamp & 1) | ((rx[1].preamp & 1) << 1) | ((rx[2].preamp & 1) << 2)
         | ((rx[0].preamp & 1) << 3) | ((mic.mic_trs & 1) << 4)
         | ((mic.mic_bias & 1) << 5) | ((mic.mic_ptt & 1) << 6);
    C2 = (mic.line_in_gain & 0b00011111) | ((puresignal_run & 1) << 6);  // <-- HERE
    C3 = user_dig_out & 0b00001111;
    if (XmitBit) C4 = (adc[0].tx_step_attn & 0b00111111) | 0b01000000;   // HL2 6-bit TX att + enable
    else         C4 = (adc[0].rx_step_attn & 0b00111111) | 0b01000000;
    break;
```

**Set when:** `prn->puresignal_run == 1`, set by `SetPureSignal(1)`
(`netInterface.c:782-790`) called from `PSForm.cs:246`. There is no
other gating — the bit is always written into C2 bit 6 of the next
round-11 C&C frame, regardless of MOX, regardless of PA state,
regardless of TX drive level.

**Cleared when:** `SetPureSignal(0)` is called. Same wire path with
C2 bit 6 = 0.

**Mirror at `0x12` C2 bit 6** — `WriteMainLoop_HL2` case 16
(`networkproto1.c:1137-1146`):

```c
case 16: // BPF2 0x12
    C0 |= 0x24;                                              // address 0x12
    C1 = (BPF2 selectors);
    C2 = (xvtr_enable & 1) | ((puresignal_run & 1) << 6);    // <-- mirror
    C3 = 0;
    C4 = 0;
    break;
```

So the puresignal_run bit is sent **twice per ~25 ms cycle**: once via
register 0x0a (round 11) and once via register 0x12 (round 16). The HL2
gateware honours either. mi0bot writes both
(`networkproto1.c:1083` + `:1143`); pihpsdr only writes 0x0a.

**No additional state change accompanies setting `puresignal_run = 1`.**
It does NOT (in mi0bot) cause:
- A change to TX drive level (case 10 C1 = `tx[0].drive_level`,
  untouched by PS path).
- A change to TX step att encoding (case 11 C4 = `tx_step_attn` with
  `0x40` enable bit, unconditionally on HL2 during XmitBit).
- A change to LPF/BPF selection (case 10 C3/C4, case 16 C1).
- A change to ANT selection (case 0 C4 ANT bits).
- A change to PA-on bit (case 10 C3 bit 7 = `tx[0].pa & 1`). This bit
  controls the HL2 internal PA enable; mi0bot does **not** modify it
  when PS arms.

#### Reg.3 Register 0x2b in mi0bot — does not exist on the wire

I performed two passes over `OpenHPSDR-Thetis/Project Files/Source/`:

1. `grep -rn "0x2b\|0x56" .../ChannelMaster/ .../Console/clsHardwareSpecific.cs .../Console/HPSDR/`
   — only hits are unrelated (`KeyMapper.cs:56` "Execute" key,
   `KeyMapper.cs:186` "V" key, none in protocol or PS files).
2. `grep -rn "subindex\|sub_index\|predist\|SetPSPredist\|PSPredist"
   .../Source/` — **zero matches** anywhere in the tree (excluding
   NuGet package XML and System.Drawing docs).

Cross-referenced against the C0 round-table in `WriteMainLoop_HL2`
(`networkproto1.c:932-1163`): the 19 rounds emit C0 high-bits
`0x00, 0x02, 0x04, 0x06, 0x1c, 0x08, 0x0a, 0x0c, 0x0e, 0x10, 0x12,
0x14, 0x16, 0x1e, 0x20, 0x22, 0x24, 0x2e, 0x74` — **`0x56` is not in
the list**. Same exhaustive list applies to the non-HL2
`WriteMainLoop` (`networkproto1.c:585-853`) — also no `0x56`.

There is no "extended address space" entry point in the mi0bot
ChannelMaster source either (no `WriteRegister`, `WriteExtAddr`,
`SendDirectRegister`, or similar generic register-write API). All
writes go via the cyclic C&C round-robin.

**Net.** mi0bot achieves the entire PS-arm sequence with:
- `Protocol1DDCConfig(...)` → host driver `nddc` → round-0 C4 bits[5:3]
  = `nddc - 1`.
- `SetPureSignal(1)` → host driver `puresignal_run = 1` → round-11 C2
  bit 6 = 1, AND round-16 C2 bit 6 = 1.

**That is the entire wire footprint for mi0bot PS-arming.** No 0x2b
writes, no extra register pokes, no separate UDP packet, no
high-priority flush.

#### Reg.4 Cross-check: project HL2 reference doc claim about 0x2b

`docs/references/protocol-1/hermes-lite2-protocol.md:393-400` says:

> `0x2b[31:24]` = predistortion **subindex** (C1).
> `0x2b[19:16]` = predistortion **value** (C2 bits [3:0]).
> mi0bot writes the same byte layout via the open address space
> (`netInterface.c` extended-write path).

**The "mi0bot writes the same byte layout" claim is not supported by
mi0bot source.** I cannot find any extended-write path in
`netInterface.c` or anywhere in the mi0bot ChannelMaster tree, and
the `0x2b` / `0x56` searches return zero hits in source.

The reference doc's `0x2b` discussion appears to be derived from
**pihpsdr** (the doc cites `pihpsdr/src/old_protocol.c lines 895-980,
1043-1094` as a cross-reference). The "mi0bot writes the same byte
layout" sentence is, on the evidence in front of me, **incorrect** —
mi0bot writes nothing to `0x2b`. CLAUDE.md's hard rule is
mi0bot-authoritative, so the conservative read is: **Zeus should not
write `0x2b` either**.

#### Reg.5 What `0x0a[22]` actually does on the HL2 gateware

From `docs/references/protocol-1/hermes-lite2-protocol.md:402-460`
(itself derived from mi0bot + cross-checked against pihpsdr): when
`0x0a[22] = 1` and the radio is keyed, the HL2 gateware re-points its
dedicated feedback ADC (ADC1) through one of the existing DDCs. The
samples come back inside the existing EP6 RX IQ stream — no new packet
type, no new endpoint. Which DDC carries feedback depends on the host's
`nddc` count and the `cntrl1` ADC-mapping byte. **All of this is
gateware-side**; the host does not need to "configure feedback routing"
beyond setting the bit and the receiver count.

This means **the only register writes mi0bot needs for PS to work on
HL2 are `0x0a[22] = 1` (C2 bit 6 of round 11) plus the matching `nddc`
in round 0 C4 bits[5:3]**. No `0x2b`. No "predistortion enable"
register. No subindex / value writes.

**The HL2 gateware's PS-mode firmware does not need predistortion
coefficients pushed via `0x2b`.** The host's WDSP `pscc()` engine
computes correction coefficients; those coefficients are baked into
the TX I/Q the host sends out via EP2 (predistorted samples), and the
gateware just transmits them. There is no "send the gateware new
predistortion coefficients via 0x2b" handoff in mi0bot — the host
applies the predistortion to its own TX samples before sending them.

#### Reg.6 What Zeus is doing differently (the prime suspect)

(Read-only inference from the auditor's notes in §"Zeus implementation
map" + the commit message of `1e53807 feat(p1): HL2 PureSignal register
encoders (0x0a[22], 0x2b[19:16])`.)

The auditor's notes show Zeus has `SetPsPredistortion(value, subindex)`
in `Protocol1Client.cs:451-491`. The new commit `1e53807` apparently
wires that into `0x2b` register writes. Per Reg.3 / Reg.5, **mi0bot
does no such writes** — the gateware's PS firmware handles all
predistortion internally once `0x0a[22] = 1`.

Hypothesis (for the team-lead / fixer to validate at the bench):

1. The `0x2b` register on HL2 may be repurposed for something other
   than PS predistortion (PA-bias control, DAC gain trim, an
   experimental feature defaulting "on" at boot).
2. Writing **zero or wrong-encoded values** to `0x2b[19:16]` /
   `0x2b[31:24]` could mute the PA — most likely interpretation is
   that `0x2b` writes are programming a PA-related register that
   defaults-on at boot, and Zeus's writes (zeros, partial values, or
   wrong-encoding values) are turning it off.
3. **Removing the `0x2b` writes entirely** should restore PA output
   without breaking PS — that is what mi0bot proves by counterexample.
   mi0bot has working PS on HL2 with zero `0x2b` writes.

Concrete experiment for the fixer:

- Bypass / no-op `Protocol1Client.SetPsPredistortion` (so no `0x2b`
  bytes ever leave the host).
- Keep `0x0a[22]` writes intact (PS-arm bit, mi0bot-authoritative).
- Re-bench: if PA output returns AND PS still arms (DDC2/DDC3 stream
  fills, info[5] cor.cnt increments), the `0x2b` writes were the bug.
- If PA still dies, suspect the per-tick frame builder is corrupting
  some other register byte during the round-robin. Verify the round-11
  C4 byte is `0x40` (= att=0 with HL2-large-range enable bit set) when
  no att is requested, and **NOT** `0x00`. C4=`0x00` in round 11
  disables the HL2 TX att enable bit (bit 6); the gateware behaviour
  with that enable bit cleared mid-TX is itself uncertain and could
  explain a PA-mute.

#### Reg.7 UNRESOLVED items

- **What `0x2b` actually maps to on the HL2 gateware.** The project HL2
  reference doc claims "predistortion subindex/value" (pihpsdr-derived,
  not mi0bot-derived). The HL2 gateware Verilog at
  `github.com/softerhardware/Hermes-Lite2` is the authoritative answer
  but is outside this task's scope. **Until the gateware mapping is
  confirmed, treat `0x2b` writes as load-bearing for whatever
  HL2-firmware-dependent feature they program — assuming they're
  PS-related is unsafe.**
- **Whether HL2 gateware versions vary in their `0x2b` interpretation.**
  `0x0a[22]` is honoured from gateware v7.2 onwards
  (`hermes-lite2-protocol.md:489-497`). No equivalent claim exists for
  `0x2b`.
- **Whether removing `0x2b` writes alone is sufficient, or whether
  `1e53807` also altered `0x0a[22]` bit position / encoding.** The
  PR #119 "C2 bit 6 not C3 bit 6" review note flags this as a
  historical mistake — fixer should verify it has not regressed in
  `1e53807`.

### pscc tx_buf source on HL2 (mi0bot)

> **Headline answer:** mi0bot's `pscc()` tx_buf is **NOT** the host-side
> TX-IQ ring. It is the **second of two paired DDC streams that come
> back from the radio** in the inbound EP6 IQ packet — the HL2 gateware
> itself provides the TX reference as a TX-DAC loopback DDC. Per-sample
> alignment is automatic because both pscc tx and pscc rx are interleaved
> in the same UDP-packet sample-tuple, time-locked by the gateware. **No
> host-side ring buffer, no delay compensation, no alignment logic.**
>
> Implication for Zeus's V1 fix: `_psTxRingI/Q` (host-side TX-IQ
> recording at `Protocol1Client.cs:191-212`) is the **wrong** source for
> pscc tx_buf if Zeus is mimicking mi0bot. The right source is the
> second DDC's IQ samples from the same wire packet — which means
> Zeus's wire layout MUST cause the HL2 gateware to produce a
> TX-DAC-loopback DDC stream. mi0bot achieves this with the 4-DDC
> layout (DDC3 = TX loopback). Whether the HL2 gateware honours an
> equivalent loopback DDC in the 2-DDC paired layout is **UNRESOLVED**
> from mi0bot source — see PSC.6 below.

#### PSC.1 The actual call site (one place, in C, in ChannelMaster.dll)

`OpenHPSDR-Thetis/Project Files/Source/ChannelMaster/sync.c:44-67`,
`InboundBlock(int id, int nsamples, double** data)`:

```c
PORT
void InboundBlock (int id, int nsamples, double** data)
{
    switch (id)
    {
    case 0: // diversity receivers
        xdivEXT (0, nsamples, data, psyn->divbuff);
        Inbound (0, nsamples, psyn->divbuff);
        break;
    case 1: // puresignal receivers
        pscc (chid (inid (1, 0), 0),
            nsamples,
            data[_InterlockedAnd (&psyn->xmtr[0].ps_tx_idx, 0xffffffff)],   // <-- TX
            data[_InterlockedAnd (&psyn->xmtr[0].ps_rx_idx, 0xffffffff)]);  // <-- RX
        break;
    case 2: // synchronous receivers
        Inbound(0, nsamples, data[0]);
        Inbound(1, nsamples, data[1]);
        break;
    case 3: // send synchronous only to first software receiver
        Inbound(0, nsamples, data[0]);
        break;
    }
}
```

`pscc` (lowercase) is the C entry point at `wdsp/calcc.c:617`:
```c
void pscc (int channel, int size, double* tx, double* rx)
```

`psccF` (the C# DllImport at `PSForm.cs:1039-1040`) is the float-buffer
variant; **it is declared in C# but never called from C#** — `grep -rn
"psccF\b\|puresignal\.psccF" .../Source/Console/` returns only the
DllImport line, no callers. All actual PS feedback flows through the
C-internal `pscc()` chain in ChannelMaster.dll. Zeus's equivalent
`FeedPsFeedbackBlock(...)` → `psccF(...)` path is therefore Zeus-specific:
mi0bot does not feed PS samples from the C# layer.

#### PSC.2 What lands in `data[ps_tx_idx]` and `data[ps_rx_idx]`

The `data` parameter is `double**` — an array of pointers to
per-stream IQ blocks. For a paired 2-stream callback, `data[0]` and
`data[1]` are the two streams from the cmaster router's de-interleave
step. The mapping is set at startup in
`OpenHPSDR-Thetis/Project Files/Source/Console/cmaster.cs:454-458`:

```csharp
// note:  if future models have different settings, these calls could be moved to
//      CMLoadRouterAll() which is called each time the receiver model changes.
SetPSRxIdx(0, 0);   // txid = 0, all current models use Stream0 for RX feedback
SetPSTxIdx(0, 1);   // txid = 0, all current models use Stream1 for TX feedback
```

`SetPSRxIdx(0, 0)` writes `psyn->xmtr[0].ps_rx_idx = 0`
(`sync.c:75-79`); `SetPSTxIdx(0, 1)` writes `ps_tx_idx = 1`
(`sync.c:69-73`). These are set ONCE, at `CMCreateCMaster()` boot, and
never mutated by mi0bot. The comment "all current models use Stream0
for RX feedback" is load-bearing — every per-model router table in
`cmaster.cs` arranges its paired streams so that `(stream 0, stream 1)
== (PS RX feedback, PS TX reference)`.

So unconditionally:
- **`pscc(... tx, rx)` → `tx = data[1]`, `rx = data[0]`.**
- **`data[0]` = Stream 0 = PS RX feedback (post-PA antenna-coupler tap).**
- **`data[1]` = Stream 1 = PS TX reference (gateware TX-DAC loopback).**

#### PSC.3 How the router pairs the streams (the de-interleave step)

The two streams arrive interleaved in the EP6 IQ packet. The
`MetisReadThreadMainLoop_HL2` HL2-specific dispatcher
(`networkproto1.c:540-555`) emits per-DDC blocks via `xrouter`, with
the paired PS streams re-interleaved by `twist()` for cmaster routing:

```c
// HL2 4-DDC PS-MOX path:
case 4:
    xrouter(0, 0, 1035, spr, prn->RxBuff[0]);   // DDC0 → port 1035 (RX1 audio)
    twist(spr, 2, 3, 1036);                      // DDC2+DDC3 → port 1036 (PS engine)
    xrouter(0, 0, 1037, spr, prn->RxBuff[1]);   // DDC1 → port 1037 (RX2 audio)
    break;
```

`twist(spr, 2, 3, 1036)` (`networkproto1.c:259-270`):

```c
void twist (int nsamples, int stream0, int stream1, int port) {
    for (i = 0, j = 0; i < 2*nsamples; i += 2, j += 4) {
        prn->RxReadBufp[j+0] = prn->RxBuff[stream0][i+0];   // I of DDC2 → buf[4i+0]
        prn->RxReadBufp[j+1] = prn->RxBuff[stream0][i+1];   // Q of DDC2 → buf[4i+1]
        prn->RxReadBufp[j+2] = prn->RxBuff[stream1][i+0];   // I of DDC3 → buf[4i+2]
        prn->RxReadBufp[j+3] = prn->RxBuff[stream1][i+1];   // Q of DDC3 → buf[4i+3]
    }
    xrouter(0, 0, port, 2 * nsamples, prn->RxReadBufp);
}
```

Then `xrouter(... port=1036, 2*spr, RxReadBufp)` (`router.c:70-108`)
looks up the cmaster router table, finds function=2 (block call) at
the active control index, and de-interleaves the 4-tuple buffer back
into per-stream `ptrs[]` for `InboundBlock(callid, sps, ptrs)`:

```c
case 2:
    for (j = 0; j < a->nstreams[bport]; j++) {     // for each stream
        si = j * sps;                              // stream index
        ptrs[j] = &(a->ddata[2 * si]);             // save pointer to stream
        for (k = 0; k < sps; k++) {                // for each sample
            a->ddata[2 * (si + k) + 0] = data[2 * (a->nstreams[bport] * k + j) + 0];
            a->ddata[2 * (si + k) + 1] = data[2 * (a->nstreams[bport] * k + j) + 1];
        }
    }
    InboundBlock(a->callid[bport][i][ctrl], sps, ptrs);
    break;
```

For port 1036 with `nstreams[1036] = 2`, the de-interleave produces:
- `ptrs[0]` → `a->ddata[0..2*sps-1]` containing `[DDC2_I, DDC2_Q]` × sps
- `ptrs[1]` → `a->ddata[2*sps..4*sps-1]` containing `[DDC3_I, DDC3_Q]` × sps

So at `InboundBlock(id=1, sps, ptrs)` with `ps_rx_idx=0, ps_tx_idx=1`:
- `pscc rx = ptrs[0] = DDC2 IQ block` (PS-RX feedback / PA tap)
- `pscc tx = ptrs[1] = DDC3 IQ block` (PS-TX loopback from gateware)

#### PSC.4 Per-sample alignment — automatic, no host logic

Both DDC2 and DDC3 are streamed by the HL2 gateware in the **same
UDP packet, same sample-tuple**. The 4-DDC packet layout (per the
"Inbound 4-DDC packet layout" subsection earlier in §N) is:

```
[DDC0 6B][DDC1 6B][DDC2 6B][DDC3 6B][mic 2B]    ← sample 0
[DDC0 6B][DDC1 6B][DDC2 6B][DDC3 6B][mic 2B]    ← sample 1
...
```

DDC2's I/Q at sample index `i` and DDC3's I/Q at sample index `i`
arrive in the same 26-byte sample-tuple, separated by 6 bytes (DDC2:
bytes [12..17], DDC3: bytes [18..23] within the tuple). The gateware
guarantees they are **the same time instant** — both DDCs run from
the same FPGA clock, just routed from different sources (DDC2 from the
PA-feedback ADC path, DDC3 from the TX-DAC loopback path).

After `twist()` interleaves them and `xrouter` de-interleaves them,
the `ptrs[0][2*i..2*i+1]` and `ptrs[1][2*i..2*i+1]` doubles still
correspond to the same sample time. `pscc()` consumes them directly:

```c
// calcc.c LCOLLECT loop, abbreviated:
env = sqrt(tx[2*i+0]*tx[2*i+0] + tx[2*i+1]*tx[2*i+1]);  // TX magnitude at sample i
// ...
a->txs[2*m+0] = tx[2*i+0]; a->txs[2*m+1] = tx[2*i+1];   // TX I/Q sample i
a->rxs[2*m+0] = rx[2*i+0]; a->rxs[2*m+1] = rx[2*i+1];   // RX I/Q sample i
```

`tx[2*i]` and `rx[2*i]` are read at the same loop index `i`, so they
**must** be sample-time-aligned. mi0bot relies on the gateware to
guarantee this; there is no host-side delay-line, no ring-buffer
lookup, no PSTXDelay-style sample-shift in the pscc input path.

> The `SetPSTXDelay` API (`PSForm.cs:1041`, `calcc.c:993-1013`) DOES
> exist — but it is an **analog amp-delay compensation** (the "AMP
> Delay (ns)" UI control, default 150 ns at 20 ns granularity). It
> compensates for LPF group delay between the DAC and the coupler,
> not for any host↔wire alignment. It runs inside the WDSP delay
> lines AFTER pscc has consumed paired tx/rx samples. So mi0bot's
> alignment story really is "0 host-side, gateware does it".

#### PSC.5 The 2-DDC ANAN-class equivalent (for Zeus's V1 fix reference)

mi0bot DOES support a 2-DDC paired PS layout — but for ANAN-10E and
ANAN-100B, **NOT** for HL2. The router table is at
`cmaster.cs:592-610` (the Protocol-1 non-loopback path):

```csharp
case HPSDRModel.ANAN10E:
case HPSDRModel.ANAN100B:
    int[] TWO_DDC_Function = {
        2, 2, 2, 2, 2, 2, 2, 2,     // Call 0, all ctrl indexes: function=2 (block call)
        0, 0, 0, 0, 0, 2, 0, 2      // Call 1, only at ctrl=5,7 (TX|PS combinations)
    };
    int[] TWO_DDC_Callid = {
        2, 2, 2, 2, 2, 1, 2, 1,     // Call 0: callid=2 normally, =1 at PS|MOX (→ pscc)
        0, 0, 0, 0, 0, 2, 0, 2      // Call 1: callid=2 at PS|MOX (→ Inbound id=2 → RX1+RX2)
    };
    int[] TWO_DDC_nstreams = { 2 };    // single port (1035), 2 streams paired
    LoadRouterAll((void*)0, 0, 1, 2, 8, pstreams, pfunction, pcallid);
```

For ANAN-10E PS-MOX (ctrl index 5 = MOX|PS, no DIV):
- Call 0 fires `InboundBlock(id=1, sps, ptrs)` → `pscc(ch, sps,
  ptrs[1]=DDC1, ptrs[0]=DDC0)`.
- Call 1 fires `InboundBlock(id=2, sps, ptrs)` → `Inbound(0, ...);
  Inbound(1, ...);` (both DDCs go to RX1+RX2 audio for monitoring).

In ANAN-10E 2-DDC PS-MOX (per `networkproto1.c:986-996` in the non-HL2
WriteMainLoop, and `cntrl1=4` from `console.cs:8333`):
- DDC0 retuned to TX freq, on ADC0
- DDC1 retuned to TX freq, on ADC1 (cntrl1=4 → bits[3:2]=01 → DDC1→ADC1)
- The ANAN-10E gateware re-routes ADCs in PS-MOX so that one ADC
  carries the **PA-feedback** sample and the other carries the
  **TX-DAC loopback**. Per mi0bot's `SetPSRxIdx(0, 0)` and
  `SetPSTxIdx(0, 1)` convention, **DDC0 (Stream 0) = PA feedback,
  DDC1 (Stream 1) = TX loopback**. Which physical ADC each maps to is
  gateware-internal for ANAN-10E and not visible from mi0bot source.

#### PSC.6 Critical UNRESOLVED for Zeus's V1 fix

**mi0bot does NOT use 2-DDC PS-MOX layout for HL2** — it uses the 4-DDC
layout (`cmaster.cs:611-635`, `console.cs:8189-8265`). The 2-DDC layout
is for ANAN-10E / ANAN-100B (`cmaster.cs:592-610`).

The auditor's V1 fix proposes routing Zeus to the 2-DDC paired layout
on HL2. This is **a Zeus-specific simplification**, not a mi0bot pattern
for HL2. Open question: does the HL2 gateware honour 2-DDC PS-MOX the
same way ANAN-10E hardware does?

- Does the HL2 gateware, when it sees `nddc=2 + puresignal_run=1 +
  XmitBit=1`, re-route ADC mapping to put PA-feedback on one DDC and
  TX-DAC loopback on the other?
- Or does it require the 4-DDC layout because the HL2 firmware's
  PS-mode ADC-routing is hard-coded for 4 DDCs?

**mi0bot source cannot answer this** — mi0bot never tries 2-DDC on HL2.
Either:
1. The HL2 gateware accepts both 2-DDC and 4-DDC PS-MOX modes (treating
   them as protocol-compatible) — in which case Zeus's V1 fix should
   work and `data[1]` will be the gateware's TX-DAC loopback as in the
   4-DDC case.
2. The HL2 gateware only honours the 4-DDC PS-MOX mode — in which case
   the 2-DDC layout's "DDC1" will carry whatever ADC1 sees (probably
   garbage or RX-band noise during TX), NOT a TX-DAC loopback, and
   pscc will compute nonsense corrections.

**Resolving this requires**:
- Either reading the HL2 gateware Verilog at
  `github.com/softerhardware/Hermes-Lite2`, looking for the PS-MOX
  ADC-routing logic and whether it conditions on `nddc`.
- Or bench-testing: run Zeus with 2-DDC layout, check whether
  `data[1]`'s magnitude is a sensible TX-reference (≈ amplitude of
  what Zeus is sending out via EP2) vs noise.

If 2-DDC turns out to NOT work on HL2, the fallback is to switch
Zeus to the 4-DDC layout (mi0bot's choice for HL2). The Zeus parser
work to handle 4-DDC may have been why commit `4ed2e63 "working on
the herpes"` was made in the first place — the auditor's note that
"4-DDC was tested only on G2 not HL2" suggests the 4-DDC parser
exists but was never validated against actual HL2 packets.

#### PSC.7 Counterexample: Zeus's `_psTxRingI/Q` is not the mi0bot pattern

Zeus's `Protocol1Client.cs:191-212` records every wire-written TX-IQ
sample into `_psTxRingI/Q`. The auditor's notes show this ring is
fed by `PsTapIqSource` (`Protocol1Client.cs:924-939`) at every TX
write to EP2.

Per PSC.2 / PSC.3 / PSC.4, **mi0bot does not use a host-side TX-IQ
ring for pscc tx_buf**. The TX reference comes from the radio
(gateware loopback DDC). Therefore:

- If Zeus's PS feedback path passes `_psTxRingI/Q` samples into
  `psccF()` as the `Itxbuff/Qtxbuff` arguments, **that is a deviation
  from mi0bot**. The ring contains the **predistorted TX I/Q the host
  computed** (i.e. the very samples WDSP IQC has already applied
  predistortion to), not the gateware's TX-DAC-loopback. PS in
  mi0bot computes `gain = TX / RX` where TX is the input reference
  the gateware actually clocked into the DAC. Feeding the
  predistorted output back as the "input reference" gives the engine
  an inconsistent picture and convergence behaviour is hard to predict.

- More charitably: if Zeus's `_psTxRingI/Q` ring is being used purely
  for *display* (panadapter PS-TX trace) or for diagnostic logging,
  it's harmless. But if it's being fed to `psccF`, that is the bug.

The auditor / fixer should grep Zeus for the actual `psccF` call site
(`WdspDspEngine.cs:2002` per the auditor's notes) and confirm what
`Itxbuff/Qtxbuff` source it uses. If it's `_psTxRingI/Q`, switch it to
the second DDC stream from the wire packet. If it's already the second
DDC stream, the ring is dead code (or display-only) and the bug is
elsewhere (e.g. the §"HL2 register 0x0a and 0x2b semantics on PS-arm"
PA-mute investigation).

**Concrete experiment to validate**:
1. Disconnect `_psTxRingI/Q` from the `psccF` call (or stub it with
   zeros).
2. If PS still arms and starts converging (`info[5]` cor.cnt
   increments) AND DDC2/DDC3 reads fill normally: the ring was unused
   or display-only.
3. If PS arms but never converges (cor.cnt stays at 0, info[15]
   cycles LCOLLECT → flush every 4 s): the ring WAS the TX reference
   and Zeus must source TX from the wire packet's second DDC instead.

#### PSC.8 Zeus implementation notes (read-only inference)

- `WdspDspEngine.FeedPsFeedbackBlock(txI, txQ, rxI, rxQ)` (auditor's
  notes, `:2002`) takes 4 separate buffers — TX I/Q and RX I/Q — and
  pushes via `psccF`. **It cannot be a transparent wrapper around
  mi0bot's `pscc()` semantics** because `psccF` wants float buffers
  for both tx AND rx, and mi0bot's `psccF` itself
  (`calcc.c:840-857`) is a thin float→double converter that calls
  `pscc()` immediately. So at the wire-data layer, Zeus correctly
  passes both halves to WDSP.

- The question is **where Zeus gets the `txI/txQ` half from**. Per
  PSC.7, mi0bot's answer is "the second DDC of the paired feedback
  stream". Zeus needs to make sure that's where it comes from too.

- The auditor's map at `Protocol1Client.cs:227-330` describes
  `HandlePs4DdcPacket` decoding all four DDCs. If that handler emits
  `PsFeedbackFrame(TxI=DDC3_I, TxQ=DDC3_Q, RxI=DDC2_I, RxQ=DDC2_Q,
  SeqHint)`, then at the parser level Zeus is correctly mimicking
  mi0bot's 4-DDC pattern. The bug would then be elsewhere (arming
  sequence, register writes, or PA-mute from §"HL2 register 0x0a and
  0x2b semantics on PS-arm" / Reg.6).

- Zeus's `_psTxRingI/Q` and `PsTapIqSource` may exist as a fallback
  for radios that don't provide a TX-DAC-loopback DDC — but HL2 with
  the 4-DDC layout DOES provide one (DDC3). On HL2 the ring is at
  best dead code.

### Canonical mi0bot HL2 PS-on MOX round table

> **Top-of-section findings the auditor MUST internalize before reading
> the table:**
>
> 1. **The HL2 PA enable bit lives at C2 bit 3 of round 10** (= the
>    `ApolloTuner` field, 0x08), NOT at C3 bit 7 of round 10. mi0bot
>    repurposes the Apollo Tuner bit on HL2 specifically for PA
>    enable — see `netInterface.c:582-591` (`DisablePA`) where on HL2
>    it calls `EnableApolloTuner(!bit)`. **This is the most likely
>    PA-mute bug**: if Zeus is sending C2 = 0x40 in round 10 instead
>    of 0x48, the HL2 firmware will mute the PA.
> 2. **`tx[0].pa` field semantics are INVERTED**: `tx[0].pa = 0` means
>    PA ENABLED, `tx[0].pa = 1` means PA DISABLED. `DisablePA(int bit)`
>    sets `tx[0].pa = bit` directly. Hence round 10 C3 bit 7 (which
>    sends `(tx[0].pa & 1) << 7`) is **0 in normal PA-enabled
>    operation, 1 when the PA is intentionally muted**.
> 3. **Round 11 C4 (HL2 6-bit step att with enable bit 0x40) MUST be
>    0x5F for txatt=0 dB**, NOT 0x00. mi0bot encoding is
>    `(31 - txatt_dB) | 0x40`. C4=0x00 in round 11 = enable bit clear
>    + att value 0 = "fall back to legacy 5-bit att in round 4 C3,
>    which mi0bot has at 0x1F = 31 dB max attenuation" → PA output
>    knocked down by ~31 dB.
> 4. **Bytes that change between PS-OFF and PS-ON in mi0bot, by ROUND**:
>    - Round 0 C4: `(nddc-1)<<3` — typically 0x04 in PS-OFF
>      (nddc=1, duplex bit only) → 0x1C in PS-ON 4-DDC (duplex +
>      `nddc-1=3`).
>    - Round 5/6 (DDC2/DDC3 freq): rounds always run; values change
>      as DDC2/DDC3 are tuned to TX freq, but they're sent regardless.
>    - Round 11 C2 bit 6: 0 → 1 (puresignal_run).
>    - Round 16 C2 bit 6: 0 → 1 (puresignal_run mirror).
>    - **Nothing else changes**. The PA enable, drive level, TX att,
>      filters, ANT — none of these change in mi0bot when PS toggles.
>    - **If Zeus's diff between PS-OFF and PS-ON shows ANY round
>      changing other than the 4 above, that is a Zeus-specific
>      behaviour with no mi0bot equivalent — and is the prime suspect.**

#### Bench state assumed for the table

| Param | Value |
|-------|-------|
| Radio | HermesLite 2 (Protocol 1, USB) |
| MOX | ON (`XmitBit = 1`) |
| PS-A | ON (`prn->puresignal_run = 1`) |
| nddc | 4 (HL2 4-DDC layout, mi0bot default — `console.cs:8189`) |
| TX VFO | 28.400 MHz USB single VFO |
| RX1 freq | 28.400 MHz (single VFO) |
| RX2 freq | 28.400 MHz (assumed shadow; runtime-dependent) |
| RX1 sample rate | 192 kHz (`SampleRateIn2Bits = 2`) |
| TX att | 0 dB → `prn->adc[0].tx_step_attn = 31 - 0 = 31` (HL2 inversion) |
| RX1 step att | 0 (default) |
| Drive | 100% TUN, `tx[0].drive_level = 0xFF` |
| PA | enabled → `tx[0].pa = 0`, `ApolloTuner = 0x08` |
| Diversity | OFF (`P1_en_diversity = 0`) |
| ANT | ANT1 (no ANT2/3 selectors) |
| Mode | SSB-USB (no CW gating, no EER) |
| Alex BPF/LPF board | NONE (HL2 default — all `bpf->_*` = 0) |
| Apollo board | NONE (`ApolloFilt=0, ApolloATU=0, ApolloFiltSelect=0`) |
| Apollo Tuner field | **0x08 on HL2 with PA enabled** (repurposed PA bit) |
| BPF2 board | NONE |
| Mic | default (mic_boost=0, line_in=0, mic_trs=0, mic_bias=0, mic_ptt=0) |
| line_in_gain | 0 |
| user_dig_out | 0 |
| Watchdog | OFF, reset_on_disconnect = 0 |
| 28.400 MHz hex | 28,400,000 = `0x01B15980` → C1=0x01, C2=0xB1, C3=0x59, C4=0x80 |

#### The 19 rounds — exact bytes mi0bot writes

Each row: `C0` = (round-specific high bits) `|` `XmitBit (=0x01 here)`.
For round 0 the C0 high bits are 0x00 so `C0 = 0x01`. For round 1,
`C0 = 0x02 | 0x01 = 0x03`. Etc.

| Round | C0_addr | C0    | C1    | C2    | C3    | C4    | mi0bot file:line | Notes |
|-------|---------|-------|-------|-------|-------|-------|------------------|-------|
| 0     | 0x00    | 0x01  | 0x02  | 0x00  | 0x00  | **0x1C** | networkproto1.c:934-956 | C1=`SampleRateIn2Bits`(192k)=2; C4=`ANT(0) \| 0x04 duplex \| (nddc-1)<<3 \| div<<7 = 0\|0x04\|0x18\|0 = 0x1C`. **In PS-OFF this is 0x04 (nddc=1).** |
| 1     | 0x02    | 0x03  | 0x01  | 0xB1  | 0x59  | 0x80  | networkproto1.c:960-966 | TX VFO bytes (TX freq 28.400 MHz big-endian) |
| 2     | 0x04    | 0x05  | 0x01  | 0xB1  | 0x59  | 0x80  | networkproto1.c:968-979 | DDC0 freq = RX1 freq (single-VFO=TX freq). HL2 nddc=4, so the `(nddc==2 && PS && TX)` re-tune branch is FALSE; DDC0 stays on RX1 freq. |
| 3     | 0x06    | 0x07  | 0x01  | 0xB1  | 0x59  | 0x80  | networkproto1.c:981-996 | DDC1 freq = `rx[1].frequency` = RX2 freq. Single-VFO assumed = TX freq. **Runtime-dependent**: if Zeus has RX2 unconfigured, this byte may be 0x00 or some other default — diff this carefully. |
| 4     | 0x0e    | 0x1D  | **0x04** | 0x00  | **0x1F** | 0x00  | networkproto1.c:1001-1007 | C1=`P1_adc_cntrl & 0xFF` = 4; C2=`(P1_adc_cntrl>>8) & 0x3F` = 0; C3=`adc[0].tx_step_attn & 0x1F` = 31&0x1F = **0x1F (legacy 5-bit "31 dB att")**. **NOTE: HL2 firmware ignores round 4 C3 when round 11 C4 enable bit is set; falls back to round 4 C3 if not.** |
| 5     | 0x08    | 0x09  | 0x01  | 0xB1  | 0x59  | 0x80  | networkproto1.c:1009-1020 | DDC2 freq = TX freq (HL2 nddc!=5 branch) |
| 6     | 0x0a    | 0x0B  | 0x01  | 0xB1  | 0x59  | 0x80  | networkproto1.c:1022-1030 | DDC3 freq = TX freq always |
| 7     | 0x0c    | 0x0D  | 0x01  | 0xB1  | 0x59  | 0x80  | networkproto1.c:1032-1040 | DDC4 freq = TX freq (Orion2 PS); HL2 sends but doesn't use DDC4 |
| 8     | 0x0e    | 0x0F  | 0x01  | 0xB1  | 0x59  | 0x80  | networkproto1.c:1042-1050 | DDC5 freq = RX1 freq ("DDC5 not used"). NB: same C0 high-bits as round 4 — distinguished by `out_control_idx` cycle position, NOT by C0. |
| 9     | 0x10    | 0x11  | 0x01  | 0xB1  | 0x59  | 0x80  | networkproto1.c:1052-1060 | DDC6 freq = RX1 freq ("DDC6 not used") |
| **10** | **0x12** | **0x13** | **0xFF** | **0x48** | **0x00** | **0x00** | networkproto1.c:1062-1075 | **THE PA ENABLE ROUND.** C1=`drive_level`=0xFF (TUN full); **C2 = `(mic_boost \| line_in<<1 \| ApolloFilt \| ApolloTuner \| ApolloATU \| ApolloFiltSelect \| 0x40) & 0x7F` = `(0 \| 0 \| 0 \| 0x08 \| 0 \| 0 \| 0x40) & 0x7F = 0x48`** ← `ApolloTuner=0x08` is HL2-specific PA enable, set by `DisablePA(0)` via `EnableApolloTuner(1)` (`netInterface.c:585`); C3 = `bpf bits \| (tx[0].pa & 1) << 7` = `0 \| 0` = 0x00 (because `tx[0].pa=0` = PA enabled, INVERTED semantics); C4 = LPF bits = 0 (no Alex). |
| **11** | **0x14** | **0x15** | **0x00** | **0x40** | **0x00** | **0x5F** | networkproto1.c:1077-1089 | **THE PS-RUN BIT + HL2 STEP ATT ROUND.** C1=preamp/mic bits=0; **C2 = `(line_in_gain & 0x1F) \| ((puresignal_run & 1) << 6) = 0 \| 0x40 = 0x40`** ← PS arm bit; C3 = `user_dig_out & 0x0F` = 0x00; **C4 (XmitBit branch) = `(adc[0].tx_step_attn & 0x3F) \| 0x40 = (31 & 0x3F) \| 0x40 = 0x1F \| 0x40 = 0x5F`** ← HL2 6-bit large-range TX att encoded for 0 dB, with enable bit 0x40 set. **C4=0x00 here means "att enable bit clear" → gateware falls back to round 4 C3 = 0x1F (31 dB legacy att) → PA output drops ~31 dB.** |
| 12    | 0x16    | 0x17  | 0x3F  | 0x20  | 0x59  | 0x32  | networkproto1.c:1091-1109 | C1 = `0x1F \| 0x20 = 0x3F` (during XmitBit, hardcoded 0x1F ADC2 att | enable bit 0x20); C2 = `(adc[2].rx_step_attn & 0x1F) \| 0x20 \| (rev_paddle<<6) = 0 \| 0x20 \| 0 = 0x20` (HL2 has no adc[2]); C3 = `(keyer_speed=25 & 0x3F) \| CWMode_iambic_modeA(0x40) = 0x19 \| 0x40 = 0x59` — these are CW values, sent in SSB but ignored by gateware; C4 = `keyer_weight=50 & 0x7F = 0x32`. |
| 13    | 0x1e    | 0x1F  | 0x00  | 0x19  | 0x0A  | 0x00  | networkproto1.c:1113-1119 | CW: C1=cw_enable=0; C2=sidetone_level (typical 25); C3=rf_delay (typical 10); C4=0. SSB-irrelevant but sent. **Specific values are runtime-dependent**; auditor should not flag minor mismatches in C2/C3 as bugs unless cw_enable bit changes. |
| 14    | 0x20    | 0x21  | 0x3E  | 0x02  | 0x25  | 0x08  | networkproto1.c:1121-1127 | CW: hang_delay=250 (default) → `>>2=62=0x3E, &3=2=0x02`; sidetone_freq=600 → `>>4=37=0x25, &0xF=8`. SSB-irrelevant. |
| 15    | 0x22    | 0x23  | 0x00  | 0x00  | 0x00  | 0x00  | networkproto1.c:1129-1135 | EER PWM (epwm_min/max=0 default, EER off) |
| **16** | **0x24** | **0x25** | **0x00** | **0x40** | **0x00** | **0x00** | networkproto1.c:1137-1146 | **PS-RUN BIT MIRROR (BPF2 reg).** C1=BPF2 bits=0 (no BPF2 board); **C2 = `(xvtr_enable & 1) \| ((puresignal_run & 1) << 6) = 0 \| 0x40 = 0x40`**; C3=0; C4=0. Mirror of round 11's PS bit; HL2 gateware honours either. |
| 17    | 0x2e    | 0x2F  | 0x00  | 0x00  | 0x04  | 0x14  | networkproto1.c:1148-1154 | TX latency / PTT hang. C1=0; C2=0; C3=`ptt_hang & 0x1F` (typical 4); C4=`tx_latency & 0x7F` (typical 20=0x14). Runtime-dependent; auditor should not flag minor mismatches. |
| 18    | 0x3a    | 0x75  | 0x00  | 0x00  | 0x00  | 0x00  | networkproto1.c:1156-1162 | Reset on disconnect, default off. C4=`reset_on_disconnect`=0. |

#### Quick-read summary: the 4 PS-significant bytes

```
Round 0 C4  : 0x1C  (= duplex 0x04 | nddc-1=3 << 3 = 0x18)
Round 11 C2 : 0x40  (= line_in_gain 0 | puresignal_run << 6)
Round 11 C4 : 0x5F  (= tx_step_attn(31, for 0 dB) & 0x3F | 0x40 enable)
Round 16 C2 : 0x40  (= xvtr_enable 0 | puresignal_run << 6)
```

And the 1 PA-significant byte that is constant across PS-OFF/PS-ON
but easy to miss:

```
Round 10 C2 : 0x48  (= ApolloTuner 0x08 [HL2 PA enable] | unconditional 0x40)
```

#### The diagnostic diff the auditor / fixer should run

For each of the 19 rounds, capture Zeus's actual outbound C0..C4 bytes
in BOTH states:
- **PS-OFF MOX** (TUN, no PS arm) — should match the table EXCEPT:
  - Round 0 C4 = 0x04 (nddc=1, not 0x1C).
  - Round 11 C2 = 0x00 (puresignal_run=0, not 0x40).
  - Round 16 C2 = 0x00 (puresignal_run=0, not 0x40).
- **PS-ON MOX** — should match the table exactly.

The first byte that differs from this table is the bug. Top
suspects ranked by failure mode:

1. **Round 10 C2 ≠ 0x48** (PA-mute primary suspect). If Zeus has not
   wired up the HL2-specific repurpose of `ApolloTuner` for PA enable,
   it will send 0x40 instead of 0x48. **Symptom**: PA muted on every
   transmit, not just PS-MOX. But team-lead says PS-OFF TUN works at
   2.695 W — so this byte is probably correct in PS-OFF. Verify it's
   ALSO correct (0x48) in PS-ON.
2. **Round 11 C4 ≠ 0x5F** (PA-attenuation primary suspect for the
   PS-MOX-specific symptom). If Zeus's PS-arm encoder writes only the
   PS bit (C2 bit 6) and zeros the rest of round 11, C4 lands at
   0x00. With C4 enable bit (0x40) clear, the HL2 gateware falls back
   to round 4 C3's legacy 5-bit att = 0x1F = 31 dB → PA output drops
   ~31 dB → a 2.695 W → 5 µW symptom. Math: 2.695 W * 10^(-31/10) =
   2.13 mW = 2130 µW. Still 400× higher than 5 µW, so 31 dB alone
   doesn't explain everything — but combined with the HL2 firmware
   reading "att=0" with no enable bit (interpretation undefined per
   gateware version) could push further down.
3. **Round 11 C1 (preamp/mic) clobbered**: if Zeus zeros the C1 byte
   when emitting the PS-arm round (instead of preserving the
   preamp/mic-bias state), the gateware loses that state. Less
   likely to mute PA but could affect mic gain → TX I/Q peak →
   apparent power.
4. **Round 11 C3 (user_dig_out) clobbered**: HL2 user_dig_out drives
   external GPIO; some HL2 boards use it for T/R relay control. If
   Zeus zeros C3 and the user_dig_out is normally non-zero, T/R
   relay could de-energize → antenna disconnected → power drops to
   leakage levels (~µW range, consistent with 5 µW reading). **THIS
   IS A STRONG SUSPECT FOR THE 5 µW READING.** Check
   `prn->user_dig_out` value in the operator's setup.
5. **Round 0 C4 ≠ 0x1C** (the nddc-1 byte). If Zeus is sending nddc=1
   in PS-MOX (C4=0x04) the firmware streams a single DDC and DDC2/DDC3
   never come back — but this affects feedback, not PA output. Not the
   PA-mute bug.
6. **Round 4 C3 ≠ 0x1F**. If Zeus is sending 0 here, it's in the
   "wrong direction": more attenuation = bigger value, so smaller value
   = less attenuation. C3=0 means "no att" in the legacy 5-bit field,
   which would NOT explain the PA-mute. Not a suspect.

#### What "PS-OFF TUN 2.695 W" tells us by elimination

- All 19 rounds in PS-OFF MOX produce ~3 W output. So:
  - Round 0 C4 = 0x04 (nddc=1) is fine.
  - Round 4 C3 = 0x1F is fine in PS-OFF (gateware uses round 11's
    enable-bit path).
  - Round 10 C2 = 0x48 (ApolloTuner=PA enable) is fine.
  - Round 10 C3 = 0x00 (PA-not-disabled) is fine.
  - Round 11 C4 = 0x5F (HL2 6-bit att with 0x40 enable, value 0 dB)
    is fine.
  - Round 11 C2 = 0x00 (PS bit clear) is fine.

- When PS toggles ON, mi0bot changes ONLY:
  - Round 0 C4: 0x04 → 0x1C (nddc 1→4, bits[5:3] 0→3).
  - Round 11 C2: 0x00 → 0x40 (PS bit).
  - Round 16 C2: 0x00 → 0x40 (PS bit mirror).
  - DDC2 / DDC3 frequency rounds become "active" from the firmware's
    perspective, but the bytes were already being sent.

- **If Zeus's PS-on encoder also touches any OTHER byte** — drives
  Round 11 C1/C3/C4 to zero, drives Round 10 C2 to 0x40 (clearing
  ApolloTuner), drives Round 4 C3 to 0, etc. — that is the bug.

#### `0x2b` register: not in this table

mi0bot does **not** write register 0x2b in any round (per §"HL2
register 0x0a and 0x2b semantics on PS-arm" / Reg.3). C0=0x56 is
absent from the round-robin. If Zeus is writing 0x2b at all (per
commit `1e53807`), that write is **outside** the 19-round mi0bot
cycle and will appear as an "extra" frame in the dump. Suspect that
extra frame heavily.

### iqc init parameters and call order (mi0bot)

> **Headline finding for the "intermittent PA mute" hypothesis:**
> mi0bot's iqc (the WDSP predistorter) starts with **`run = 0`**
> (`TXA.c:425`, `create_iqc(0, ...)`) — identity passthrough. xiqc()
> only flips to active predistortion when **`SetTXAiqcStart` is called
> from inside `doPSCalcCorrection` AFTER `calc()` succeeds with
> `scOK = 1`** (`calcc.c:485-507`). There is no host-side path that
> seeds or resets the iqc. mi0bot relies on the engine's internal
> sanity check to never push zero/bad coefficients to iqc.
>
> **There is no "zero-output mode" in xiqc()** (iqc.c:122-203). The
> 5 enum states (`RUN, BEGIN, SWAP, END, DONE`) all output non-zero
> IF coefficients are non-zero. **BUT**: iqc operates IN-PLACE
> (`txa[ch].midbuff` for both in and out, `TXA.c:427-428`). If
> coefficients ARE zero (the malloc0 default after `create_iqc`,
> `iqc.c:89`) and `iqc->run` gets flipped to 1 with state=BEGIN, the
> xiqc math fades the buffer to zero over `ntup` samples and locks
> there. **That's the "PA muted" mechanism, and it's ENTIRELY
> Zeus-side if it's happening — mi0bot's calc-gated path never sets
> `run=1` with zero coefficients.**

#### iqc.1 The values mi0bot ships at boot (TXA.c create_calcc + create_iqc)

`OpenHPSDR-Thetis/Project Files/Source/wdsp/TXA.c:405-432`:

```c
txa[channel].calcc.p = create_calcc (
    channel,                              // channel number
    1,                                    // run cal flag
    1024,                                 // input buffer size
    ch[channel].in_rate,                  // sample rate (typically 48000 for TX-side)
    16,                                   // ints
    256,                                  // spi
    (1.0 / 0.4072),                       // hw_scale (overwritten by SetPSHWPeak per radio)
    0.1,                                  // mox delay (sec)              ← TXA.c default
    0.0,                                  // loop delay (sec)             ← TXA.c default
    0.8,                                  // ptol                         ← TXA.c default
    0,                                    // mox
    0,                                    // solidmox
    1,                                    // pin mode                     ← TXA.c default
    1,                                    // map mode                     ← TXA.c default
    0,                                    // stbl mode                    ← TXA.c default (zero!)
    256,                                  // pin samples
    0.9);                                 // alpha (stabilise blend)

txa[channel].iqc.p0 = txa[channel].iqc.p1 = create_iqc (
    0,                                    // run                          ← TXA.c default (zero!)
    ch[channel].dsp_size,                 // size
    txa[channel].midbuff,                 // input buffer ── IN-PLACE
    txa[channel].midbuff,                 // output buffer ── IN-PLACE
    (double)ch[channel].dsp_rate,         // sample rate
    16,                                   // ints
    0.005,                                // changeover time (`tup`)
    256);                                 // spi
```

**`create_iqc` runs `malloc0`** (`iqc.c:89`) so all coefficient arrays
(`a->cm`, `a->cc`, `a->cs`) are **zeroed at creation**. iqc cannot
produce non-zero predistorted output until `SetTXAiqcStart` loads real
coefficients.

#### iqc.2 The iqc lifecycle — when `iqc->run` flips

`iqc->run` is touched in **exactly two places** in mi0bot:

1. **Set to 1**: `iqc.c:284` — `InterlockedBitTestAndSet(&txa[ch].iqc.p1->run, 0)`
   inside `SetTXAiqcStart(channel, cm, cc, cs)` (`iqc.c:271-286`).
   - Loads new coefficients into `a->cm[a->cset], a->cc[a->cset],
     a->cs[a->cset]`.
   - Sets `a->state = BEGIN`.
   - Sets `iqc->run = 1`.
   - Spins on `a->busy` until BEGIN→RUN transition completes.

2. **Set to 0**: `iqc.c:298` — `InterlockedBitTestAndReset(&txa[ch].iqc.p1->run, 0)`
   inside `SetTXAiqcEnd(channel)` (`iqc.c:288-299`).
   - Sets `a->state = END`.
   - Spins on `a->busy` until END→DONE transition completes.
   - Sets `iqc->run = 0`.

3. **Plus a third path** that resets `iqc->run = 0` directly
   (`calcc.c:1120` inside `ForceShutDown`, called only from
   `SetPSIntsAndSpi` when ints/spi changes — not part of normal PS
   arm/disarm).

**Who calls `SetTXAiqcStart`?** Only `doPSCalcCorrection`
(`calcc.c:498`):

```c
void __cdecl doPSCalcCorrection (void *arg) {
    CALCC a = (CALCC)arg;
    while (!InterlockedAnd(&a->calccorr_bypass, 0xffffffff)) {
        WaitForSingleObject(a->Sem_CalcCorr, INFINITE);
        if (!InterlockedAnd(&a->calccorr_bypass, 0xffffffff)) {
            calc(a);
            if (a->scOK)                                                  // <-- gate
            {
                EnterCriticalSection (&a->ctrl.cs_SafeToEnd);
                if (!InterlockedBitTestAndSet(&a->ctrl.running, 0))
                    SetTXAiqcStart(a->channel, a->cm, a->cc, a->cs);     // first lock
                else
                    SetTXAiqcSwap (a->channel, a->cm, a->cc, a->cs);     // subsequent
                LeaveCriticalSection(&a->ctrl.cs_SafeToEnd);
            }
            InterlockedBitTestAndSet(&a->ctrl.calcdone, 0);
        }
    }
}
```

The `if (a->scOK)` gate is the **only** thing protecting iqc from
loading bad coefficients. `calc()` (`calcc.c:270-470`) sets `scOK`
based on the spline sanity check (`scheck` / `rxscheck` results). If
calc has not yet run successfully (i.e. PS just armed, no LCALC has
happened yet, or all calc attempts failed), **`SetTXAiqcStart` is
never called and `iqc->run` stays at 0** → identity passthrough →
PA output normal.

**Who calls `SetTXAiqcEnd`?** Only `doPSTurnoff` (`calcc.c:509-523`),
signalled by `Sem_TurnOff`. The `Sem_TurnOff` is released from inside
the engine state machine when transitioning to a no-corrections-active
state. Not from any host call.

#### iqc.3 The five xiqc() states — none of them produce zero except via zero coefficients

`iqc.c:122-203` (`xiqc(IQC a)`):

```c
if (_InterlockedAnd(&a->run, 1))     // run == 0 → identity memcpy at line 201
{
    ... // compute ym, yc, ys from coefficients via cubic spline
    PRE0 = ym * (I * yc - Q * ys);
    PRE1 = ym * (I * ys + Q * yc);
    switch (a->state) {
    case RUN:    /* output PRE0/PRE1 directly */               break;
    case BEGIN:  /* (1-cup)*I + cup*PRE0 — fade I → PRE0 */    break;
    case SWAP:   /* fade PRE_oldset → PRE_newset */            break;
    case END:    /* (1-cup)*PRE0 + cup*I — fade PRE0 → I */    break;
    case DONE:   /* PRE0 = I; PRE1 = Q (passthrough) */        break;
    }
    a->out[2*i+0] = PRE0;
    a->out[2*i+1] = PRE1;
}
else if (a->out != a->in)
    memcpy (a->out, a->in, a->size * sizeof (complex));
```

Failure mode analysis:

| condition | xiqc output | host-visible PA effect |
|-----------|-------------|------------------------|
| `run == 0` (default after create_iqc) | identity (memcpy or no-op since in==out) | Normal — host TX I/Q reaches PA |
| `run == 1`, state == DONE | `PRE = I, Q` (identity) | Normal |
| `run == 1`, state == RUN, coefs non-zero (post successful calc) | predistorted I/Q | Normal — PS active and converging |
| `run == 1`, state == RUN, **coefs zero** | `PRE = 0` | **PA muted to zero** |
| `run == 1`, state == BEGIN, coefs zero | crossfade I → 0 over `ntup` samples | **PA fades to zero, then stays zero** |
| `run == 1`, state == END, coefs zero | crossfade 0 → I, ends at DONE | Recovers to normal after END completes |

**The two failure rows above are reachable ONLY if a host calls
`SetTXAiqcStart(... cm, cc, cs)` with all-zero arrays.** mi0bot's
`doPSCalcCorrection` never does this because of the `if (a->scOK)`
gate; only successfully-computed coefficients get pushed.

#### iqc.4 Per-parameter values mi0bot sets — when, where, and re-issue policy

| Parameter | Boot value (TXA.c) | UI default (PSForm) | When re-issued | mi0bot file:line |
|-----------|-------------------|---------------------|----------------|------------------|
| `pin` (SetPSPinMode) | **1** | 1 (`chkPSPin.Checked=true`) | Only on user UI toggle | TXA.c:418, designer.cs:210, PSForm.cs:861-867 |
| `map` (SetPSMapMode) | **1** | 1 (`chkPSMap.Checked=true`) | Only on user UI toggle | TXA.c:419, designer.cs:193, PSForm.cs:869-875 |
| `stbl` (SetPSStabilize) | **0** | 0 (`chkPSStbl` no-Checked) | Only on user UI toggle | TXA.c:420, designer.cs:175-188, PSForm.cs:877-883 |
| `ptol` (SetPSPtol) | **0.8** | 0.8 (`chkPSRelaxPtol` unchecked) | Only on user UI toggle | TXA.c:415, PSForm.cs:833-839 |
| `ints` (SetPSIntsAndSpi) | **16** | 16 (combo "0.5") | Only on user combo change | TXA.c:410, PSForm.cs:885-913 |
| `spi` | **256** | 256 (combo "0.5") | Only on user combo change | TXA.c:411, PSForm.cs:885-913 |
| `mox_delay` (SetPSMoxDelay) | **0.1 sec** | 0.2 sec (designer) | Only on user UI change | TXA.c:413, designer.cs:368, PSForm.cs:493-496 |
| `loop_delay` (SetPSLoopDelay) | **0.0 sec** | 0.0 sec | Only on user UI change | TXA.c:414, designer.cs:801, PSForm.cs:498-501 |
| `tx_delay` (SetPSTXDelay) | **0** sec (TXA.c does not seed) | 150 ns (designer) | Only on user UI change | designer.cs:409, PSForm.cs:503-506 |
| `feedback_rate` (SetPSFeedbackRate) | **192000** | 192000 | Boot only (`cmaster.cs:457`); RedPitaya re-issues on `cmaster.PSrate` setter (`cmaster.cs:404-411`) | cmaster.cs:457 |
| `hw_scale` (SetPSHWPeak) | 1/0.4072 | 0.233 for HL2 (set after model identification) | On model identify; on user "Default" button; on textbox change | TXA.c:412, cmaster.cs:458, clsHardwareSpecific.cs:311-312 |
| `iqc->run` | **0** (identity) | n/a (not user-controllable) | Only via `SetTXAiqcStart`/`SetTXAiqcEnd` from `doPSCalcCorrection`/`doPSTurnoff` | iqc.c:90, calcc.c:498, iqc.c:284 |

**Notes:**
- `mox_delay`: TXA.c boot value is **0.1 sec**; PSForm designer default
  is **0.2 sec**. The discrepancy is harmless because PSForm overrides
  on first user interaction or restored prefs. Both values are above
  the HL2 firmware's MOX-rampup time.
- `tx_delay`: TXA.c does NOT call `SetPSTXDelay` at boot; iqc's `txdel`
  is malloc0-zeroed. PSForm sets 150 ns once on user interaction or
  restored prefs.
- **None of these are re-issued on PS arm.** They're set once at boot
  (TXA.c) or on user interaction (PSForm), and persist across all
  subsequent PS-on/off cycles.

#### iqc.5 The PS-arm sequence — what mi0bot actually invokes per arm

When PS-A toggles ON in mi0bot, the sequence is:

1. `console.PureSignalEnabled = true` → `psform.AutoCalEnabled = true`
   (PSForm.cs:271-289):
   - `_autoON = true`
   - `console.PSState = true`
   - **No iqc parameters touched.**

2. PSLoop's `timer1code()` next 10 ms tick (PSForm.cs:629-726):
   - State `OFF` reads `_autoON = true` → transitions to
     `TurnOnAutoCalibrate`.
   - In `TurnOnAutoCalibrate` (PSForm.cs:644-649):
     - `puresignal.SetPSControl(_txachannel, 1, 0, 1, 0)` —
       `(reset=1, mancal=0, automode=1, turnon=0)`. This sets
       `a->ctrl.reset = 1, a->ctrl.automode = 1`, etc.
       (`calcc.c:958-968`).
     - `if (!PSEnabled) PSEnabled = true;`
   - `PSEnabled = true` setter (PSForm.cs:240-269):
     1. `console.UpdateDDCs(rx2_enabled)` — re-program DDCs.
     2. `NetworkIO.SetPureSignal(1)` — host driver flag for round-11
        C2 bit 6.
     3. `NetworkIO.SendHighPriority(1)` — flush HP packet (P2 only;
        no-op on P1).
     4. `console.UpdateAAudioMixerStates()`.
     5. `cmaster.LoadRouterControlBit((void*)0, 0, 0, 1)` — flip the
        cmaster router PS-on bit.
     6. **`console.radio.GetDSPTX(0).PSRunCal = true`** — propagates
        to `puresignal.SetPSRunCal(channel, true)` (radio.cs:4067)
        which sets `a->runcal = 1` (calcc.c:891-898). **This opens
        the pscc() gate; samples can now flow into calc.**
   - **No iqc parameters touched. iqc->run stays at 0.**

3. The PS engine state machine cycles `LRESET → LWAIT → LMOXDELAY →
   LSETUP → LCOLLECT` in calcc.c when MOX is asserted, then →
   `MOXCHECK → LCALC`.

4. In `LCALC` (`calcc.c:773-801`), `Sem_CalcCorr` is signalled. The
   `doPSCalcCorrection` thread wakes, runs `calc()`, and IF
   `scOK = 1`:
   - First successful calc → `SetTXAiqcStart(ch, cm, cc, cs)` →
     **iqc->run = 1**, state = BEGIN with the new coefficients.
   - Subsequent successful calcs → `SetTXAiqcSwap(...)` swaps
     coefficient sets without re-flipping run.

5. PA output is identity (run=0) until step 4 first triggers; then it
   crossfades smoothly to predistortion.

**At no point does mi0bot re-issue `SetPSPinMode/MapMode/Stabilize/
Ptol/MoxDelay/LoopDelay/TXDelay/IntsAndSpi` during PS arm.** They are
boot-time or user-set constants.

#### iqc.6 What the auditor's "intermittent PA mute" likely means in Zeus

The auditor's hypothesis, rephrased:
> WDSP iqc initializing in zero-output mode on `SetPSRunCal(1) +
> SetPSControl(reset=1, automode=1)`, only sometimes recovering to
> identity.

Reframed against mi0bot's iqc semantics:
- `SetPSRunCal(1)` sets `a->runcal = 1` only — it does NOT touch
  iqc->run. xiqc still passes through identity.
- `SetPSControl(1, 0, 1, 0)` sets the engine control flags only. It
  does NOT touch iqc.
- **Neither call mutes the PA in mi0bot.** The PA mute happens ONLY
  if `iqc->run = 1` AND coefficients are zero/garbage.

For Zeus to exhibit "intermittent PA mute on PS arm" via the iqc
mechanism, **Zeus must be calling something equivalent to
`SetTXAiqcStart` outside the calc-gated path** — possibly a host-side
"prime the iqc with identity" or "reset the iqc" call that ships zero
or stale coefficients into iqc.cm/cc/cs and then flips iqc->run = 1.

**Things to check on the Zeus side**:

1. Does Zeus's `WdspDspEngine.SetPsEnabled(true)` or any subsequent
   call invoke `SetTXAiqcStart` directly (bypassing the calc-gated
   path)?
2. Does Zeus call `SetPSReset(channel, 1)` or `SetPSControl(reset=1,
   ...)` somewhere that ALSO resets iqc state? (mi0bot's
   `SetPSControl(reset=1)` sets `a->ctrl.reset = 1` only — this drives
   the engine state machine to LRESET, which does NOT touch
   `iqc->run`.)
3. Is Zeus's iqc `cm/cc/cs` allocation actually zeroed, or could it
   contain stale data from a previous session that becomes garbage
   when `iqc->run` gets flipped?
4. Is there a "prime the corrections to identity" routine in Zeus that
   pushes `cm = [1, 0, 0, 0, 1, 0, 0, 0, ...]` (identity-ish poly)
   and flips run=1? mi0bot has no such routine — it relies on iqc's
   own run=0 default for identity passthrough.

#### iqc.7 The patch the team-lead asked for

Per the team-lead's request: a precise patch for the fixer.

**Based on this spec analysis, the auditor's specific patch hypothesis
("change SetPSStabilize(0) → SetPSStabilize(1), re-issue PinMode/MapMode
on every PS arm") is NOT supported by mi0bot:**

- `SetPSStabilize(1)` would diverge from mi0bot's default `stbl = 0`
  (TXA.c:420). mi0bot recommends `stbl = 1` only for noisy PAs (per
  ps.md §3.3 and the chkPSStbl tooltip "Averages multiple collections
  of calibration samples"). Setting `stbl = 1` does not affect iqc
  zero-output behaviour; it averages successive calibrations and slows
  tracking.
- Re-issuing PinMode/MapMode on every PS arm is harmless but
  pointless: mi0bot does not do it, and the values persist correctly
  across PS arm/disarm cycles.

**The actual fix the spec suggests:** make sure Zeus's PS code path
does NOT push coefficients into iqc (or anything WDSP-equivalent)
outside the calc-completion path. If Zeus has a pscc / IQC reset
that sets `iqc->run = 1` with zero coefficients (whether on PS arm,
on MOX edge, or on `SetPSRunCal`), **remove that path**. mi0bot
relies entirely on:

```
iqc starts at run=0 (identity) → engine arms via SetPSRunCal(1) →
samples flow → LCOLLECT → LCALC → calc() succeeds → scOK=1 →
SetTXAiqcStart(real coefficients) flips iqc->run=1
```

If Zeus's flow short-circuits this (e.g. flips run=1 on PS arm
without coefficients), the PA mutes intermittently depending on
whether real coefficients have arrived yet.

#### iqc.8 UNRESOLVED for this hypothesis

- **Does Zeus actually call `SetTXAiqcStart` outside the calc path?**
  Cannot tell from this task's read-only scope. The auditor / fixer
  must grep Zeus for `SetTXAiqcStart` callers and verify it is invoked
  ONLY from a calc-completion handler analogous to mi0bot's
  `doPSCalcCorrection`.
- **Does Zeus have a Zeus-specific "iqc identity push"?** Not visible
  in this task's scope. If yes, removing it is the fix. If no, the
  intermittent PA mute is from another mechanism entirely (possibly
  the wire-byte issues in §"Canonical mi0bot HL2 PS-on MOX round
  table" or the 0x2b register write in §"HL2 register 0x0a and 0x2b
  semantics on PS-arm").
- **What `a->scOK` checks specifically.** Looking at `calc()`
  (`calcc.c:270-470`) shows scOK is set by the spline-builder
  sanity-check `scheck` (info[6]) and `rxscheck` (info[7]). If Zeus
  has its own pre-flight check that's more lenient than mi0bot's,
  zero/bad coefficients could slip through. Auditor should verify
  Zeus's calc-result gating matches mi0bot's `if (a->scOK)`.

## Zeus implementation map

Owner: `zeus-auditor` (pre-survey complete 2026-05-03; cross-reference pending
`mi0bot-spec`). Paths relative to the worktree root.

### Engine seam (Zeus.Dsp)

- **`Zeus.Dsp/IDspEngine.cs`** — public PS surface. Methods relevant to HL2:
  - `SetPsEnabled(bool)` — master arm. Comments claim "true → SetPSRunCal(1) +
    SetPSControl mode-on; false → 7× zero-pscc + SetPSRunCal(0) + SetPSControl
    reset" (pihpsdr-shaped shutdown).
  - `SetPsControl(bool autoCal, bool singleCal)` — cal-mode select; "single
    takes precedence when both true."
  - `SetPsAdvanced(ptol, moxDelay, loopDelay, ampDelay, hwPeak, ints, spi)` —
    batch setter; `SetPSIntsAndSpi` only fires when ints/spi actually change.
  - `SetPsHwPeak(double)` — per-radio default; called from
    `RadioService.ApplyPsHwPeakForConnection` at connect time.
  - `FeedPsFeedbackBlock(txI, txQ, rxI, rxQ)` — push 1024-sample paired blocks
    into WDSP `psccF`. Same shape for P1 and P2.
  - `GetPsStageMeters()` → `PsStageMeters` (FeedbackLevel info[4],
    CalState info[15], Correcting info[14], CorrectionDb derived,
    MaxTxEnvelope GetPSMaxTX, CalibrationAttempts info[5]).
  - `ResetPs()` — two-phase `SetPSControl(1,0,0,0)` then
    `SetPSControl(0, mancal, automode, 0)` to restore saved auto/single mode.
- **`Zeus.Dsp/PsStageMeters.cs`** — record struct used by both meters frame
  and AutoAttenuate gate logic.

### WDSP engine (Zeus.Dsp/Wdsp)

- **`Zeus.Dsp/Wdsp/NativeMethods.cs:769-844`** — P/Invoke surface. PS entries:
  `SetPSControl(channel, reset, mancal, automode, turnon)`,
  `SetPSRunCal(channel, run)`,
  `SetPSMox(channel, mox)`,
  `SetPSFeedbackRate(channel, rate)`,
  `SetPSHWPeak(channel, peak)`,
  `SetPSPtol(channel, ptol)`,
  `SetPSPinMode(channel, pin)`, `SetPSMapMode(channel, map)`,
  `SetPSStabilize(channel, stbl)`,
  `SetPSIntsAndSpi(channel, ints, spi)`,
  `SetPSMoxDelay`, `SetPSLoopDelay`, `SetPSTXDelay`,
  `GetPSInfo(channel, intPtr)`, `GetPSMaxTX(channel, out double)`,
  `psccF(channel, size, txI, txQ, rxI, rxQ, mox, solidmox)`.
- **`Zeus.Dsp/Wdsp/WdspDspEngine.cs`** — concrete implementation. Key spans:
  - Field defaults `:249-283`: `_psHwPeak=0.4072` (P1), `_psInts=16`,
    `_psSpi=256`, `_psMoxDelaySec=0.2`, `_psLoopDelaySec=0.0`,
    `_psAmpDelayNs=150.0`, `_psPtol=false` (= strict 0.8).
  - PS seed inside `OpenTxChannel` `:1235-1270`: SetPSFeedbackRate(192_000),
    SetPSPtol, SetPSPinMode(1), SetPSMapMode(1), SetPSStabilize(0),
    SetPSIntsAndSpi, SetPSMoxDelay, SetPSLoopDelay, SetPSTXDelay,
    SetPSHWPeak (with field default — RadioService overrides post-connect),
    `SetPSControl(id, 1, 0, 0, 0)` (RESET). `SetPSRunCal` stays 0 until arm.
  - `SetMox` `:1347-1422`: `SetPSMox(txaId, 1)` after RXA→0/TXA→1 on key-on;
    `SetPSMox(txaId, 0)` BEFORE TXA damping on key-off.
  - `SetPsHwPeak` `:1756-1771`: pushes unconditionally inside (0,2] range.
  - `SetPsControl` `:1773-1797`: maps (autoCal, singleCal) →
    `(reset, mancal, automode, turnon)`. `singleCal` precedence over auto;
    both-off → `reset=1`.
  - `SetPsAdvanced` `:1799-1855`: change-detect on every field. **HW peak
    push is intentionally NOT change-gated** — comment cites mi0bot
    PSpeak_TextChanged behaviour. `SetPSIntsAndSpi` is heavy-restart and IS
    change-gated.
  - `SetPsEnabled` `:1857-1913`:
    - On: `SetPSRunCal(id, 1)` then `SetPSControl(id, 1, mancal, automode, 0)`
      (reset=1 for clean LRESET transit). Opens PS-feedback display analyzer.
    - Off: tear down PS-FB analyzer, push 7× `psccF` zero-blocks, then
      `SetPSRunCal(id, 0)` and `SetPSControl(id, 1, 0, 0, 0)`. (pihpsdr
      transmitter.c:2422-2444 shutdown ordering.)
  - `FeedPsFeedbackBlock` `:2002-...`: writes psccF; mox/solidmox always 0
    because `SetPSMox` is the canonical MOX driver per calcc.c:846 comment.
  - `GetPsStageMeters` `:2081-2162`: short-circuits to `Silent` when
    `_psEnabled==false`. Reads info[4], info[5], info[6], info[13], info[14],
    info[15] under `_psLock`. Edge-triggered state-transition log.
  - `ResetPs` `:2164-2187`: two-phase reset+restore.

### Server pipeline (Zeus.Server.Hosting)

- **`Zeus.Server.Hosting/RadioService.cs`** — state owner. PS-relevant:
  - `:164-233` ctor reads persisted PS settings (`_psStore.Get()`),
    seeds `StateDto` with `PsEnabled=false` (NOT persisted, post-revert
    91105ad), `PsAuto=true`, `PsAutoAttenuate=true`, etc.
  - `:245-263` `PersistPsState()` — single-source-of-truth Upsert called
    from every PS mutate path. After 91105ad, `PsEnabled` is excluded.
  - `:1062-1158` REST mutators: `SetPs`, `SetPsAdvanced`,
    `SetPsFeedbackSource`, `SetPsMonitor`, `SetTwoTone`. Each calls
    `Mutate(...)` then `PersistPsState()`.
  - `:1162-1170` `UpdatePsLiveReadout` — TxMetersService writes back
    feedback / cal-state / correcting at 10 Hz.
  - `:1172-1220` `ResolvePsHwPeak(isProtocol2, board)` and
    `ApplyPsHwPeakForConnection` — per-board defaults. `(false, HermesLite2)
    → 0.233`. Pushes via `Mutate` and updates `PsHwPeakDefault` for the UI's
    "differs from default" hint.

- **`Zeus.Server.Hosting/DspPipelineService.cs`** — applies StateDto to engine.
  - `:97-152` PS feedback pump fields (P1 + P2), `_appliedPs*` latches
    (HwPeak default `0.4072` post d71ae40 revert), `_psResyncRequired` flag.
  - `:328-342` `ConnectAsync` (P1) starts `StartPsFeedbackPumpP1(client)`,
    sets `_psResyncRequired=true`, calls
    `_radio.ApplyPsHwPeakForConnection(isProtocol2:false, ConnectedBoardKind)`.
  - `:443-526` PureSignal apply ladder inside `OnRadioStateChanged`:
    1. HwPeak — change-gated (post d71ae40 revert; was unconditional briefly).
    2. Advanced batch (ptol, mox/loop/amp delay, ints/spi preset).
    3. `engine.SetPsControl(s.PsAuto, s.PsSingle)`.
    4. Master arm: on arm, `_p2Client?.SetPsFeedbackEnabled(true)` →
       `p1Active?.SetPsEnabled(true)` → 100 ms `Task.Delay` → `engine.SetPsEnabled(true)`.
       On disarm, engine first → wire off → `DrainPsFeedback`.
    5. Feedback source bit (Internal/External) — P2-only effect.
  - `:715-720` `StartPsFeedbackPumpP2` — reads
    `client.PsFeedbackFrames` channel.
  - `:743-770` `StartPsFeedbackPumpP1` — sibling for HL2; same shape.
  - `:798-820` `DrainPsFeedback` on disarm.

- **`Zeus.Server.Hosting/PsAutoAttenuateService.cs`** — 10 Hz background loop.
  - Constants `:55-78`: `IdealFeedback=152.293`, window `[128, 181]`,
    P1 attn range `0..31`, HL2 attn range `-28..+31`, `PostStepSettle=100ms`.
  - HL2 3-state machine `:107-122`: `Hl2AutoAttState{Monitor, SetNewValues,
    RestoreOperation}` with saved auto/single between disable→re-enable.
  - HW-peak auto-cal `:124-141` (post b87e70b + ec8fc74 simplification):
    `HwPeakSafetyMargin=1.02`, `HwPeakDeadbandRatio=0.05`, throttled to
    `AutoCalMinIntervalMs=1000`, skipped when `_hl2State != Monitor`.
  - `Tick1` `:194-313`: arm-edge reset, PsEnabled / TX-keyed / engine gates,
    runs `TickAutoCalHwPeak`, then either HL2 dance or P2 single-step path.
  - `TickAutoCalHwPeak` `:324-356`: env = `engine.GetPsStageMeters().MaxTxEnvelope`,
    target = clamp(env\*1.02, 0.05, 2.0); writes via `_radio.SetPsAdvanced`
    when current is ≥5 % off and HL2 dance is not mid-flight.
  - `Tick1Hl2` `:365-476`: Monitor (gate on `CalibrationAttemptsChanged` +
    `feedback>181 || feedback<=128 && attn>-28`) → `engine.SetPsControl(false,
    false)` → SetNewValues writes `p1.SetHl2TxStepAttenuationDb(newAttn)` +
    100 ms settle → RestoreOperation re-arms with saved auto/single.

- **`Zeus.Server.Hosting/PsSettingsStore.cs`** — LiteDB persistence
  (`zeus-prefs.db`).
  - Header (post 91105ad) explicitly excludes PsEnabled / PsAuto / PsSingle.
  - `PsSettingsEntry` fields persisted: `Auto`, `Ptol`, `AutoAttenuate`,
    `MoxDelaySec`, `LoopDelaySec`, `AmpDelayNs`, `IntsSpiPreset`, `Source`,
    TwoTone freq1/freq2/mag.
  - **NB:** comment says PsEnabled / PsAuto / PsSingle "reset to safe defaults
    each session" yet the entry still has `Auto` field and ctor does
    `PsAuto: ps?.Auto ?? true`. Cal-mode IS persisted, header is wrong; PS
    master arm is NOT persisted, header is right.

- **`Zeus.Server.Hosting/ZeusEndpoints.cs:432-512`** — REST surface:
  `POST /api/tx/ps`, `/api/tx/ps/advanced`, `/api/tx/ps/feedback-source`,
  `/api/tx/ps/monitor`, `/api/tx/ps/reset`, `/api/tx/ps/save`,
  `/api/tx/ps/restore`.

### Wire format (Zeus.Protocol1)

- **`Zeus.Protocol1/Protocol1Client.cs`**:
  - `:149-174` PS feedback channel + paired-block plumbing.
  - `:176-212` `_psTxRingI/Q` ring filled by `PsTapIqSource`
    (`:924-939`) on every TX-IQ sample written to EP2.
  - `:227-330` `HandlePs4DdcPacket` — decodes the HL2 4-DDC packet:
    DDC0→IqFrame (RX1 audio stays alive during PS+TX),
    DDC2→pscc RX (post-PA tap),
    DDC3→pscc TX (TX-DAC loopback). Emits `PsFeedbackFrame` every 1024
    samples. Heartbeat log every 190 blocks (~1 Hz at 192k).
  - `:451-491` `SetPsEnabled(bool)` (HL2-only wire effect; ANAN no-op),
    `SetPsPredistortion(value, subindex)`, `SetHl2TxStepAttenuationDb(db)`
    clamped `-28..+31`.
  - `:501-546` `SnapshotState` — `numRxMinus1=3` only when `psOn && isHl2 &&
    moxOn` (the gate that flips the radio into 4-DDC paired layout).
    `Hl2TxAttnDb` sentinel `int.MinValue` falls through to RX-side encoding.
  - `:603-630` RxLoop dispatch: when `_psEnabled && _mox && board==HL2`, route
    packet to `HandlePs4DdcPacket`; otherwise standard 1-DDC parser.
  - `:842-867` TxLoop emits PS-armed phase rotation (16-phase mod when
    psArmed, otherwise 4-phase) so `Predistortion (0x2b)` register fits
    without crowding TxFreq.

- **`Zeus.Protocol1/PsFeedbackFrame.cs`** — `record struct(TxI, TxQ, RxI, RxQ,
  SeqHint)`. Mirrors `Zeus.Protocol2.PsFeedbackFrame`. Header comment notes
  HL2 has an internal coupler; TX-side reference is operator's TX-IQ.

- **`Zeus.Protocol1/ControlFrame.cs`** — `WriteAttenuatorPayload` encodes
  `(31 - db) | 0x40` for HL2 TX-step-attn during MOX (mi0bot
  networkproto1.c:1086-1088 reference).

### Frontend (zeus-web/src)

- **`zeus-web/src/components/PsSettingsPanel.tsx`** — PURESIGNAL settings,
  fully redesigned in 5dfa3cd as a "live calibration dashboard": SVG
  convergence dial, signal-flow diagram, HW-peak vs observed bar, mode cards,
  `Run-now` (= one-shot), `Reset`. Calls `setPs`, `setPsAdvanced`,
  `setPsFeedbackSource`, `setPsMonitor`, `resetPs`, `setTwoTone` from
  `api/client`. `HL2_BOARD_ID="HermesLite2"` — Internal/External selector
  shown on every board (per CLAUDE.md HL2 internal-coupler invariant);
  PS-Monitor toggle hidden on HL2.
- **`zeus-web/src/components/PsToggleButton.tsx`** — transport-bar arm
  button (toggles `PsEnabled`).
- **`zeus-web/src/components/PsStatusPopover.tsx`** — hover popover; live
  feedback dial, cal state, mode, correction dB.
- **`zeus-web/src/layout/panels/PsFlexPanel.tsx`** — dockable wrapper that
  hosts `PsSettingsPanel` outside the Settings modal.
- **`zeus-web/src/state/tx-store.ts`** — Zustand store mirroring the PS
  StateDto fields; `PsMetersFrame` ingest from SignalR streams.

### TxMetersService (where the wire-back happens)

- **`Zeus.Server.Hosting/TxMetersService.cs`** — 10 Hz pump that calls
  `engine.GetPsStageMeters()`, broadcasts a `PsMetersFrame` to the hub, AND
  calls `_radio.UpdatePsLiveReadout(...)` so the StateDto carries the live
  feedback / cal-state / correcting fields the UI dial reads. (Confirm at
  cross-reference time — file present in `Zeus.Server.Hosting/`.)

### Recent commits (suspected change-set, oldest → newest)

1. `0df1c4b feat(p1): wire HL2 PureSignal feedback path through Protocol1Client`
2. `c5278da feat(ps): surface GetPSMaxTX observed envelope peak in panel`
3. `4571c02 feat(ps): warn when hw_peak diverges from default; lift WDSP dedup`
4. `b670dcf fix(ps): drop DspPipelineService hwPeak dedup` *(later reverted)*
5. `d71ae40 Revert "fix(ps): drop DspPipelineService hwPeak dedup"`
   — restored `_appliedPsHwPeak` change-gate in `DspPipelineService`.
6. `cd5b3e5 feat(ps): wire P1/HL2 PureSignal auto-attenuate via 3-state mi0bot dance`
   — added the HL2 timer2code state machine in `PsAutoAttenuateService`.
7. `b87e70b feat(ps): silent auto-calibration of hw_peak from observed TX envelope`
   — Zeus-side `TickAutoCalHwPeak` (DEVIATION FROM mi0bot, with explicit comment).
8. `ec8fc74 refactor(ps): simplify PS-AUTOCAL to spec algorithm` —
   replaced 1-s stability window with instantaneous-env + 1-s debounce +
   skip-during-dance gate. Faster initial lock, more aggressive overrides.
9. `5dfa3cd feat(ps): redesign PureSignal settings as live calibration dashboard` —
   pure UI rewrite of `PsSettingsPanel.tsx`. No backend changes per diff.
10. `91105ad Revert "feat(ps): persist PS-A master arm to LiteDB across restarts"` —
    `PsEnabled` no longer persisted; `PsSettingsEntry.Enabled` field removed.
    Cal-mode (PsAuto / PsSingle) still persisted via `Auto` field.

Suspect surface area (for cross-reference once mi0bot spec lands):
auto-cal hw_peak race vs operator override; HL2 dance ordering vs mi0bot
PSForm.cs:728-815; HwPeak change-gate in DspPipelineService vs the
"re-push to clear info[6]=0x0044" use case the lifted dedup was solving;
SetPsAdvanced HwPeak gate inside WdspDspEngine that's NOT change-gated
(double-write path with the auto-cal loop).

## Divergences found

Audit performed 2026-05-03 by `zeus-auditor`, refocused per team-lead onto the
**feedback-delivery path** after `bench-tester` reported PS stuck at
`calState=4 LCOLLECT` with `info[4] FeedbackLevel=0` indefinitely (TX envelope
healthy, two-tone visible on air, MaxTxEnvelope ≈ 0.22-0.23). Citations: `mi0bot
path:lines` (under `/Users/bek/Data/Repo/github/OpenHPSDR-Thetis/`) vs `Zeus
path:lines` (under this worktree). Ranked by **how-likely-this-is-the-COLLECT-stuck-bug**.

### D1 — `TickAutoCalHwPeak` locks the env→hw_peak ratio near 1.0, starving bins 0..13 of samples *(SMOKING GUN)*

**mi0bot.** §H. `hw_peak` is operator-tuned (`PSForm.cs:815-831`
`txtPSpeak.TextChanged → SetPSHWPeak`). Default for HL2 is `0.233`
(`clsHardwareSpecific.cs:311-312`). **mi0bot does NOT auto-tune `peak` at
all.** Per `calcc.c:701-739` (`LCOLLECT`), every TX sample bins as
`bin = (env * (1/peak)) * ints` only when `env*(1/peak) ≤ 1.0`; per
`calcc.c:747-748` the state advances out of `LCOLLECT` only when **every bin**
has `spi` samples (default 16 ints × 256 spi). The mi0bot design assumption is
that `hw_peak > observed_max_envelope`, so envelope modulation distributes
samples across bins 0..15 over time and `full_ints == ints` is reachable.

**Zeus.** `Zeus.Server.Hosting/PsAutoAttenuateService.cs:124-141, 315-356`
silently retargets `hw_peak ← env * 1.02` once per second whenever
`|cur - target| / cur ≥ 0.05`, throttled to 1 push/sec (commit `b87e70b`,
simplified by `ec8fc74`). Code carries an explicit `*** DEVIATION FROM mi0bot
***` banner. Bench symptom — operator forces `0.18 → snaps back to 0.2309 in
2 s` — is exactly this loop firing.

**Why this stops COLLECT cold.** When `hw_peak ≈ env_max * 1.02`,
`env / hw_peak` is locked at ~`0.98` for the envelope peak and varies only by
the TX modulation ratio. For a steady carrier (TUN) the envelope is a near-DC
sample → only bin 15 fills, bins 0..14 starve. For a two-tone, the envelope
dwell at any given level is short → only the top 2-3 bins approach `spi`. With
`count >= 4 * rate` (`calcc.c:751-760`) the per-bin counters flush every ~4 s
and the cycle repeats forever — exactly the bench symptom (`info[5]` /
`CalibrationAttempts` never increments, so the auto-att gate never even
arms). `info[4]` only updates inside `LCALC`, so it stays `0` while we churn in
`LCOLLECT`. **THIS IS the "feedback=0 in COLLECT forever" pattern.**

**Smoking-gun proof.** Operator memory note (`docs/puresignal.hl2.md:49-50`)
records `0.18 worked at drive=21 %` in a prior session — that's because at
that drive level, `env_max ≈ 0.18 / 1.07 ≈ 0.168`, leaving real headroom so
`env / hw_peak` *does* sweep across bin range. Auto-cal would have walked it
straight back to `env_max + 2 %` and reproduced the current breakage; bench
session that worked must have predated the auto-cal landing (commit `b87e70b`,
~04 h before today's regression report).

**Recommended fix scope (NOT for this audit, for task #4):** disable
`TickAutoCalHwPeak` on HL2 — let mi0bot's static-default + operator-override
model run. If "automatic" is still desired, the target must be
`hw_peak ≈ env_max * 1 / 0.6` (room for ~10 bins of distribution), NOT
`env_max * 1.02`.

---

### D2 — `Protocol1Client.SetPsEnabled` is an atomic flag flip; wire effects propagate via the round-robin only

> **Re-investigated 2026-05-03 per team-lead question 1.** Walking the wire path
> end-to-end shows Zeus DOES achieve every wire-side bit mi0bot's PSEnabled
> setter sets — but reaches them **piecemeal across the round-robin**, not as a
> single coordinated handshake. Whether that's a real bug for the COLLECT-stuck
> symptom depends on D5's instrumentation outcome (see below).

**mi0bot.** §D, `PSForm.cs:240-263`. `PSEnabled = true` runs five calls in
strict order, all on a single host turn, before `PSRunCal = true` gates
`pscc()`:

1. `console.UpdateDDCs(...)` — re-program the DDC table; for HL2 PS-MOX this
   sets `P1_DDCConfig=6`, `cntrl1=4`, `Rate[0..3] = rx1_rate`, then issues the
   wire sequence `EnableRxs → EnableRxSync → SetDDCRate(0..3) →
   SetADC_cntrl1 → SetADC_cntrl2 → CmdRx → Protocol1DDCConfig`
   (`console.cs:8246-8263, 8343-8350`).
2. `NetworkIO.SetPureSignal(1)` — master PS enable bit on the wire.
3. **`NetworkIO.SendHighPriority(1)`** — explicit immediate flush of a
   HighPriority packet so the firmware sees the new state in the next
   wire tick, not the next round-robin slot.
4. `console.UpdateAAudioMixerStates()` — host-side audio re-route.
5. `cmaster.LoadRouterControlBit(...)` — switch the cmaster router so DDC2/DDC3
   feed pscc rx/tx (HL2 `cmaster.cs:611-635` FOUR_DDC table).
6. `console.radio.GetDSPTX(0).PSRunCal = true` — gate WDSP `pscc()`.

**Zeus.** Two-layer flag flip + round-robin propagation. The body of
`Protocol1Client.SetPsEnabled` is **literally one line**
(`Zeus.Protocol1/Protocol1Client.cs:465-468`):

```csharp
public void SetPsEnabled(bool on)
    => Interlocked.Exchange(ref _psEnabled, on ? 1 : 0);
```

There is **no wire move triggered by this call.** The `_psEnabled` flag drives
two downstream readers:

| Reader (file:line)                          | Effect when `_psEnabled=1` AND `_mox=1` AND board=HL2          |
|---------------------------------------------|----------------------------------------------------------------|
| `Protocol1Client.cs:510-545` `SnapshotState` | `numRxMinus1=3` (Config C4 [5:3]); `PsEnabled=true` field passed into every register payload |
| `Protocol1Client.cs:603-630` `RxLoop` dispatch | Switch parser to `HandlePs4DdcPacket` — i.e. assume the radio is already in 4-DDC layout |

The wire-side moves then happen as the next `TxLoopAsync`
(`Protocol1Client.cs:826-881`) iteration build a packet, calling
`PhaseRegisters(phase, mox=true, psArmed=true)`
(`Protocol1Client.cs:746-803`). Two registers per packet, 381 pkts/s, the
psArmed+mox rotation is mod-16 and includes:

| Phase | First register     | Second register | Wire byte set when fires                                                  |
|-------|--------------------|-----------------|---------------------------------------------------------------------------|
| 2,10  | `Attenuator`       | TxFreq/RxFreq3  | C2 bit 6 = `puresignal_run` (`ControlFrame.cs:277-280`) — equals mi0bot step (2) |
| 6,14  | `AdcRouting`       | TxFreq/RxFreq3  | C1 = `0x04` when MOX (`ControlFrame.cs:295-296`) — equals mi0bot `cntrl1=4` (step 1 partial) |
| 7,15  | `Config`           | TxFreq/RxFreq4  | C4 [5:3] = `numRxMinus1=3` (`ControlFrame.cs:354-356`) — equals mi0bot nddc-1 (step 1 partial) |
| 0,8,11| `TxFreq`           | RxFreq3/4/Drive | per-DDC NCO at TX freq (DDC0 audio NCO is RxFreq, DDC2/3 use RxFreq3/4)   |
| 4,9   | `RxFreq` / `RxFreq2`| RxFreq3/4      | DDC0/DDC1 NCO programmed                                                  |
| 3,11  | `TxFreq`           | `DriveFilter`   | TX drive level                                                            |

Worst-case landing time for all six PS-critical registers ≈ **42 ms**
(16 phases × 1/381 s × 1 = ~42 ms / cycle, all six registers fire at least
once per cycle). The 100 ms `Task.Delay` in `DspPipelineService.cs:515` is
designed to absorb this, but does so AFTER `_psEnabled` is already true.
That's the wrong order vs mi0bot — mi0bot flushes the register, THEN flips
PSRunCal.

**What Zeus does NOT do that mi0bot does:**
- **No explicit HighPriority flush** (mi0bot step 3). Zeus relies on the
  next round-robin tick.
- **No per-DDC rate write** (mi0bot `SetDDCRate(0..3)`). Protocol-1 carries a
  single rate field in `Config` C1 [1:0] for ALL DDCs — Zeus inherits whatever
  RX1 sample rate the operator picked. mi0bot's `console.cs:8246-8263` notes
  HL2 keeps DDC0/1 at `rx1_rate` for "high sample rate" — Zeus already does
  this implicitly because all DDCs share the rate field.
- **No `EnableRxs / EnableRxSync` separate writes.** mi0bot's `UpdateDDCs`
  calls these as distinct host commands; Zeus collapses everything into the
  single `Config` register write. Protocol-1 spec (HL2 doc) says these
  enables ARE driven from the same Config C4 nddc field, so this MAY be a
  no-op divergence — but unverified against HL2 firmware behaviour.
- **No `Protocol1DDCConfig` (selector word).** mi0bot's `UpdateDDCs`
  finishes with this; the firmware-side selector that picks "PS-active TX"
  vs "RX-only" layouts. **This is the most suspicious gap** — if the HL2
  firmware needs an explicit `P1_DDCConfig=6` write that Zeus doesn't issue,
  the gateware never enters PS-MOX mode and DDC2/DDC3 stay zero or random
  even after `Config(numRxMinus1=3)` lands. **NEEDS BENCH VERIFICATION** — see
  D5 instrumentation table.

**Why the existing 100 ms `Task.Delay` does not save us.** Mi0bot's order is
"set the wire, then flip PSRunCal". Zeus's order is "flip the flag (which
allows the round-robin to start drifting the new bytes onto the wire), wait
100 ms, then flip PSRunCal in WDSP". By that 100 ms point, all six register
slots have fired at least twice — UNLESS some register isn't actually in the
rotation, or the firmware needs a wire byte Zeus doesn't write (the
`Protocol1DDCConfig` selector).

**Verdict.** Zeus's wire-side PS handshake is a **round-robin equivalent of
mi0bot's coordinated 5-step setter, with one possibly-missing wire write
(`Protocol1DDCConfig` selector)**. If D5 instrumentation shows
`p1.ps.fb DDC2(rx) peak=0` (TX reference flowing on DDC3, but PA-loopback
RX feedback dead), this divergence is probably the cause. If D5 shows
`p1.ps.fb` lines completely absent, the parser is never being entered (
gate failure or magic-header reject — investigate `RxLoop` first). If D5
shows healthy peaks on BOTH DDC2 and DDC3, the wire path is fine and **D1
is the bug.**

---

### D3 — Predistortion register (0x2b) is never sent on the wire

**mi0bot.** Predistortion subindex+value are written every PS tick once the
firmware switches to PS-active mode. The 0x2b register carries the
`predistortion subindex` (C1, 0..255) and `predistortion value` (C2 [3:0],
0..15) per the HL2 protocol doc. mi0bot writes these from inside the
ChannelMaster routing — `cmaster.cs` plus `clsHardwareSpecific.cs` HL2 case.

**Zeus.** `Zeus.Protocol1/ControlFrame.cs:106-111, 302-315` defines
`CcRegister.Predistortion = 0x56` and `WritePredistortionPayload`, and
`Zeus.Protocol1/Protocol1Client.cs:478-482` exposes `SetPsPredistortion`.
**But:** the `PhaseRegisters` table at `Protocol1Client.cs:746-803` does
NOT include `Predistortion` in the round-robin under any branch. The
in-source comment at `:759` says `// … Predistortion is omitted; …`. And no
caller in the codebase invokes `SetPsPredistortion(...)` (grep across
Zeus.Server.Hosting, Zeus.Dsp, Zeus.Protocol1 — zero hits). So the 0x2b
frame never goes on the wire.

**Impact on COLLECT.** Predistortion = the *output* of the calcc fit applied
back to TX. Without it the radio transmits uncorrected, but COLLECT (which
just bins TX/RX I/Q samples) does not depend on it. **Not the COLLECT-stuck
bug.** It IS a correction-application bug: even if calcc converges, the
correction never reaches the radio. Worth fixing once D1 unblocks COLLECT.

---

### D4 — `PsAutoAttenuateService` evaluates HL2 dance gates correctly but only fires after `CalibrationAttemptsChanged`, which never ticks while D1 is in play

**mi0bot.** §G.1, `PSForm.cs:734-780`. Trigger requires
`puresignal.CalibrationAttemptsChanged` (= `_info[5] != _oldInfo[5]`). Every
3-state dance run is gated on a fresh `cor.cnt` increment, which only
happens at the end of `LCALC` (`calcc.c:773-801`).

**Zeus.** `PsAutoAttenuateService.cs:385-391` and `:273-278` — same gate
(`_lastCalibrationAttempts != psm.CalibrationAttempts`). The HL2 ddB clamp
(±10 not ±100) at `:413-417` matches mi0bot `PSForm.cs:756-761` exactly.
The `NeedToRecalibrate_HL2` predicate at `:402-403` matches
`PSForm.cs:1134-1137` (FB > 181 OR (FB ≤ 128 AND attn > -28)).

**Conclusion.** Auto-att is correctly paralysed waiting for `info[5]` to tick
— but `info[5]` cannot tick because `LCOLLECT` never advances (D1). Not a
divergence per se, but **important secondary symptom**: the auto-att step
attenuator never moves either, so the radio is sitting at whatever step
attenuation it had at PS-arm time (default 0). If D1 is fixed, this loop
should immediately start working as designed.

---

### D5 — Existing log surface answers team-lead question 2 ("does anything prove DDC2/DDC3 arrived?")

**mi0bot.** Built-in: every TX-armed cycle increments `cor.cnt` (visible in
`info[5]`) and `feedback_level` (visible in `info[4]`); `txtGetPSpeak` shows
live `GetPSMaxTX`. UI feedback is immediate and can be screenshotted.

**Zeus.** YES — **the existing instrumentation already answers this question
without any code change.** Three logs at INFO level cover the full feedback
path. Bench-tester just needs to grep the server log during the next 5-second
PS+MOX rack key:

| Log line (file:line)                                                      | Frequency       | Counter source           | What it proves                                                       |
|---------------------------------------------------------------------------|-----------------|--------------------------|----------------------------------------------------------------------|
| `p1.ps.fb DDC2(rx) peak=… mean=… DDC3(tx) peak=… mean=… blocks=…` (`Protocol1Client.cs:314-318`) | every 190 blocks ≈ **1 Hz** at 192 kHz | `_psBlocksEmitted` | DDC2/DDC3 packets arrived AND the parser produced 1024-sample blocks |
| `wdsp.pscc fed N blocks` (`WdspDspEngine.cs:2032-2039`)                   | every 100th call ≈ **2 Hz** at 192 kHz | `_psFeedCount`     | Feedback pump (`StartPsFeedbackPumpP1`) is alive AND blocks reach `psccF` |
| `wdsp.psState {Prev}->{Cur} info4=… info6=… info13=… info14=…` (`WdspDspEngine.cs:2125-2140`) | edge-triggered on each calcc state change | `_lastLoggedPsState` | calcc state machine progressing AND what's in info[4..15] at each transition |

There is also `Protocol1Client.PsPairedPacketCount` (`Protocol1Client.cs:172`,
exposed via `IProtocol1Client.PsPairedPacketCount` interface) — a public
property incremented on every parsed 4-DDC packet. **Not currently logged
anywhere**; if bench-tester wants to verify packets arrive even when blocks
don't form (1024 / 38 ≈ 27 packets per block, so ~10000 packets/sec at 192k),
adding a tick-1 log of this counter is a one-line addition.

**The presence/absence pattern localises the failure:**

| `p1.ps.fb` | `wdsp.pscc fed` | `wdsp.psState` | Diagnosis                                                        |
|------------|-----------------|----------------|-------------------------------------------------------------------|
| absent     | absent          | stuck @ COLLECT| **D2 — wire-side handshake.** `Protocol1DDCConfig` selector likely missing; HL2 firmware never enters 4-DDC PS layout. Or: RxLoop dispatch gate failing (state desync). Add a one-line log of `Volatile.Read(ref _psEnabled), _mox, _boardKind` at the dispatch point to localise. |
| present, peaks both ≈ 0 | present  | stuck @ COLLECT| Parser entering on garbage packets (single-DDC mis-decoded as 4-DDC). Confirms D2 — `Protocol1DDCConfig` write missing. |
| present, DDC3(tx) peak ≈ 0.22 BUT DDC2(rx) peak ≈ 0 | present | stuck @ COLLECT | Wire layout switched (TX reference flowing on DDC3) but PA-loopback ADC routing dead. **Confirms D2 too** — gateware not honouring `cntrl1=4` without a separate selector write. |
| present, both peaks > 0 | present | stuck @ COLLECT | **D1 confirmed.** Wire path delivers good samples, calcc is binning them, but `auto-cal hw_peak` is locking the env/hw_peak ratio so bins 0..13 starve. Cross-check by also greppling for `psAutoAttn.autoCal env=… oldHw=… newHw=…` (`PsAutoAttenuateService.cs:351-353`) — one push/sec while keyed. |
| present, both peaks > 0 | present | advances thru CALC | Working. Bench result is something else. |

**Bench-tester action requested before task #4 starts:** capture 5 seconds of
`journalctl -u zeus-server` (or `stdout`) during a fresh PS+MOX rack key on
28.400 MHz; search for the three log prefixes; report which row of the table
above matches. **This is the highest-value next step** — it disambiguates D1
vs D2 in a single rack run with zero code changes.

---

### D6 — `TurnOFF` does not poll engine state before returning to `OFF`

**mi0bot.** §F, `PSForm.cs:701-716`. The `TurnOFF` state stays in
`TurnOFF`, repeatedly pushing `SetPSControl(1,0,0,0)`, until
`!CorrectionsBeingApplied && State == LRESET` — i.e. it polls until the
engine actually reaches `LRESET` and drops `info[14]`.

**Zeus.** `Zeus.Dsp/Wdsp/WdspDspEngine.cs:1894-1912` (`SetPsEnabled(false)`)
runs once: tear down PS-FB analyzer, push 7× zero psccF blocks (pihpsdr
shutdown shape, `transmitter.c:2422-2444`), `SetPSRunCal(0)`,
`SetPSControl(1,0,0,0)`. No polling. **Divergence: stale IQC corrections may
linger after disarm if the engine hadn't yet drained.**

**Impact on COLLECT-stuck.** None directly. But: a re-arm shortly after a
disarm could land into a `LSTAYON` that mi0bot would have cleared. Worth
fixing post-D1.

---

### D7 — `StayON` does not drop wire-side `PsEnabled` (mi0bot does)

**mi0bot.** §L item 8, `PSForm.cs:678`. When the command state machine
enters `StayON`, the `PSEnabled` setter is called with `false` so the
feedback DDCs un-route while corrections continue to be applied by IQC.

**Zeus.** No equivalent. `Zeus.Server.Hosting/RadioService.cs:1062-1080`
`PsEnabled` mutates only on operator action; the engine's internal `LSTAYON`
transition is invisible to the wire. Feedback DDCs stay routed.

**Impact on COLLECT-stuck.** None directly. Cosmetic / efficiency
divergence.

---

### D8 — `PsFeedbackSource` UI selector has no mi0bot equivalent (intentional Zeus deviation)

**mi0bot.** §K. No Internal/External feedback-source selector exists in
PSForm. HL2 has a single ADC and a single hard-wired feedback path.

**Zeus.** `PsSettingsPanel.tsx` shows the selector on every board including
HL2 (per CLAUDE.md "HL2 has internal coupler — keep PS Internal/External
feedback-source radio on HL2"). The wire-side effect is P2-only
(`DspPipelineService.cs:531`). On HL2 the toggle is cosmetic.

**Impact on COLLECT-stuck.** None — it's a UI-only deviation and the user
preference is documented. **Do not "fix" this** without explicit maintainer
sign-off.

---

### Top-3 suspects (per team-lead request, refined 2026-05-03)

Equal-weighted between D1 and D2 until D5 instrumentation discriminates:

1. **D1 — `PsAutoAttenuateService.TickAutoCalHwPeak`** locks env/hw_peak
   ratio near 1.0, starving LCOLLECT bins 0..13. Single source-line gives
   the smoking-gun mechanism for "stuck COLLECT, info[4]=0", AND explains
   "operator forces hw_peak=0.18, snaps back to 0.2309 in 2 s" in the same
   loop. mi0bot has NO `peak` auto-tuning at all (§H), so removing this is
   spec-aligned.
2. **D2 — `Protocol1Client.SetPsEnabled` is a one-line atomic flag flip;
   wire effects propagate via the round-robin only.** Specifically Zeus
   does not appear to write the equivalent of mi0bot's
   `Protocol1DDCConfig` selector (the firmware-side wire byte that
   actually switches the HL2 gateware into PS-MOX 4-DDC mode after
   `Config(numRxMinus1=3)` lands). If the firmware needs that selector,
   no amount of D1 fixes will produce DDC2/DDC3 feedback samples — they
   stay zero forever and COLLECT sits with `info[4]=0`.
3. **D5 — Existing `p1.ps.fb` / `wdsp.pscc fed` / `wdsp.psState` logs
   already discriminate D1 vs D2 in one rack run.** No code change needed
   for the discrimination; bench-tester captures `journalctl -u zeus-server`
   during a 5-second PS+MOX rack key and reports the presence/absence
   pattern against the table in D5.

**Recommended task #4 sequencing:**
- Step 0 (no code): bench-tester runs the D5 log capture on the
  existing build. Decide D1 vs D2 from the table.
- Step 1a (if D1): one-line `if (board == HermesLite2) return;` at the
  top of `TickAutoCalHwPeak` (`PsAutoAttenuateService.cs:324`). Re-bench.
- Step 1b (if D2): instrument `Protocol1DDCConfig` write — the
  `console.cs:8246-8263` and `:8343-8350` mi0bot wire sequence likely
  needs a new `CcRegister` and a one-shot send on PS-arm before the
  `Task.Delay(100)` in `DspPipelineService.cs:515`. Read mi0bot
  `networkproto1.c` first to identify the exact wire byte; do NOT
  guess.
- Step 2 (after primary fix lands): D3 (Predistortion register never
  sent) for correction-application; D6 (TurnOFF doesn't poll engine)
  for clean disarm.

---

## Parser & emit verification (post-D5)

> **Update 2026-05-03 (REVISED — V1 RETRACTED).** mi0bot-spec confirmed via
> `console.cs:8189` (`P1_rxcount = 4; nddc = 4` for HERMES/HERMESLITE
> family) AND parser indexing `k = 8 + isample * 26 + iddc * 6` (matching
> Zeus's `TryParseHl2Ps4DdcPacket` exactly): **mi0bot's HL2 default IS the
> 4-DDC layout.** Zeus's parser layout matches mi0bot. The protocol doc
> statement at `docs/references/protocol-1/hermes-lite2-protocol.md:455-460`
> ("Zeus implements the 2-DDC paired layout") was the original PR design
> from commit `1e53807`; commit `4ed2e63` correctly upgraded to the
> 4-DDC layout that matches mi0bot's HL2 default. The dead-code
> `TryParsePsPairedPacket` 2-DDC parser was orphaned, not regressed.
> V1 below is RETAINED for audit-trail completeness but **superseded by
> the M1 finding in the next section** — the parser is fine; the PA is muted.
>
> mi0bot-spec also confirmed mi0bot writes **ZERO `0x2b` register frames**
> in the entire codebase. Zeus's dead `Predistortion` encoder
> (`ControlFrame.cs:111, 222, 302-315`) is therefore mi0bot-aligned by
> accident: never invoked at runtime (`PhaseRegisters` table at
> `Protocol1Client.cs:746-803` doesn't include it; grepped — no other
> caller exists), and even if it were, mi0bot doesn't either.
> **D3 is withdrawn — not a bug.**
>
> Original D5 capture context retained: bench-tester reports 4-DDC frames
> arriving at expected 192 kHz cadence (parser block counter ticks
> normally) BUT both `DDC2(rx) peak=0.0000` AND `DDC3(tx) peak=0.0000` in
> the `p1.ps.fb` log. **The PA is muted (M1) → no RF anywhere → both
> DDC2 (post-PA RX tap) AND DDC3 (TX-DAC loopback, taken AFTER the
> AD9866 PGA) read zero.**

### V1 — RETRACTED: Zeus uses 4-DDC parser, which matches mi0bot

Zeus carries **two** HL2 PureSignal packet decoders side-by-side in
`Zeus.Protocol1/PacketParser.cs`:

| Decoder                        | File:line                              | Slot bytes | DDCs/slot | Samples/packet | Caller in production code             |
|--------------------------------|----------------------------------------|------------|-----------|----------------|---------------------------------------|
| `TryParsePsPairedPacket`       | `PacketParser.cs:303-349`              | 14         | 2         | 72             | **DEAD CODE in P1.** Only `Zeus.Protocol2/Protocol2Client.cs:1130` calls it (P2 path). |
| `TryParseHl2Ps4DdcPacket`      | `PacketParser.cs:375-429`              | 26         | 4         | 38             | `Zeus.Protocol1/Protocol1Client.cs:237` (called from `HandlePs4DdcPacket` at `:227`, dispatched from `RxLoop` at `:603-607`). |

**The two decoders' header comments contradict each other on which mi0bot
source they're matching:**

- `TryParsePsPairedPacket` header (`PacketParser.cs:268-281`) — claims
  **"nddc=2"** with **"DDC1 carries the dedicated feedback ADC samples"**;
  cites `mi0bot networkproto1.c:990, 1005`.
- `TryParseHl2Ps4DdcPacket` header (`PacketParser.cs:351-364`) — claims
  **"mi0bot's HL2 path always uses nddc=4 during PS+MOX"**; cites
  `console.cs:8186-8265` and `networkproto1.c:WriteMainLoop_HL2 case 5/6`.
  Routes DDC2 → pscc rx, DDC3 → pscc tx.

**These claims cannot both be true.** mi0bot-spec §J quoted directly from
mi0bot `console.cs:8246-8263`:

```csharp
else // transmitting and PS is ON
{
    P1_DDCConfig = 6;
    DDCEnable = DDC0;       // 0x01
    SyncEnable = DDC1;      // 0x02 — DDC1 sync-paired to DDC0
    if (hpsdr_model == HPSDRModel.HERMESLITE) {
        Rate[0] = rx1_rate;
        Rate[1] = rx1_rate;
    }
    cntrl1 = 4;             // ADC routing word: PS feedback path enabled
}
```

`DDCEnable=0x01` enables only DDC0; `SyncEnable=0x02` sync-pairs DDC1 to
DDC0. Only `Rate[0]` and `Rate[1]` are programmed (DDC2/DDC3 are NOT
configured). This is a **2-DDC paired layout**, not 4-DDC. The cmaster
FOUR_DDC router table in §B.3 is the **host-side software router** indexed
by `tot` state, not the wire layout — its name is misleading.

**Conclusion:** the `TryParsePsPairedPacket` (2-DDC) header was correct;
`TryParseHl2Ps4DdcPacket` (4-DDC) misread mi0bot — the `nddc=4` claim has no
mi0bot source backing it. **Zeus is forcing the radio into a 4-DDC layout the
firmware never expects mi0bot to ask for, then parsing the resulting packets
as if DDC2 and DDC3 carry meaningful data.** Since DDC2 and DDC3 NCO
frequencies are never programmed (Zeus rotation does write `RxFreq3` and
`RxFreq4`, but the gateware has no DDC2/DDC3 paths active in the mi0bot-mode
HL2 firmware), the slots in the EP6 packet are zero — exactly the bench
symptom.

**Commit history confirms:** the 4-DDC path landed in commit `4ed2e63
"feat(ps); working on the herpes"` (typo for "hermes"), 2026-05-03 02:59 UTC.
Commit message body literally reads `"test on G2 needed"`. It was committed
**without HL2 bench verification** and the prior 2-DDC parser was orphaned
in the same commit but not deleted.

**Why bench-tester sees the parser running but values=0:** the 4-DDC parser
asks the radio for 26-byte/slot packets, the radio dutifully fills 26 bytes
(DDC0 + DDC1 + zeros for DDC2/DDC3 + 2 mic), the parser dutifully decodes 4
streams, two of which are always zero. `_psBlocksEmitted` ticks normally
because 1024-sample blocks fill in ~28 packets regardless of stream content.

### V2 — Wire-emit of `numRxMinus1` and `puresignal_run` is byte-correct AND the value matches mi0bot

Per team-lead Q2 — verified end-to-end. The serialisation chain is:

1. `Protocol1Client.SnapshotState` (`Protocol1Client.cs:501-546`) reads
   `_psEnabled`, `_mox`, `_boardKind` and constructs `CcState` with
   `NumReceiversMinusOne = (psOn && isHl2 && moxOn) ? 3 : 0` and
   `PsEnabled = true`.
2. `TxLoopAsync` (`Protocol1Client.cs:826-881`) calls
   `ControlFrame.BuildDataPacket(buf, sendSeq++, first, second, in state, _txIqSource)`.
3. `BuildDataPacket` (`ControlFrame.cs:364-386`) writes the Metis header
   (`packet[0..3] = 0xEF 0xFE 0x01 0x02`, sequence in `[4..8]`) then writes
   two USB frames at `[8..520]` and `[520..1032]`.
4. `WriteUsbFrame` (`ControlFrame.cs:404-...`) writes 3-byte sync
   `0x7F 0x7F 0x7F` at offset 0..2, then `WriteCcBytes(frame.Slice(3, 5),
   register, in state)`.
5. `WriteCcBytes` (`ControlFrame.cs:169-232`) writes
   `cc[0] = (register & 0xFE) | (state.Mox ? 1 : 0)` (XmitBit at bit 0)
   and dispatches to `WriteConfigPayload` / `WriteAttenuatorPayload` etc.
6. `WriteConfigPayload` (`ControlFrame.cs:317-356`) writes
   `c14[3] = 0x04 | (NumReceiversMinusOne & 0x07) << 3` →
   **with `numRxMinus1=3`, `c4 = 0x04 | 0x18 = 0x1C`** in Config register's
   C4 byte.
7. `WriteAttenuatorPayload` (`ControlFrame.cs:234-281`) writes
   `c14[1] |= 1 << 6` when `Board==HermesLite2 && PsEnabled` →
   **C2 bit 6 set unconditionally during PsEnabled** (independent of MOX,
   matching mi0bot networkproto1.c:1102).
8. `sock.SendToAsync(buf, ...)` at `Protocol1Client.cs:869` puts the buffer
   on the wire — no further filtering.

**There is NO middleware flag check between SnapshotState and
the network send.** The bytes Zeus believes it's sending ARE on the wire.

**Therefore:**
- ✓ `cc[0]` of Config (0x00) wire byte is **`0x01` when MOX** (XmitBit set) — matches mi0bot.
- ✓ `cc[0]` of Attenuator (0x14) wire byte is **`0x29` when MOX** (`0x14 << 1 | 0x01`) — matches mi0bot.
- ✓ Config C4 [5:3] carries the 3-bit `numRxMinus1` value at runtime — Zeus emits **`(3 << 3) | 0x04 = 0x1C`** when `psArmed && mox`.
- ✓ Attenuator (0x14) C2 bit 6 carries `puresignal_run` at runtime — Zeus emits **`0x40 | other_bits`** when PsEnabled.

The wire emit is correct *for what Zeus is asking for* — and per
mi0bot-spec follow-up, **`nddc=4` (numRxMinus1=3) IS what mi0bot sends for
HL2 PS-MOX** (`console.cs:8189` `P1_rxcount=4; nddc=4` for the
HERMES/HERMESLITE family branch). Zeus matches mi0bot byte-for-byte on
this register. ✓

### V3 — RETRACTED: layout fix is unnecessary; M1 is the actual root cause

Original V3 proposed switching to 2-DDC paired layout. **mi0bot-spec
disconfirmed this:** mi0bot's HL2 default IS the 4-DDC layout, with
DDC2=PS-RX feedback (post-PA tap, fed to pscc rx) and DDC3=PS-TX
loopback (TX-DAC reference taken AFTER the AD9866 PGA, fed to pscc tx).
Zeus's parser layout matches mi0bot exactly — slot stride 26 bytes,
indexing `off + iddc * 6`, 24-bit big-endian I and Q.

**The real bug is M1 (PA mute via wrong `0x14 C4` TX-side encoding) in the
next section.** With the AD9866 TX PGA muted by Zeus's out-of-range wire
byte, no RF reaches the PA. DDC2 (post-PA RX tap) reads zero because no
RF is on the antenna. DDC3 (TX-DAC loopback after PGA) reads zero because
the PGA itself is muted. Both DDC2 and DDC3 = 0 is the **correct** parser
behaviour given the muted-PA upstream condition.

**For task #4 — see `## PA-output mute audit` section below for M1 fix.**
After M1 lands and DDC2/DDC3 become non-zero, D1 (auto-cal hw_peak) needs
re-evaluation against actual bin-fill behaviour.

---

## PA-output mute audit (post-operator-feedback)

> **Update 2026-05-03.** Operator confirmed: "no power when PureSignal is
> on". DDC2/DDC3 = 0 in `p1.ps.fb` is a SECONDARY symptom — there is no RF
> anywhere in the chain because the HL2 PA is being silenced when PS arms.
> V1's parser-layout finding is still real but it's downstream of this:
> with the PA muted, even a 2-DDC parser would see zero feedback. **THIS
> SECTION SUPERSEDES V1/V3 as the root-cause analysis.** Fix this first,
> then re-test the parser layout question.

### Wire-byte delta when `psEnabled` flips false→true on HL2

Walked every CC register Zeus emits, comparing pre-PS-MOX to post-PS-MOX
with the same drive % and same operator state. Differences:

| Register / Field                                                | Pre-PS-MOX        | Post-PS-MOX                          | Source                                                              |
|------------------------------------------------------------------|-------------------|---------------------------------------|----------------------------------------------------------------------|
| 0x14 C2[6] (puresignal_run)                                      | 0                 | **1**                                 | `ControlFrame.cs:277-280` `WriteAttenuatorPayload`                  |
| 0x14 C4 (TX-side AD9866 PGA when XmitBit)                        | `0x40 ⎮ (60−db)` = `0x7C` for db=0 | **same** `0x7C` (no change!) — see P1 below | `ControlFrame.cs:241-243`                                           |
| 0x1c C1 (cntrl1 = ADC routing)                                   | 0                 | **0x04** (DDC1 → ADC1)                | `ControlFrame.cs:295-296` `WriteAdcRoutingPayload`                  |
| 0x00 C4 [5:3] (numRxMinus1)                                      | 0                 | **3** (= 4 DDCs requested)            | `ControlFrame.cs:354-356` `WriteConfigPayload` + `Protocol1Client.cs:522` |
| Round-robin schedule                                             | 4-phase mod       | **16-phase mod** (`PhaseRegisters` `psArmed=true`) | `Protocol1Client.cs:746-803`                                        |
| 0x12 (DriveFilter) C1 = DriveLevel                               | unchanged         | unchanged (no `RecomputePaAndPush` on PS-arm) | confirmed: `RadioService.cs:850-855` `SetMox` does not call recompute |
| 0x12 (DriveFilter) C2[3] (PA enable)                             | 1 during MOX      | unchanged 1 during MOX                | `ControlFrame.cs:208-211`                                           |
| Engine: WDSP `SetPSRunCal(1)`                                     | not called        | called once                           | `WdspDspEngine.cs:1877`                                              |
| Engine: WDSP `SetPSControl(1, mancal, automode, 0)`              | not called        | called once                           | `WdspDspEngine.cs:1883`                                              |
| Engine: WDSP `OpenPsFeedbackAnalyzer`                             | not called        | called once                           | `WdspDspEngine.cs:1891`                                              |

**Important null findings:**

- `RadioService.RecomputePaAndPush` is NOT called on PS arm. Drive byte stays
  whatever it was before. PA enable bit stays set.
- The auto-att 3-state dance (`PsAutoAttenuateService.Tick1Hl2`) is gated on
  `feedback > 0` (via `LogGate("hl2.skip=fb-zero")` early return at
  `:394-398`); since calcc never sees real feedback samples,
  `_hl2TxAttnDb` stays at the `int.MinValue` sentinel and
  `SetHl2TxStepAttenuationDb` is never called.
- `Protocol1Client.SetPsEnabled` does not touch any drive / PA-control field
  (`Protocol1Client.cs:465-468` is one line).

### M1 — 0x14 C4 wire-byte: Zeus uses RX-side encoding during MOX, mi0bot uses TX-side encoding *(SMOKING GUN)*

**HL2 protocol doc** at `docs/references/protocol-1/hermes-lite2-protocol.md:381-384`
quotes mi0bot `networkproto1.c:1102` `case 11: // Preamp control 0x0a` literally:

```c
if (XmitBit) C4 = (prn->adc[0].tx_step_attn & 0b00111111) | 0b01000000;
else         C4 = (prn->adc[0].rx_step_attn & 0b00111111) | 0b01000000;
```

mi0bot maintains **two separate fields** (`tx_step_attn` and `rx_step_attn`)
and the C4 wire byte switches based on `XmitBit`. The default values:

- `rx_step_attn` defaults to 60 (= max RX gain, no attenuation)
- `tx_step_attn` defaults to **31** (= `31 - txatt` with `txatt=0` per
  spec §G.2 mi0bot `console.cs:19610` `SetTxAttenData(31 - txatt)`)

So mi0bot's wire bytes:
- !XmitBit (RX): `(60 & 0x3F) | 0x40 = 0x7C`
- XmitBit (TX, default): `(31 & 0x3F) | 0x40 = 0x5F`

**Zeus** at `Zeus.Protocol1/ControlFrame.cs:234-262` `WriteAttenuatorPayload`:

```csharp
int db = s.Atten.ClampedDb;
byte c4 = s.Board == HpsdrBoardKind.HermesLite2
    ? (byte)(0x40 | Math.Clamp(60 - db, 0, 60))   // RX-side encoding ALWAYS
    : (byte)(0x20 | (db & 0x1F));

if (s.Board == HpsdrBoardKind.HermesLite2
    && s.Mox
    && s.Hl2TxAttnDb != int.MinValue)             // ONLY if auto-att has fired
{
    c4 = (byte)(Math.Clamp(31 - s.Hl2TxAttnDb, 0, 60) | 0x40);
}
```

Zeus writes `0x40 | (60 - db) = 0x7C` for db=0 in **both** XmitBit=0 and
XmitBit=1 branches. The TX-side encoding only activates when
`Hl2TxAttnDb != int.MinValue`, which never happens on a fresh PS arm
(because the auto-att dance is gated on `feedback > 0` and feedback is
zero until the radio sends real samples).

**Why this manifests only on PS arm:**

Per the protocol doc and mi0bot reference, the HL2 firmware reads C4 of
the 0x14 frame as follows:

- **!XmitBit:** wire byte → `rx_step_attn` (RX gain, range 0..60).
- **XmitBit AND `puresignal_run=0`:** the TX path is driven by the
  legacy DriveLevel (0x12 C1) and the gateware's PA control. Whatever
  C4 wire byte the host sends is **either ignored or treated permissively**
  in this mode — Zeus's `0x7C` worked pre-PS for years.
- **XmitBit AND `puresignal_run=1`:** the firmware enters strict PS mode.
  C4 is now read STRICTLY as `tx_step_attn` for the AD9866 TX PGA. The
  6-bit field `[5:0]` valid range is mi0bot's `0..59` (= `txatt = 31..-28`).
  Zeus's wire byte `0x7C & 0x3F = 60` is **out-of-range**; the AD9866 either
  saturates to maximum attenuation, wraps to a deep gain reduction, or
  enters an undefined state. **PA goes silent.**

mi0bot would have written `0x5F` (= `txatt = 0`, neutral PGA) and the PA
stays at full drive.

**Bench-test prediction:**
- Drive 21 % @ 28.4 MHz, PS off, MOX on → 1.5 W out (operator confirmed
  pre-PS power was healthy).
- Drive 21 % @ 28.4 MHz, PS on, MOX on → 0.0 W out (operator confirmed
  current symptom).
- Patch C4 to `0x5F` always when board=HL2 && MOX → expect 1.5 W out
  with PS armed.

### M2 — 0x14 C1, C3 are zero in Zeus but mi0bot writes preamp / mic / user_dig_out bytes

`HL2 protocol doc:373-380` quotes mi0bot:

```c
C1 = (prn->rx[0].preamp & 1) | ((prn->rx[1].preamp & 1) << 1) |
     ((prn->rx[2].preamp & 1) << 2) | ((prn->rx[0].preamp & 1) << 3) |
     ((prn->mic.mic_trs & 1) << 4) | ((prn->mic.mic_bias & 1) << 5) |
     ((prn->mic.mic_ptt & 1) << 6);
C2 = (prn->mic.line_in_gain & 0b00011111) | ((prn->puresignal_run & 1) << 6);
C3 = prn->user_dig_out & 0b00001111;
```

Zeus at `ControlFrame.cs:264-280`:

```csharp
c14[0] = 0;   // C1 — reserved on this register
c14[1] = 0;   // C2  (then OR'd with 0x40 if HL2 && PsEnabled)
c14[2] = 0;   // C3
c14[3] = c4;
```

Zeus writes `C1=0` (no preamp, no mic-PTT, no mic-bias) and `C3=0` (no
user_dig_out). But these gaps are present **pre-PS too** and TX worked
pre-PS, so they don't explain the regression. They DO mean the HL2
gateware's preamp/mic state is whatever the radio defaults to — possibly
fine for SSB/two-tone test on the bench, possibly broken for some other
scenario. **Not a blocker for the PA-mute fix; flag for follow-up.**

### M3 — 0x12 (DriveFilter) C2/C3/C4 are zero except for PA enable bit

mi0bot writes additional bits to C2/C3/C4 of the DriveFilter register
beyond `cc[2] |= 0x08` (per the comment at `ControlFrame.cs:200-211`
referring to "PR #119 review notes mi0bot writes C2/C3/C4 and lights
C2[3] for PA enable when pa_enabled && !txband->disablePA"). The exact
bits are not enumerated in the Zeus comment; need mi0bot networkproto1.c
case for register 0x09 (= C0 wire byte 0x12) confirmation.

**Impact on PA mute:** unknown. If mi0bot writes a `pa_enabled` bit somewhere
in DriveFilter that Zeus doesn't, AND that bit is cleared by default in HL2
gateware AND the PS-active path requires it set, that could mute the PA.
**Lower-confidence candidate than M1**; would need spec citation to
escalate. Worth checking if M1 doesn't fix it.

### M4 — 0x1c C3 (`tx_step_attn` duplicate?) is zero in Zeus

`HL2 protocol doc:478-485` and mi0bot networkproto1.c documentation note
that the AdcRouting (0x1c) register has C3 = `prn->adc[0].tx_step_attn`
in some mi0bot revisions — i.e. the same TX step attenuator value is
duplicated into the AdcRouting frame's C3. Zeus's `WriteAdcRoutingPayload`
at `ControlFrame.cs:296-299` writes `C3=0` always, with the comment "Zeus
drives TX attenuation through the dedicated 0x14 path, leave 0 here".

**Impact on PA mute:** if the HL2 gateware reads tx_step_attn from BOTH
0x14 C4 and 0x1c C3 and uses an OR-combined or AND-combined value, Zeus's
`0x14 C4 = 60` plus `0x1c C3 = 0` could produce a different effective PGA
value than mi0bot's `0x14 C4 = 31` plus `0x1c C3 = 31`. **Lower confidence
than M1 but worth verifying with mi0bot-spec.**

### Verdict & single-line fix candidate

**M1 is the only candidate that explains all observed facts:**
- PA silent only when PS armed (puresignal_run=1 enables strict-PS firmware
  mode that reads C4 strictly).
- Pre-PS TX works (legacy/permissive C4 interpretation).
- DDC2/DDC3 zero downstream (no RF means no feedback samples regardless of
  parser layout).
- `MaxTxEnvelope` reads in the `0.22-0.23` range earlier in the session
  came from WDSP's `GetPSMaxTX` which measures the TXA-stage IQ that's
  fed into the wire — that IQ is non-zero (TXA still produces samples
  regardless of PA state). The MUTE happens at the AD9866 TX PGA stage
  on the radio side, AFTER WDSP has done its work.

**Single-line fix candidate** for task #4:

```diff
--- a/Zeus.Protocol1/ControlFrame.cs
+++ b/Zeus.Protocol1/ControlFrame.cs
@@ -239,11 +239,15 @@ private static void WriteAttenuatorPayload(Span<byte> c14, in CcState s)
     int db = s.Atten.ClampedDb;
-    byte c4 = s.Board == HpsdrBoardKind.HermesLite2
-        ? (byte)(0x40 | Math.Clamp(60 - db, 0, 60))
-        : (byte)(0x20 | (db & 0x1F));
+    byte c4;
+    if (s.Board == HpsdrBoardKind.HermesLite2)
+    {
+        // Per docs/references/protocol-1/hermes-lite2-protocol.md:381-382,
+        // the same C4 byte is read as `tx_step_attn` during XmitBit and
+        // `rx_step_attn` otherwise. Switch encoding accordingly.
+        // tx_step_attn default = 31 (mi0bot console.cs:19610 `31 - txatt`
+        // with txatt=0). rx_step_attn default = 60 - db (Zeus existing).
+        int rawAttn = s.Mox
+            ? (s.Hl2TxAttnDb != int.MinValue ? 31 - s.Hl2TxAttnDb : 31)
+            : 60 - db;
+        c4 = (byte)(0x40 | Math.Clamp(rawAttn, 0, 59));
+    }
+    else
+    {
+        c4 = (byte)(0x20 | (db & 0x1F));
+    }
-
-    if (s.Board == HpsdrBoardKind.HermesLite2
-        && s.Mox
-        && s.Hl2TxAttnDb != int.MinValue)
-    {
-        c4 = (byte)(Math.Clamp(31 - s.Hl2TxAttnDb, 0, 60) | 0x40);
-    }
```

(Also caps clamp at 59 not 60 — the valid wire range is 0..59 per
mi0bot's `udTXStepAttData` `Minimum=-28, Maximum=+31` mapped via
`31 - txatt`. The existing 0..60 clamp was a small off-by-one.)

**Bench-test before/after:**
1. With current code, drive 21 %, MOX on, PS off → expect non-zero power.
2. With current code, drive 21 %, MOX on, PS on → expect zero power
   (the bug). Capture `journalctl` for `wdsp.psState` and `p1.ps.fb`.
3. Apply the patch. Re-test step 2 → expect non-zero power AND non-zero
   DDC2 (post-PA RX feedback) AND non-zero DDC3 (TX-DAC loopback after
   PGA). All three should come back together — they share the same
   AD9866 PGA gate that the wrong wire byte was muting.

**Order of operations recommended (post-mi0bot-spec correction):**
1. **Fix M1 first** (this section). Single-line patch in
   `WriteAttenuatorPayload`. Expect: PA outputs power, AND DDC2/DDC3
   feedback samples become non-zero, AND `wdsp.psState` advances out of
   COLLECT toward CALC, AND `info[5] CalibrationAttempts` starts ticking.
2. **Re-evaluate D1** (auto-cal hw_peak). With real samples flowing, the
   bin-starvation analysis becomes testable. If `info[5]` ticks but
   `info[14] correcting=1` never asserts, D1's lock-on-env hypothesis is
   live and needs the `if (board == HermesLite2) return;` guard at the
   top of `TickAutoCalHwPeak`. If `info[14]=1` asserts and corrections
   take hold, leave D1 alone.
3. **D6 / D7 / D8 are non-blocking** — clean-disarm polish, not
   regression cause. Address after PS is bench-confirmed working.

**Withdrawn from earlier ranking:**
- D3 (Predistortion register never sent) — mi0bot doesn't write 0x2b
  either, per mi0bot-spec confirmation. Zeus's dead encoder is harmless
  AND mi0bot-aligned by accident.
- V1 (switch to 2-DDC parser) — mi0bot's HL2 default IS 4-DDC, Zeus
  matches.
- D2 (HighPriority flush + UpdateDDCs reprogram) — round-robin
  propagation already places all bytes on the wire within ~42 ms;
  M1 fix is sufficient on its own.
- **M1 (0x14 C4 wire-encoding) — RETRACTED 2026-05-03.** Bench-tester's
  19-round wire dump showed C4 of 0x14 did NOT change between PS-off
  and PS-on captures (only C2 changed, the puresignal_run bit). Zeus
  HAS been writing the "wrong" 0x7C byte during MOX for both PS-off
  and PS-on, but the operator confirmed PA worked in PS-off. So either
  the HL2 firmware tolerates the byte even in strict-PS mode, or the
  AD9866 PGA isn't actually muted by it. M1 is no longer the root
  cause — flagged for cleanup but not the regression. **The actual
  regression is M5 (next section).**

### M5 — DriveFilter (0x12) C1 drops on PS-arm; mi0bot's drive_level is unconditional *(NEW SMOKING GUN)*

> **Bench evidence:** Wire-dump diff between PS-off MOX burst and PS-on MOX
> burst shows `DriveFilter (0x12) C1: 0xF0 → 0x90` (`bits 5 and 6 cleared`).
> mi0bot-spec's canonical PS-on table at line 1761 of this doc shows
> Round 10 (= DriveFilter `0x12`) C1 = `tx[0].drive_level` and explicitly
> notes (lines 1180-1181): "It does NOT (in mi0bot) cause: A change to TX
> drive level (case 10 C1 = `tx[0].drive_level`, **untouched by PS path**)."

#### M5.A — What mi0bot writes for round 10 C1

mi0bot canonical, per `networkproto1.c:1062-1075` (cited in the §N table at
line 1761): **`C1 = tx[0].drive_level`** — the raw 8-bit drive_level byte.
HL2 protocol doc at `docs/references/protocol-1/hermes-lite2-protocol.md:51`
clarifies that **only bits [31:28] (= bits 4-7 of C1) are honoured by the
gateware** — the bottom nibble is silently discarded.

So mi0bot's C1 = `drive_level`, where `drive_level` is set ONCE by the
operator's drive slider and **does not change when PS is toggled**.

mi0bot-spec's table assumes "TUN full" → `drive_level = 0xFF` (all bits
set, max nibble F). For partial drive % the byte is correspondingly
lower. The salient point: **the same byte goes out in PS-off and PS-on**.

#### M5.B — What bits 5 and 6 of C1 mean per mi0bot

Per the protocol doc at `:51-58`, register `0x09` (= wire byte `0x12`)
fields are:

| 0x09 bit | C1 bit | Meaning                                              |
|----------|--------|------------------------------------------------------|
| 31       | 7      | drive_level MSB                                      |
| 30       | 6      | drive_level (bit 2 of top nibble)                    |
| 29       | 5      | drive_level (bit 1 of top nibble)                    |
| 28       | 4      | drive_level LSB-of-used-nibble                       |
| 27..24   | 3..0   | drive_level low nibble (**ignored by gateware**)     |

**Bits 5 and 6 of C1 are NOT separate control bits.** They are the
middle bits of the drive_level top nibble (the 4 bits the gateware
honours). `0xF0` (top nibble F = 15) is max drive; `0x90` (top nibble
9) is 9/15 ≈ 60 % drive. The team-lead's "bits 5 and 6 cleared" framing
is a bit-pattern coincidence; semantically the change is a **numeric drive
reduction from 100 % effective to 60 % effective**.

#### M5.C — Where does the change come from in Zeus?

**Answer: searched, no PS-aware drive code path exists.**

Search results (greps `psEnabled|state.Ps|args.Ps|PsEnabled` across
`Zeus.Server.Hosting/*.cs`):

- `RadioService.cs` — only PS-related path is `SetPs` mutator (writes
  `s.PsEnabled`, `s.PsAuto`, `s.PsSingle`); does **not** call
  `RecomputePaAndPush`.
- `RadioDriveProfile.cs` — `HermesLite2DriveProfile.EncodeDriveByte`
  takes `drivePct, paGainDb, maxWatts` arguments only; **NO PsEnabled
  branch**.
- `DspPipelineService.cs:443-526` — PS apply ladder (HwPeak, advanced,
  mode, master arm, feedback source); **NO call to any drive-related
  setter**.
- `PsAutoAttenuateService.cs` — calls `engine.SetPsControl`,
  `GetPsStageMeters`, `p1.SetHl2TxStepAttenuationDb`, `p2.SetTxAttenuationDb`,
  `engine.ResetPs`, `_radio.SetPsAdvanced`. **None touch `_drivePct`,
  `_tunePct`, `_driveByteOverride`, or call `RecomputePaAndPush`**.
- `TxService.cs` — only calls `_radio.NotifyTunActive(...)` on TUN edges;
  not on PS-arm.
- `tx-store.ts` (frontend) — `setPs` REST call does not bundle a drive
  change.

`RecomputePaAndPush` is invoked from:
- `_paStore.Changed` (PA settings store mutation)
- `_preferredRadioStore.Changed`
- `RadioService.cs:375` (ConnectAsync)
- `RadioService.cs:431` (post-band-change)
- `SetDrive` (operator slider)
- `SetTuneDrive` (operator TUN-drive slider)
- `NotifyTunActive` (TUN flips → switches `_tunePct` ↔ `_drivePct`)
- `ReplayPaSnapshot` (manual replay)
- `RadioService.cs:1274` (band-change consequence)

**None of these are wired to PS-arm.** So the drive-byte change between
the two bench bursts MUST come from one of:

1. **Operator changed drive % between bursts** (slider movement,
   uncontrolled experiment).
2. **Operator pressed TUN for one burst and MOX for the other.**
   `NotifyTunActive(true)` swaps `_drivePct` for `_tunePct` (default
   `_tunePct = 10`) — but `_tunePct=10` × bandPct=100 → byte ≈ 0x20,
   which doesn't match either bench reading. So this only fits if the
   operator also pre-set `_tunePct` to a non-default value.
3. **Hidden code path I haven't found.** Possible but greps came up
   empty across `Zeus.Server.Hosting`, `Zeus.Server`, `Zeus.Protocol1`,
   and `zeus-web/src`.

#### M5.D — Verdict and clarifying question for bench-tester

The wire-side change is real and matches the operator's "no power when PS
arms" symptom (drive byte at top nibble 9 instead of F = ~6 dB less RF
drive into the PA). But Zeus's source code does NOT contain any
PS-conditional drive logic.

**Before recommending a fix, bench-tester should clarify:**

1. **Did you touch the drive slider between the PS-off and PS-on
   captures?** If yes, then the wire change is operator-induced, not a
   Zeus bug, and the "no power" symptom is pure operator confusion. Repeat
   the bench with strict drive-slider invariance.
2. **Did you use TUN or MOX for each capture?** TUN switches Zeus to
   `_tunePct` via `NotifyTunActive`; MOX uses `_drivePct`. If one was TUN
   and one was MOX, the byte change is from this expected switch, not a
   PS-arm bug.
3. **Does `pa.recompute` log line appear in `journalctl -u zeus-server`
   between the two captures?** It logs every `RecomputePaAndPush` call
   with `pct=… band=… gainDb=… profile=… -> byte=…`. If a call fires
   between bursts, the log line will show what triggered it AND what
   inputs produced the new byte. If no `pa.recompute` line appears
   between bursts but the wire byte still changed, that's evidence of a
   bypass path (would need deeper investigation).

#### M5.E — Companion finding: Round 10 C2 — Zeus emits `0x08` while mi0bot emits `0x48` (missing unconditional `0x40` base bit)

Per the team-lead's secondary question. Zeus's `WriteCcBytes` for
`CcRegister.DriveFilter` (`Zeus.Protocol1/ControlFrame.cs:197-211`):

```csharp
case CcRegister.DriveFilter:
    cc[1] = state.DriveLevel;
    cc[2] = 0;
    cc[3] = 0;
    cc[4] = 0;
    if (state.Board == HpsdrBoardKind.HermesLite2 && state.Mox)
    {
        cc[2] |= 0x08;          // ← Apollo Tuner repurposed PA-enable bit
    }
    break;
```

So Zeus's C2 = **`0x08`** during HL2+MOX, regardless of PS state.

mi0bot canonical (per the §N table line 1761, `networkproto1.c:1062-1075`):
**C2 = `0x48`** = `0x40 (unconditional base) | 0x08 (ApolloTuner=PA enable)`.

**Zeus is missing the unconditional `0x40` bit on every PS-active and
PS-inactive MOX burst.** This is a STATIC divergence (constant in both
bench captures), not a regression. Per protocol doc `:51-58`, bit 6 of
C2 = bit 22 of register 0x09 = "Alex manual mode (Not Yet Implemented)" —
which on HL2 may be a no-op (Alex isn't present). mi0bot writes it
unconditionally as a literal in the C source; the HL2 gateware likely
ignores it.

**Verdict:** flag for cleanup — Zeus should match mi0bot's literal even
if the HL2 ignores the bit, both for protocol cleanliness and to avoid
breaking when an Alex-board HL2 mod is wired up. **NOT the regression
cause.** Bench-tester's note that C2 doesn't change between PS-off and
PS-on is consistent with this analysis.

#### M5.F — Recommended task #4 sequencing (revised again)

1. **Bench-tester clarifies M5.D first** — was the drive slider touched,
   was TUN vs MOX consistent, and what does the `pa.recompute` log show
   between bursts? Without this, fixing without understanding risks
   masking the real issue.
2. **If operator-induced:** no Zeus fix needed for the immediate "no
   power" symptom. Re-bench with strict invariance and proceed to
   evaluate D1 (auto-cal hw_peak) once feedback samples actually flow.
3. **If a hidden Zeus path is reducing drive on PS-arm:** instrument
   with a one-line log at the top of `RecomputePaAndPush` and re-run.
   The log already exists at `RadioService.cs:925-927` (`pa.recompute`);
   it just needs to be observed.
4. **Companion clean-up (low priority):** add `cc[2] = 0x40` base in
   `WriteCcBytes DriveFilter` HL2 path so it matches mi0bot literally.
   Cite `networkproto1.c:1062-1075`.

---

## TX-IQ payload audit (post-v3-wire-capture)

> **Update 2026-05-03.** Bench-tester's controlled v3 capture (single
> backend lifetime, identical drive sliders, multi-shot dump) shows Zeus's
> outbound C&C bytes match mi0bot's canonical PS-on MOX table EXACTLY. The
> only delta on PS-arm is `Config C4: 0x04 → 0x1C` (nddc 1→4) which mi0bot
> ALSO emits. Yet PA goes 5.515 W → 15 µW on PS-arm. **The C&C bytes are
> NOT the regression cause.** That points the investigation at the TX-IQ
> payload itself, NOT the C&C round-robin. M5 is also retracted (no drive
> recompute happens on PS-arm under controlled invariance). Investigation
> below traces the TX-IQ source path end-to-end and addresses each of
> team-lead's four hypotheses.

### Q1 — Is the TX-IQ payload zeroed when `psEnabled=true`?

**Trace of `TxLoopAsync` → wire IQ-pack** (`Zeus.Protocol1`):

1. **`Protocol1Client.TxLoopAsync` `:826-881`.** Loops on `_txSignal.WaitAsync`
   (one tick per RX-paced TX slot). Calls
   `ControlFrame.BuildDataPacket(buf, sendSeq++, first, second, in state, _txIqSource)`
   at `:900` with `_txIqSource` = `PsTapIqSource(inner, this)` from the
   ctor at `:133`. **No PS gate here — `_psEnabled` is read by
   `SnapshotState` for the C&C side ONLY, not the IQ payload.**

2. **`ControlFrame.BuildDataPacket` `:364-386`.** Calls `WriteUsbFrame` twice
   (one per USB frame within the 1032-byte Metis packet).

3. **`ControlFrame.WriteUsbFrame` `:404-484`.** Writes the 3-byte sync,
   5-byte CC bytes, then iterates 63 IQ-sample slots. The IQ payload gate at
   `:440-444`:

   ```csharp
   if (source is null || !state.Mox || state.Board != HpsdrBoardKind.HermesLite2)
   {
       return;  // payload stays zero
   }
   if (state.DriveLevel == 0) return;
   ```

   **No PS gate.** As long as MOX is on, board is HL2, and DriveLevel ≠ 0,
   IQ samples are pulled from `source.Next(amplitude)` and packed.

4. **`PsTapIqSource.Next` `:1051-1066`.** Pure passthrough wrapping the inner
   source (`TxIqRing` in production, `TestToneGenerator` in unit tests).
   Calls `_inner.Next(amplitude)`, records the sample to
   `_psTxRingI/Q` for PS-feedback purposes, returns the sample
   **unchanged**. **No PS gate; no zeroing logic.** The "PS-tap" name
   refers to the side-effect of recording, not any modification.

5. **`TxIqRing.Read` (called from `Next`).** Reads from a 16384-pair FIFO
   filled by `TxAudioIngest.ProcessTxBlock` (mic-driven path) or
   `TxTuneDriver` (TUN/TwoTone path). Returns `(0, 0)` when the ring is
   empty (= silent IQ on the wire, which is the right thing to do when
   the producer hasn't yet caught up). **No PS gate.**

**Diagnostic:** the existing `p1.tx.rate` log at
`Protocol1Client.cs:919-922` (1 Hz) prints the actual wire IQ bytes:

```
p1.tx.rate pkts=N in T ms = R pkt/s (target 381) | wire: peak=P/32767 mean=M firstI=I firstQ=Q drv=D
```

The `peak=` and `mean=` values are taken from `ControlFrame.LastPeakAbs`
and `LastMeanAbs` — written by every `WriteUsbFrame` call.
**This log already discriminates Q1.** Bench-tester need only grep for
`p1.tx.rate` during PS-on MOX:

| `p1.tx.rate peak=…` reading       | Diagnosis                                                  |
|------------------------------------|------------------------------------------------------------|
| > 0 in BOTH PS-off and PS-on MOX  | Wire IQ is non-zero; PA mute is happening on the radio side, not in Zeus → look at HL2 firmware behaviour or the `0x40` base bit gap (M5.E) or some other byte the v3 capture didn't include |
| > 0 in PS-off, **= 0 in PS-on**   | Wire IQ silenced on PS-arm. **Zeros are arriving at the EP2 packer from the ring.** Step to Q2/Q3 below to find which upstream layer is producing zeros. |

**Verdict on Q1:** No code path in `Zeus.Protocol1` zeros the IQ payload
when `_psEnabled=true`. If the wire is silent on PS-on, the silence
originates upstream of `TxIqRing`.

### Q2 — Is `_psTxRingI/Q` competing with the EP2 producer?

`_psTxRingI/Q` (`Protocol1Client.cs:191-212`) is a **2048-sample ring with
ONE producer (`PsTapIqSource.Next` via `RecordPsTxSample`) and ZERO
consumers**. It exists purely to provide a "TX-IQ history" snapshot for
future pscc-TX-buffer use; the current `HandlePs4DdcPacket` path uses
DDC3 (TX-DAC loopback) instead, so the ring is filled but never read.

**No competition with `TxIqRing`** (the wire-bound IQ source). They're
separate buffers. `_psTxRingI/Q` is wired into the PS feedback path only;
`TxIqRing` is wired into the EP2 packer path. The producer for both is
the WDSP TXA chain via `ProcessTxBlock`, but the buffers are independent.

**`StartPsFeedbackPumpP1` (`DspPipelineService.cs:753-775`)** consumes
**`PsFeedbackFrames`** (the parsed DDC2/DDC3 frames from
`HandlePs4DdcPacket`), NOT `TxIqRing`. It calls
`engine.FeedPsFeedbackBlock(...)` which writes to WDSP `psccF` — a
separate path entirely. **Zero contention with the wire-IQ pump.**

**Verdict on Q2:** No. `_psTxRingI/Q` and `StartPsFeedbackPumpP1` do not
starve `TxIqRing`.

### Q3 — Does the TX siphon / PS Monitor divert TX-IQ?

Two independent answers:

**Q3a — TX siphon (`5caa699`):** `WdspDspEngine.ProcessTxBlock`
`:2412-2427` calls `TXAGetaSipF1(txa, ref sipBuf[0], sipSize)` to read
the WDSP **xsiphon** tap (xsiphon position in xtxa, BEFORE iqc per
`siphon.c, TXA.c:586`) and feeds it to the TX analyzer for the
panadapter. **`TXAGetaSipF1` is a read-only pull from a separate WDSP
tap point — it does NOT divert samples from the `iout/qout` output
buffer that the caller writes to `iqInterleaved`.** No PS gate, no
sample diversion.

**Q3b — PS Monitor auto-enable on PS arm (`fef3031`):** the
`PsToggleButton` arm handler explicitly **SKIPS PS Monitor auto-enable
on HL2** at line 24-27 of the diff:

```javascript
const PS_MONITOR_UNSUPPORTED = new Set(['HermesLite2']);
...
if (next && !psMonitorEnabled && !PS_MONITOR_UNSUPPORTED.has(connectedBoard)) {
    setPsMonitorLocal(true);
    setPsMonitor(true)...
}
```

Same `HermesLite2` gate as the Settings panel. PS Monitor stays off on
HL2 PS-arm. **And even if it were on:** `_psMonitorEnabled` only routes
which analyzer the panadapter reads from
(`DspPipelineService.cs:1118-1142`); it does NOT modify the wire IQ
path.

**Verdict on Q3:** Neither siphon nor PS Monitor diverts TX-IQ. Both
Q3a and Q3b are eliminated.

### Q4 — Does WDSP's `SetPSMox(channel, 1)` swallow TX-IQ?

`WdspDspEngine.SetMox(true)` at `:1378-1393`:

```csharp
_moxOn = true;
rxaPrior = NativeMethods.SetChannelState(rxaId, 0, 1);
if (!_txaRunning)
{
    txaPrior = NativeMethods.SetChannelState(txaId, 1, 0);
    _txaRunning = true;
}
NativeMethods.SetPSMox(txaId, 1);
```

`SetPSMox` is called UNCONDITIONALLY on every MOX-on edge — even when
`_psEnabled = false`. The comment at `:1389-1391` explicitly notes:
"Safe to call even when PS isn't armed — it just toggles a flag inside
calcc."

**Therefore `SetPSMox(1)` fires identically in PS-off MOX and PS-on
MOX bursts**, so it cannot account for the regression. Per spec §I,
`SetPSMox(1)` only sets `solidmox` inside calcc, which gates the
`LMOXDELAY → LSETUP → LCOLLECT` transitions. **It does not modify the
TXA modulator output (`iout/qout`); fexchange2 still produces IQ
through the iqc stage regardless of solidmox state.**

**Verdict on Q4:** No. `SetPSMox(1)` is PS-state-agnostic and doesn't
touch the modulator output.

### Q5 — What CAN silence TX-IQ on PS-arm?

After eliminating Q1–Q4, the remaining suspect is the **WDSP iqc stage
itself**. When `SetPSRunCal(id, 1)` and `SetPSControl(id, 1, 0, 1, 0)`
fire (engine.SetPsEnabled(true)):

- calcc transitions LRESET → LWAIT (per spec §I).
- iqc switches from "bypass" to "apply stored corrections".
- **The stored corrections at this point are whatever calcc last wrote
  — which on a fresh PS arm is THE IDENTITY MATRIX.** Per WDSP
  documentation and the Zeus comment at `WdspDspEngine.cs:1235-1248`
  "PureSignal seed" block, iqc starts at identity and stays there until
  calcc completes a fit.

**If iqc is somehow returning ZERO instead of IDENTITY on a fresh
PS arm**, that would silence the wire while leaving every other path
visible-correct. This is the **only remaining hypothesis I can construct
without WDSP source.**

Possible sub-causes inside iqc:
- iqc state cleared by `SetPSControl(reset=1)` (the reset bit Zeus sends)
  but NOT properly re-armed to identity afterwards.
- iqc requires a `SetPSStabilize(1)` to apply identity rather than zero
  output. The Zeus seed at `:1259` sends `SetPSStabilize(id, 0)`. mi0bot
  may send `1` here (not yet verified — needs mi0bot-spec citation).
- `SetPSPinMode(1)` and `SetPSMapMode(1)` may need to be re-issued after
  `SetPSRunCal(1)` to put iqc in pass-through mode. Zeus issues them once
  at OpenTxChannel and never again.

### Q6 — `tx.peaks` log already discriminates iqc-zeroing vs ring-zero

`TxAudioIngest.cs:286-294` emits `tx.peaks blocks=N mic=M iq=I` every
1 second, where `iq` is the peak of the buffer that came back from
`engine.ProcessTxBlock` (= the buffer about to be written to
`TxIqRing`). Combined with `p1.tx.rate peak=` (the peak read OUT of the
ring at the EP2 packer):

| `tx.peaks iq=` (PS-on) | `p1.tx.rate peak=` (PS-on) | Diagnosis                                           |
|------------------------|----------------------------|------------------------------------------------------|
| > 0                    | > 0                        | Wire IQ fine; PA mute is on the radio side          |
| > 0                    | = 0                        | Ring is being drained/cleared somewhere between TxAudioIngest and TxLoopAsync (NOT found in code review — would be a serious surprise) |
| **= 0**                | **= 0**                    | **fexchange2 is producing zeros — Q5 confirmed.** WDSP iqc is silencing TX-IQ output on PS arm. Investigate `SetPSStabilize`, `SetPSPinMode`, `SetPSMapMode`, and the WDSP iqc state-machine reset behaviour. |

### Q7 — Note on `tx.meters.diag fwdSlot/s=0`

Bench-tester's "HL2 forward-power ADC isn't being reported" observation
is consistent with EITHER:

- The PA being silent (no forward power → ADC reads zero → telemetry
  slot reports zero). This is a SYMPTOM of the PA-mute, not a separate
  bug.
- A separate telemetry-decode regression. Cross-reference
  `Protocol1Client.cs` AIN3/AIN4 decode paths if the value stays zero
  even after the PA-mute is fixed.

If `p1.tx.rate peak=0` AND `tx.peaks iq=0` AND `fwdSlot/s=0` all line up
on PS-arm, all three are the same root cause: WDSP iqc is silencing the
TX-IQ output on PS arm, the radio sees silence, the PA outputs no power,
the forward-power ADC reads zero.

### Q8 — Verdict and one-line investigation step for the fixer

**Cannot recommend a single-line code fix yet.** The C&C is correct, the
TX-IQ source path has no PS gates, and PS Monitor / siphon / SetPSMox
are eliminated. The most likely culprit is **WDSP iqc returning zero
output after `SetPSRunCal(1)` + `SetPSControl(reset=1, automode=1)`** —
which would require either reading WDSP iqc.c source or experimenting
with the seed parameters (`SetPSStabilize`, `SetPSPinMode`,
`SetPSMapMode`).

**One-line investigation request for the fixer (BEFORE any code change):**

Bench-tester captures `journalctl -u zeus-server` during a 5-second PS-on
MOX burst and reports the **`tx.peaks iq=…`** value alongside the
**`p1.tx.rate peak=…`** value. The combination disambiguates per the Q6
table:

- **If both show iq/peak > 0 on PS-on**, the wire IQ is correct and the
  mute is firmware-side. Move investigation to mi0bot's
  `SetPSStabilize`/`SetPSPinMode`/`SetPSMapMode` parameters and the
  protocol doc's `0x40` base bit (M5.E) — there's some HL2 firmware
  expectation Zeus is missing that only matters when `puresignal_run=1`.
- **If both show 0 on PS-on but > 0 on PS-off**, the bug is inside WDSP
  fexchange2 (iqc zeroing). Compare the Zeus seed parameters
  (`WdspDspEngine.cs:1249-1266`) against mi0bot's per `cmaster.cs`
  initialization. Specifically check whether `SetPSStabilize(0)` should
  be `(1)` and whether `SetPSPinMode(1)` / `SetPSMapMode(1)` need
  re-issuing after `SetPSRunCal(1)`.

Both logs are already in the code; no instrumentation needed.

---

## iqc start-path audit

> **Update 2026-05-03.** mi0bot-spec hypothesised that `iqc->run=1` with
> zero coefficients zeros the TX buffer (`PRE0 = ym*(I*yc - Q*ys) =
> 0*(...) = 0`). Per `native/wdsp/iqc.c:118-213` this mechanism is REAL.
> Audit task: find every Zeus caller of `SetTXAiqcStart` (or anything
> that flips `iqc->run`), verify each is gated on calc-completion (`scOK=1`),
> NOT on PS arm. **Result: Zeus has ZERO callers — direct or transitive
> — of any iqc setter.** Plus full WDSP-source trace of when iqc->run can
> flip on a fresh PS arm.

### I.1 — `xiqc` mute mechanism (mi0bot-spec hypothesis confirmed at the source level)

`native/wdsp/iqc.c:118-213` (`xiqc`):

```c
void xiqc (IQC a) {
  if (_InterlockedAnd(&a->run, 1)) {              // ← THE GATE
    // ... predistortion math:
    //   PRE0 = ym * (I * yc - Q * ys)
    //   PRE1 = ym * (I * ys + Q * yc)
    // with state-machine for BEGIN/RUN/SWAP/END/DONE
  } else if (a->out != a->in) {
    memcpy (a->out, a->in, a->size * sizeof (complex));
  }
  // when run=0 AND out==in, this function is a NO-OP — IQ samples
  // flow through midbuff unchanged.
}
```

TXA wires `out == in` (in-place on `txa[ch].midbuff` per `TXA.c:397-398`),
so when `iqc->run=0`, xiqc is a true no-op and TX samples pass
through unchanged. **iqc cannot mute the TX path while `run=0`.**

When `iqc->run=1` AND coefficients are malloc0-fresh zeros, the math
collapses: `ym=yc=ys=0` → `PRE0=PRE1=0`. The state machine BEGIN ramps
this in over `ntup = 0.005 sec * dsp_rate` samples (~960 samples at
192 kHz, ~5 ms), then enters RUN where output stays at zero forever.
**Symptom would be:** PA produces signal for ~5 ms after PS arm, then
fades to zero. Bench's "5.515 W → 15 µW" is consistent with this fade
+ small radio noise floor.

### I.2 — `iqc->run` initial value: `0` (verified at WDSP source)

`native/wdsp/TXA.c:394-402`:

```c
txa[channel].iqc.p0 = txa[channel].iqc.p1 = create_iqc (
                        0,    // run
                        ch[channel].dsp_size,
                        txa[channel].midbuff,
                        txa[channel].midbuff,    // out == in (in-place)
                        ...
);
```

`create_iqc(int run, ...)` at `iqc.c:88-100` stores `a->run = run` literally.
**On TXA-open, `iqc->run = 0`.** Identity passthrough by default.

### I.3 — Three (and only three) WDSP paths that flip `iqc->run=1`

Verified by `grep -n "iqc.p1->run" native/wdsp/*.c`:

| File:line          | Function                  | Effect                                                             |
|--------------------|---------------------------|--------------------------------------------------------------------|
| `iqc.c:289`        | `SetTXAiqcStart`           | `run ← 1`                                                          |
| `iqc.c:305`        | `SetTXAiqcEnd`             | `run ← 0`                                                          |

`SetTXAiqcStart` itself is called from EXACTLY two places in WDSP:

| File:line          | Caller                    | Gate                                                               |
|--------------------|---------------------------|--------------------------------------------------------------------|
| `calcc.c:511`      | `doPSCalcCorrection` (worker thread)         | After `if (a->scOK)` — calc completion                              |
| `calcc.c:626`      | `PSRestoreCorrection` (worker thread)        | After successful file load (`if (!error)`)                          |

Both are gated. `doPSCalcCorrection` waits on `Sem_CalcCorr` which is
released ONLY at `calcc.c:837` inside the `LCALC` state of the calcc
state machine. To reach LCALC, the state machine must traverse:
`LRESET → LWAIT → LMOXDELAY → LSETUP → LCOLLECT → MOXCHECK → LCALC`.
LCOLLECT advances to MOXCHECK only when `full_ints == ints` (every bin
filled to `spi` samples), which requires non-zero TX envelope. **If PA
is muted from the very start of MOX, env=0 → bins never fill → LCOLLECT
never advances → no Sem_CalcCorr release → no doPSCalcCorrection wakeup
→ no `SetTXAiqcStart` call → iqc->run stays 0.**

This is the chicken-and-egg: muted PA prevents calc completion which
prevents iqc activation. So **iqc->run cannot transition to 1 on the
FIRST PS arm of a fresh engine if the PA is already silent**. iqc
self-mute hypothesis is INCOMPATIBLE with first-arm symptom.

### I.4 — Zeus has ZERO direct or transitive callers of `SetTXAiqcStart`

Greps performed across the entire repo:

```
grep -rn "SetTXAiqcStart\|SetTXAiqcRun\|EnableIqc\|IqcStart\|SetPSIqcStart" \
  --include="*.cs" --include="*.h" Zeus.Dsp Zeus.Server.Hosting Zeus.Protocol1
→ ZERO matches in any C# file
→ Only matches: native/wdsp/iqc.h:83 (declaration),
                 native/wdsp/wdsp.h:651 (declaration),
                 native/wdsp/iqc.c:278 (definition)
```

**Zeus's `Zeus.Dsp/Wdsp/NativeMethods.cs` does NOT P/Invoke `SetTXAiqcStart`.**
It is therefore IMPOSSIBLE for Zeus to call it directly. The only path
to `iqc->run=1` from Zeus's actions is the legitimate calcc-completion
chain in I.3 — which requires the PA to ALREADY be producing signal.

### I.5 — Verification of each Zeus path mi0bot-spec asked about

| Zeus path                                           | iqc-related? | Verdict |
|-----------------------------------------------------|--------------|---------|
| `WdspDspEngine.SetPsEnabled(true)` `:1857-1913`     | Calls `SetPSRunCal(1)`, `SetPSControl(1, mancal, automode, 0)`, `OpenPsFeedbackAnalyzer(id)`. **None of these touch `iqc->run`.** `SetPSRunCal` only sets `a->runcal` (calcc.c:960-966). `SetPSControl` only sets `ctrl.{reset,mancal,automode,turnon}` (calcc.c:1020-1029). | ✓ Clean — no iqc start |
| `WdspDspEngine.SetPsEnabled(false)` `:1894-1912`    | Pushes 7× zero-psccF, `SetPSRunCal(0)`, `SetPSControl(1,0,0,0)`. Indirectly may release `Sem_TurnOff` if `ctrl.running=1` when calcc next ticks LRESET (calcc.c:670-673), which would call `SetTXAiqcEnd` → `iqc->run=0`. | ✓ Cleanup path; only relevant if a prior arm had completed calc |
| `DspPipelineService.OnRadioStateChanged` PS apply ladder `:443-526` | HwPeak / advanced / control / arm / feedback-source. **No iqc setter.** | ✓ Clean |
| `PsAutoAttenuateService.Tick1Hl2`                    | Calls `engine.SetPsControl(false, false)` → `SetPSControl(1,0,0,0)`; `engine.SetPsControl(saved)` → `SetPSControl(0, mancal, automode, 0)`; `p1.SetHl2TxStepAttenuationDb(...)`. **No iqc setter.** | ✓ Clean |
| `PsAutoAttenuateService.TickAutoCalHwPeak` (commits `b87e70b`, `ec8fc74`) | Calls `_radio.SetPsAdvanced(new PsAdvancedSetRequest(HwPeak: target))` which goes through `Mutate → StateChanged → DspPipelineService.OnRadioStateChanged → engine.SetPsAdvanced` (`WdspDspEngine.cs:1799-1855`). SetPsAdvanced calls `SetPSPtol`, `SetPSMoxDelay`, `SetPSLoopDelay`, `SetPSTXDelay`, `SetPSHWPeak`, `SetPSIntsAndSpi`. **No iqc setter.** | ✓ Clean |
| `WdspDspEngine.ResetPs` `:2164-2187`                 | Calls `SetPSControl(1,0,0,0)` then `SetPSControl(0, mancal, automode, 0)`. **No iqc setter.** | ✓ Clean |
| `WdspDspEngine.RestorePsCorrection` `:2203-2217`     | Calls `PSRestoreCorr(id, path)` which can transitively reach `SetTXAiqcStart` at calcc.c:626 IF the file loads successfully. **Only fires from `POST /api/tx/ps/restore`** — operator action, not auto. | ⚠ Operator-triggered; not implicated unless someone hits the endpoint during PS arm |
| `WdspDspEngine.OpenTxChannel` PS seed `:1235-1270`   | Issues `SetPSFeedbackRate, SetPSPtol, SetPSPinMode, SetPSMapMode, SetPSStabilize, SetPSIntsAndSpi, SetPSMoxDelay, SetPSLoopDelay, SetPSTXDelay, SetPSHWPeak, SetPSControl(1,0,0,0)`. **No iqc setter.** Per I.2, `iqc->run` was just initialized to 0 by `create_iqc(0, ...)` inside `create_txa()`. | ✓ Clean |

**Result: ZERO Zeus paths flip `iqc->run`. None of mi0bot-spec's
suspected commits introduced an iqc setter.** The hypothesised
"host-side iqc-start beats calc-completion" race CANNOT occur in Zeus
because Zeus has no host-side iqc start.

### I.6 — So what's actually muting the PA?

Three candidate hypotheses survive after the iqc audit:

**Hypothesis A — Stale `iqc->run=1` across PS arm/disarm cycles
within a single engine lifetime.** If a prior PS session DID complete
a calc successfully (`iqc->run=1`, `ctrl.running=1`), and the disarm
sequence didn't transition calcc into LRESET cleanly (because pscc
isn't called between SetPSControl(reset=1) and the next arm),
`Sem_TurnOff` is never released, `SetTXAiqcEnd` never fires, and
`iqc->run` stays at 1. The next PS arm with stale-1 iqc→run + freshly-
zeroed coefficients (because calcc was reset) would mute. **Bench-tester
should report whether the symptom occurs on the FIRST PS arm of a
fresh server start, or only on subsequent arms.** If first-arm only
on a fresh start, this hypothesis is ruled out.

**Hypothesis B — Some non-iqc WDSP TXA stage is being affected by PS
arm that I haven't traced.** Candidates: ALC, Leveler, CFC, AMSQ,
PostGen — but none have any documented PS interaction. The seed at
OpenTxChannel sets these all to fixed values; none are touched on PS
arm.

**Hypothesis C — `SetPSRunCal(1)` itself triggers some side effect
inside calcc.c that affects the TX path, OR pscc(...) when called with
`runcal=1` does something to the in-band TX buffer.** Looking at
`pscc` at `calcc.c:646`: it takes separate `tx, rx` double pointers
(the FeedPsFeedbackBlock arguments). It does NOT modify TXA's
`midbuff`. So pscc cannot mute the TX path.

### I.7 — Recommended next investigation step

**Request to bench-tester (one observation, no code change):**

Repro the bench symptom under each of these conditions and record
which one(s) reproduce 5.5 W → 15 µW:

1. **Fresh server start** → Connect HL2 → MOX (no PS) → confirm 5.5 W →
   un-MOX → arm PS → MOX → measure power.
2. **Same session as #1, after the PS-on MOX in #1** → un-MOX →
   disarm PS → arm PS → MOX → measure power.
3. Repeat #1 but with **server restart between PS-off MOX and PS-on
   MOX** → measure.

If only #2 mutes (subsequent-arm mute, fresh-arm fine), Hypothesis A
is confirmed and the fix is in `WdspDspEngine.SetPsEnabled(false)` —
need to ensure pscc gets called between `SetPSControl(reset=1)` and
the next arm so `Sem_TurnOff` releases and iqc->run clears. Could be
as simple as: after `SetPSRunCal(0)` and `SetPSControl(1,0,0,0)` in
the disarm path, push one more zero-psccF block to tick the calcc
state machine into LRESET and trigger the SetTXAiqcEnd cascade.
Or call `SetTXAiqcEnd(id)` directly from Zeus on disarm (would
require adding the P/Invoke).

If #1 mutes too (first-arm mute), Hypothesis A is eliminated — the
bug is something in Hypothesis B or C territory and the
`tx.peaks iq=…` log is the next discriminator (per Q6 above).

**No code fix to recommend until bench-tester reports the
first-arm vs subsequent-arm distinction.** The iqc audit confirms the
mute mechanism is real but eliminates Zeus as the direct trigger.

---

## Post-PA-mute COLLECT convergence audit (task #4)

> **Update 2026-05-03.** Two fixes landed (uncommitted in worktree):
>
> 1. **M1 PA-mute fix** in `Zeus.Protocol1/ControlFrame.cs WriteAttenuatorPayload`
>    — side-aware C4 encoding (`tx_step_attn` default 31 during MOX, `rx_step_attn`
>    default `60-db` otherwise; clamp 0..59 / 0..60 respectively).
> 2. **D1 disable** in `Zeus.Server.Hosting/PsAutoAttenuateService.TickAutoCalHwPeak`
>    — early `return;` with comment citing `docs/puresignal.hl2.md` D1.
>
> Plus instrumentation added: `wdsp.pscc.in tx.peak=… rx.peak=…` log line at
> 1 Hz, and `wdsp.psState` now includes `info5={Cal}` (CalibrationAttempts).
>
> Bench symptom now: PA delivers RF (M1 worked), but PS still doesn't apply
> corrections — `info[4] FeedbackLevel = 0` after 6 s of TX. This section
> answers the team-lead's source-side question (d) and traces the WDSP code
> paths that determine whether COLLECT advances and what makes `info[4]`
> non-zero.

### CV.A — Question (d): pscc tx_buf source — gateware DDC3, NOT host-side `_psTxRingI/Q`

**Definitive answer (no bench data needed):**

`Zeus.Protocol1/Protocol1Client.HandlePs4DdcPacket` `:227-330` decodes the
4-DDC EP6 packet and writes:

```csharp
// :270-278 — DDC2 → pscc RX, DDC3 → pscc TX. Mirror mi0bot
// cmaster.cs:8537-8538 (FOUR_DDC routing for HL2 with tot=5).
for (int s = 0; s < samples; s++)
{
    _psRxI[_psBlockFill] = (float)ddc2[2 * s];      // RX feedback ← DDC2 (post-PA tap)
    _psRxQ[_psBlockFill] = (float)ddc2[2 * s + 1];
    _psTxI[_psBlockFill] = (float)ddc3[2 * s];      // TX reference ← DDC3 (TX-DAC loopback)
    _psTxQ[_psBlockFill] = (float)ddc3[2 * s + 1];
    ...
}
```

These buffers are emitted as `PsFeedbackFrame(txI, txQ, rxI, rxQ, seq)` and
consumed by `DspPipelineService.StartPsFeedbackPumpP1` →
`engine.FeedPsFeedbackBlock(txI, txQ, rxI, rxQ)` →
`NativeMethods.psccF(id, 1024, txI, txQ, rxI, rxQ, 0, 0)`.

**Zeus's pscc tx_buf source = gateware DDC3 (TX-DAC loopback at TX freq,
post-AD9866 PGA per protocol-doc and mi0bot-spec).** This matches mi0bot's
4-DDC HL2 path exactly.

The host-side `_psTxRingI/Q` (`Protocol1Client.cs:191-212`) is **dead code**:

- 1 producer: `PsTapIqSource.Next` → `RecordPsTxSample` (`:1051-1066`).
- **0 consumers** — verified `grep -rn "_psTxRingI\|_psTxRingQ" --include="*.cs"`
  returns only the field declaration, the producer write, and zero readers.

`_psTxRingI/Q` is **NOT blocking PS convergence.** It's a future-use
snapshot of host-side TX-IQ that current PS uses DDC3 for instead. My
earlier "open question" can be closed: the pscc tx_buf source is
correct.

### CV.B — Why `info[4]` can stay at 0 even with non-zero pscc inputs

Verified at the WDSP source level. `info[4]` (FeedbackLevel reported to host)
is written EXACTLY once in `calcc.c`:

```c
// calcc.c:369 — inside calc() (the worker function called from doPSCalcCorrection)
a->binfo[4] = (int)(256.0 * (a->hw_scale / a->rx_scale));
a->binfo[5]++;   // CalibrationAttempts increments at line 370
```

`binfo` → `info` copy happens at `calcc.c:841` inside the LCALC state
*after* `calcdone=1` is set by the worker thread.

**For `info[4]` to become non-zero, three things must all happen:**

1. **calc() must run at all.** Triggered by `Sem_CalcCorr` release at
   `calcc.c:837` inside LCALC. To reach LCALC, calcc traverses
   `LRESET → LWAIT → LMOXDELAY → LSETUP → LCOLLECT → MOXCHECK → LCALC`.
2. **LCOLLECT must advance to MOXCHECK.** Per `calcc.c:800`:
   `if (a->ctrl.full_ints == a->ints) state = MOXCHECK`. ALL `ints=16` bins
   must reach `spi=256` samples. (Else: `count >= 4*rate ≈ 4 sec` flushes
   per-bin counters and restarts at LSETUP — the "stuck in COLLECT"
   pattern.)
3. **calc() must reach line 369 without bailing out.** Earlier sanity
   checks at `:354` (`(binfo[0]==0 && binfo[7]==0)`) gate the rx_scale
   computation; if they fail, rx_scale stays at its prior (or default)
   value. binfo[4] is computed regardless, but if `rx_scale` is huge
   (no real RX feedback signal), `binfo[4] = 256 * hw_scale / huge ≈ 0`.

**The `info[5]` (CalibrationAttempts) instrumentation just added to
`wdsp.psState` log is the discriminator:**

| `info[5]` (PS-on MOX) | `info[4]`              | Diagnosis                                                      |
|------------------------|-------------------------|-----------------------------------------------------------------|
| **= 0** | = 0                     | calc() **never runs** → LCOLLECT not advancing (CV.C below)    |
| > 0     | = 0                     | calc() runs but `rx_scale` huge → no real RX feedback (CV.D)   |
| > 0     | > 0                     | calc() succeeded → PS converging                                |
| > 0     | > 0 + `info[14]=1` correcting | iqc is applying corrections — fully working                |

### CV.C — Why LCOLLECT can fail to advance (info[5]=0 case)

LCOLLECT bin math at `calcc.c:744-790`:

```c
for (i = 0; i < a->size; i++) {
    env = sqrt(tx[2*i]^2 + tx[2*i+1]^2);
    if (env > a->ctrl.env_maxtx) a->ctrl.env_maxtx = env;
    if ((env *= a->hw_scale) <= 1.0) {                  // a->hw_scale = 1 / hw_peak
        n = (int)(env * a->ints);                        // bin index 0..15 (16 bins)
        // ... store sample in txs[]/rxs[] for bin n
        if (++a->ctrl.cpi[n] == a->spi) a->ctrl.full_ints++;
    }
    // else: env > hw_peak → SAMPLE DROPPED, no bin update
}
if (a->ctrl.full_ints == a->ints) a->ctrl.state = MOXCHECK;
```

For LCOLLECT to advance, **every one of the 16 bins must collect 256
samples**. Bin `n` covers `env_post_scale ∈ [n/16, (n+1)/16)` (with bin 15
also catching the special `env==1.0` case at line 755).

**For bin 15 to fill: `env_raw ≥ 0.9375 × hw_peak`.**

Concrete numbers for HL2 with default `hw_peak = 0.233`:
- Bin 15 threshold: `env_raw ≥ 0.218`
- For typical bench drive ~21 % producing `env_max ≈ 0.22`, bin 15 fills
  marginally. Lower drive → bin 15 never fills → LCOLLECT never advances.

**Two excitation choices have very different bin-fill behaviour:**

- **TUN (single carrier).** `env_raw` is constant (CW carrier amplitude).
  Only ONE bin fills (the bin matching the constant amplitude). Bins
  0..14 (or whichever 15 bins aren't selected) NEVER fill. **LCOLLECT
  never advances. PS cannot converge with TUN.**
- **Two-tone (PureSignal calibration excitation).** `env_raw` sweeps
  sinusoidally between 0 and `peak` at the difference frequency
  (e.g., 1.2 kHz for 700+1900 Hz tones). Over time, samples land in
  every bin from 0 (zero-crossing) to 15 (peak). **LCOLLECT advances
  in tens of milliseconds.**

**Operator action required:** PS convergence test MUST use two-tone, NOT
TUN. The two-tone control was hidden from the transport bar in commit
`b152d51` per `CLAUDE.md` memory note `project_hl2_ps_session_2026_05_03`,
but it's still available in the PS Settings panel. The bench-tester's
"6 s of TX with info[4]=0" symptom is consistent with either:
- TUN excitation (most likely — info[5] would also stay 0)
- Two-tone with drive too low for env_max to fill bin 15
- Two-tone with hw_peak too high for env_max to clear bin 15 threshold

### CV.D — Why `info[4]=0` even when calc() runs (info[5]>0 case)

If `info[5]>0` but `info[4]=0`, calc() is running but:

```c
// calcc.c:369
binfo[4] = (int)(256.0 * (hw_scale / rx_scale));
```

is producing zero. With `hw_scale = 1/0.233 ≈ 4.29`, `binfo[4]=0` requires
`rx_scale ≥ ~1100`, which means `rx_scale` is huge.

Per `calcc.c:355`:
```c
rx_scale = 1.0 / (poly evaluation of txrxcoefs at index ix, dx)
```

`rx_scale` is the inverse of the TX→RX gain estimated by `xbuilder` from
the (env_TX, env_RX) sample pairs. If env_RX is dominated by noise or
DC offset (no real feedback signal arriving on DDC2), the polynomial fit
produces near-zero coefficients, `rx_scale → ∞`, `binfo[4] → 0`.

**This means even with TX present and DDC3 (TX-DAC loopback) carrying
signal, if DDC2 (PA-coupler RX feedback) is still dead — e.g., no
external coupler wired, or HL2 firmware not honouring `cntrl1=4`
properly — calc runs but produces meaningless output.**

The new `wdsp.pscc.in tx.peak=… rx.peak=…` log line discriminates:

| `tx.peak` | `rx.peak` | Diagnosis                                                  |
|-----------|-----------|------------------------------------------------------------|
| > 0       | > 0       | Both DDC streams carrying signal → check info[5] trajectory |
| > 0       | ≈ 0       | TX reference flowing on DDC3, **PA-coupler RX path dead** — operator needs external coupler wired (HL2 has no internal coupler per protocol-doc `:470-475`); or `cntrl1=4` not actually routing DDC2 to ADC1 |
| ≈ 0       | ≈ 0       | psccF receiving zeros — back to TX-IQ / pump audit (shouldn't happen post-PA-mute fix) |

### CV.E — Sequencing for bench-tester (no further code change needed for diagnosis)

Single bench run captures everything. Use **two-tone excitation** (PS Settings
panel → Two-Tone enable, default 700/1900/0.49). Capture
`journalctl -u zeus-server` during 5 s of PS-on MOX at 28.400 MHz with
external coupler wired. Grep for:

1. **`p1.ps.fb DDC2(rx) peak=… DDC3(tx) peak=…`** — Protocol1Client parser
   side. Both should be > 0 (DDC3 ≈ 0.22 ish, DDC2 = post-PA RX feedback
   amplitude depending on coupler gain).
2. **`wdsp.pscc.in tx.peak=… rx.peak=…`** — what's actually arriving at
   psccF. Should match (1) byte-for-byte (no in-flight scaling).
3. **`wdsp.psState … info4=… info5=…`** — calcc state-machine trajectory
   AND (new) CalibrationAttempts counter. Trace the diagnostic table in
   CV.B/CV.C/CV.D.
4. **`psAutoAttn.gate …`** — confirm dance gates aren't paralysing
   anything. With D1 disabled, autocal-hw-peak shouldn't appear; the
   3-state HL2 dance still runs but only if `feedback > 0` (which
   requires LCOLLECT to advance first).

### CV.F — Verdict and one-line code recommendations (conditional)

**No code fix recommended until bench data lands — but the menu is
narrow:**

| Bench observation                                  | Source-side action                                               |
|-----------------------------------------------------|------------------------------------------------------------------|
| Two-tone OFF during test (operator using TUN)      | Operator/UX issue, not a code bug. Document in PS panel: "PS calibration requires two-tone excitation; TUN single-carrier cannot fill calcc bins." |
| Two-tone ON, `wdsp.pscc.in rx.peak ≈ 0`            | DDC2 routing not delivering RX feedback. Look at `WriteAdcRoutingPayload` `:283-300` (currently `cntrl1=0x04` only when MOX+PS). Verify HL2 firmware accepts this byte. Possibly need `0x14` C3 (`user_dig_out` / coupler-bias bit?) or a different cntrl1 value. mi0bot-spec citation needed for exact firmware expectation. |
| Two-tone ON, both peaks > 0, `info[5]=0`           | LCOLLECT not advancing. Either drive too low (env_max < 0.218 with hw_peak=0.233 → bin 15 starves) OR hw_peak too high. Operator can manually tune hw_peak in PS Settings to slightly above observed `MaxTxEnvelope`. **Do NOT re-enable D1 auto-cal** — its over-correction starves bins 0..13. If automatic tuning is desired, the target should be `hw_peak ≈ env_max / 0.9` (room for both upper and lower bin fill), NOT `env_max * 1.02`. |
| Two-tone ON, both peaks > 0, `info[5]>0, info[4]=0` | calc() runs but `rx_scale` huge → no real RX feedback signal. Same diagnosis as CV.D row 2: external coupler / cntrl1 routing issue. |
| Two-tone ON, both peaks > 0, `info[5]>0, info[4]>0, info[14]=0` | Convergence underway but `iqc->run` not yet flipped. Wait one or two more LCALC cycles. If `info[14]` never asserts, look at `binfo[6]` flag bits (calcc.c:239-283) for which sanity check is rejecting calc results. |
| All four > 0, `info[14]=1` correcting              | **PS converged; success.** No further fix needed. |

**Recommended bench-tester action:** capture the four logs above during a
single 5 s two-tone PS-on MOX burst, report the row from the table that
matches.

---

## psccF vs pscc source-pointer trace (time-boxed 20 min)

> **Bench evidence at trace start:** `wdsp.pscc.in tx.peak/rx.peak` matches
> `p1.ps.fb DDC2/DDC3` byte-for-byte → samples reach `psccF`. But
> `info[5]=0` and `info[6]=0x0000` → calcc never invokes `calc()`.
> Question: is `psccF` writing to a buffer that calcc's LCOLLECT doesn't
> read from? Hypothesis from earlier: psccF and C-internal `pscc()` are
> separate paths.
>
> **Result of trace: hypothesis IS WRONG.** psccF and pscc write to the
> SAME buffer, run the SAME state machine. Buffer wiring is correct.
> Bug is elsewhere — operator-side or bin-fill mechanics. **STOPPING per
> 20-min timebox; clean fix candidate of the requested shape does NOT
> exist.**

### PSP.1 — `psccF` definition (calcc.c:906-924)

```c
PORT
void psccF (int channel, int size,
            float *Itxbuff, float *Qtxbuff, float *Irxbuff, float *Qrxbuff,
            int mox, int solidmox)
{
    int i;
    CALCC a;
    EnterCriticalSection (&txa[channel].calcc.cs_update);
    a = txa[channel].calcc.p;
    // a->mox = mox;
    // a->solidmox = solidmox;       ← mox/solidmox args IGNORED, commented out
    LeaveCriticalSection (&txa[channel].calcc.cs_update);

    for (i = 0; i < size; i++) {
        a->temptx[2 * i + 0] = (double)Itxbuff[i];
        a->temptx[2 * i + 1] = (double)Qtxbuff[i];
        a->temprx[2 * i + 0] = (double)Irxbuff[i];
        a->temprx[2 * i + 1] = (double)Qrxbuff[i];
    }

    pscc (channel, size, a->temptx, a->temprx);    // ← directly calls pscc
}
```

`psccF` is a **`float→double` shim around `pscc()`**. It:
1. Copies the four float input buffers into the `CALCC` struct's
   `temptx`/`temprx` double-precision scratch buffers (allocated at
   `calcc.c:170-171` `(double*)malloc0(2048 * sizeof(complex))`).
2. Immediately calls `pscc(channel, size, a->temptx, a->temprx)` with
   pointers to those scratch buffers.

**No separate code path.** The `// remove later` comments at
`calcc.c:170-171` suggest WDSP devs intended to inline the conversion
eventually but kept it as a temporary scratch.

### PSP.2 — `pscc` definition (calcc.c:646)

```c
PORT
void pscc (int channel, int size, double* tx, double* rx) {
    ...
    if (a->runcal) {
        a->size = size;
        // ... runs the full calcc state machine on the (tx, rx) buffers
        // passed in. LCOLLECT (calcc.c:744-790) reads tx[2*i] / tx[2*i+1]
        // for env, rx[2*i] / rx[2*i+1] for the matched RX feedback sample.
    }
}
```

`pscc` directly drives the state machine using whatever `tx, rx` pointers
the caller supplied. When called from `psccF`, those pointers are
`a->temptx, a->temprx` — i.e., the buffers `psccF` just filled.

**Conclusion:** psccF and pscc are NOT writing to different buffers.
The data flow is:
```
Zeus FeedPsFeedbackBlock → P/Invoke psccF
  → psccF writes Itxbuff/Qtxbuff/Irxbuff/Qrxbuff → CALCC.temptx/temprx
  → psccF calls pscc(channel, size, a->temptx, a->temprx)
  → pscc runs LCOLLECT against a->temptx/a->temprx
  → LCOLLECT reads env from a->temptx, bins samples in a->ctrl.cpi[n]
```

If `wdsp.pscc.in` shows non-zero peaks, `a->temptx`/`a->temprx` are
non-zero, and LCOLLECT is reading non-zero envelopes. The chain from
psccF → calcc bin-filler IS connected.

### PSP.3 — LCOLLECT bin-fill: where bin 15 starves

`calcc.c:744-790` (LCOLLECT loop body):

```c
for (i = 0; i < a->size; i++) {
    env = sqrt(tx[2*i+0]^2 + tx[2*i+1]^2);          // raw envelope
    if (env > a->ctrl.env_maxtx) a->ctrl.env_maxtx = env;
    if ((env *= a->hw_scale) <= 1.0) {              // env_post = env / hw_peak
        n = (int)(env * a->ints);                    // bin index 0..15
        // store sample in bin n's slot (a->txs[]/rxs[])
        if (++a->ctrl.cpi[n] == a->spi) a->ctrl.full_ints++;
        ++a->ctrl.count;
    }
    // env_raw > hw_peak → SAMPLE DROPPED, no count increment
}

if (full_ints == ints) state = MOXCHECK;      // calcc.c:800 — advance
else if (info[13] >= 6) state = LRESET;       // dog count
else if (count >= 4 * rate) {                  // calcc.c:804 — flush
    count = 0; for-each cpi[i]=0; full_ints=0;
}
```

**For LCOLLECT to advance to MOXCHECK (and trigger calc), every bin
0..15 must reach `spi=256` samples.** With `hw_peak=0.233` (HL2 default,
Zeus matches) and `env_max=0.22`:
- Bin 15 covers `env_post ∈ [0.9375, 1.0]` → `env_raw ∈ [0.218, 0.233]`.
- TX envelope reaches bin 15 only when env_raw > 0.218.
- For two-tone with peak P, env sweeps 0..2A (where 2A=peak). If 2A < 0.218,
  bin 15 NEVER fills.
- After `count >= 4 * 192_000 = 768_000` BINNED samples without all
  bins filling, the per-bin counters flush and start over. Stuck in
  LCOLLECT forever.

### PSP.4 — `SetPSRxIdx` / `SetPSTxIdx` are NOT WDSP functions

mi0bot-spec mentioned `cmaster.cs:455-456` does `SetPSRxIdx(0,0); SetPSTxIdx(0,1)`.
These are **CHANNELMASTER (Thetis-side) router functions, NOT WDSP**:

```bash
grep -rn "SetPSRxIdx\|SetPSTxIdx" native/wdsp/
→ ZERO matches
```

ChannelMaster is a Thetis layer above WDSP that routes `xrouter` callbacks
to `pscc()`. **Zeus bypasses ChannelMaster entirely** — it does its own
`Protocol1Client.HandlePs4DdcPacket` parse and calls `psccF` directly via
P/Invoke. So `SetPSRxIdx`/`SetPSTxIdx` are not needed in Zeus. They have
no WDSP-side equivalent to add.

### PSP.5 — Verdict

**The buffer wiring is correct end-to-end.** `psccF` is the same path
as `pscc`, and calcc's LCOLLECT consumes the samples Zeus is feeding.
Bench's `wdsp.pscc.in tx.peak/rx.peak` matching `p1.ps.fb DDC2/DDC3` is
exactly the right confirmation that data is flowing. **The bug is that
LCOLLECT bins are not all reaching `spi=256` samples** — either because
the operator is using TUN (single carrier → only one bin fills) or
because `env_max` is below `0.9375 × hw_peak = 0.218` so bin 15 never
fills.

**No code fix candidate of the requested shape exists:**

- ❌ "Add P/Invoke X and call it on PS arm or at OpenTxChannel."
  → No init function is missing. `SetPSRxIdx`/`SetPSTxIdx` don't exist
  in WDSP. Zeus's `OpenTxChannel` PS seed (`WdspDspEngine.cs:1249-1266`)
  already calls every `SetPS*` setter that affects the calcc data path.
- ❌ "Switch from psccF to pscc-equivalent and pass these specific arguments."
  → They're the same path. Switching from float to double inputs would
  not change behaviour (psccF converts internally and forwards to pscc).
- ❌ "Wire psccF's output buffer into calcc's input pointer at init."
  → They're already wired. psccF's "output buffer" IS calcc's input
  pointer (`a->temptx`/`a->temprx`).

The remaining fix space is:
- **Operator-side:** use two-tone (not TUN), tune drive level OR
  hw_peak so `env_max ≥ 0.218` for HL2 default `hw_peak=0.233`.
- **Code-side (red-light per CLAUDE.md "defaults"):** lower
  `ResolvePsHwPeak((false, HermesLite2))` from `0.233` to ~`0.18`
  (matches operator memory note for drive=21 %). Mi0bot ships 0.233 for
  HL2; this would be a Zeus-specific deviation requiring maintainer
  approval.
- **Code-side (red-light per CLAUDE.md "defaults"):** re-introduce
  `TickAutoCalHwPeak` with corrected target `hw_peak ≈ env_max / 0.9`
  (room for upper AND lower bin fill). The prior version targeted
  `env_max * 1.02` which over-corrected and starved bins 0..13.

**STOPPING per 20-min timebox.** No code change to ship in this session.

### PSP.6 — Honest read on what 30-60 more minutes would buy

A deeper dive could clarify TWO open items but neither yields a
mechanical fix:

1. **What hw_peak does `env_max ≈ 0.22` need to fully populate all 16
   bins?** Math suggests `hw_peak ≈ 0.245` (env_max post-scaled =
   0.22/0.245 = 0.898 → covers bins 0..14 cleanly, bin 15 needs slight
   over-drive to fire). Not a one-line fix — would require introducing
   either a default change (red-light) or a properly-tuned auto-cal
   (red-light). Neither shape matches the team-lead's allowed fix
   templates.
2. **What's the actual bench excitation?** TUN (single carrier) cannot
   ever converge per LCOLLECT mechanics. Two-tone with sufficient drive
   should. This is operator/UX clarification, not source code.

A new session should:
1. Get bench-tester to confirm two-tone vs TUN, drive %, observed
   `env_max`, and observed `info[5]` trajectory.
2. With those numbers in hand, decide whether default change or
   auto-cal v2 is the right shape — both are red-light per CLAUDE.md
   and need maintainer (Brian, EI6LF) sign-off.

**Recommend shipping M1 (PA-mute fix) + D1-disable as a standalone PR
now.** Picking up the bin-fill convergence is a maintainer-decision
session, not an audit session.

## Session log

### 2026-05-03 — kickoff

- Symptom: PS collected fine earlier today, no longer collecting after recent
  changes. Reverts on this branch include #229 (revert PS-A master arm
  persistence). See `git log` since `5dfa3cd feat(ps): redesign PureSignal
  settings as live calibration dashboard` for the change set being investigated.
- Recent memory notes (worth re-checking against current state, may be stale):
  - HL2 default `hw_peak` 0.233 is too high for typical drive levels; ~0.18 at
    drive=21% is what got COLLECT moving on previous session.
  - HL2 has an internal coupler — the PS Internal/External feedback selector
    must remain visible on HL2.
- Backend port 6060, frontend Vite dev 5173 (per `docs/lessons/dev-conventions.md`).

### 2026-05-03 — bench-tester run (28.400 MHz, drive=21%, TUN=59%)

Hardware: HL2 connected at `192.168.100.21:1024`. VFO 28.400 MHz USB,
10 m. Antenna resonant on 28.400. Two-tone generator used as PS excitation
(MOX engages automatically per `TxService.TrySetTwoTone`).

**Run 1 — defaults (`hw_peak = 0.233`, no reset)**

| t | calState (name) | corr | feedbackLevel | hwPeak | twoTone |
|---|-----------------|------|---------------|--------|---------|
| baseline | 1 (WAIT) | false | 0 | 0.233 | false |
| t=1s | 4 (COLLECT) | false | 0 | 0.217 | true |
| t=2s | 4 (COLLECT) | false | 0 | 0.217 | true |
| t=3s | 4 (COLLECT) | false | 0 | 0.217 | true |
| t=4s | 4 (COLLECT) | false | 0 | 0.217 | true |
| t=5s | 4 (COLLECT) | false | 0 | 0.217 | true |
| post-disengage | 1 (WAIT) | false | 0 | 0.217 | false |

PURESIGNAL popover during TX showed: `STATE COLLECT`, `MODE Auto`,
`Observed 0.2231` (envelope IS in TX path), `HW peak 0.2170`,
`CORRECTION —`, calibration-attempts counter `0`.

**Run 2 — operator-recommended (`hw_peak = 0.18`, after `POST /api/tx/ps/reset`)**

| t | calState (name) | corr | feedbackLevel | hwPeak |
|---|-----------------|------|---------------|--------|
| pre-engage | 1 (WAIT) | false | 0 | 0.18 |
| t=1s | 1 (WAIT) | false | 0 | 0.18 |
| t=2s | 4 (COLLECT) | false | 0 | **0.2309** |
| t=3..7s | 4 (COLLECT) | false | 0 | 0.2309 |
| post-disengage | 1 (WAIT) | false | 0 | 0.2309 |

Auto-attenuate **raised** `hw_peak` from 0.18 → 0.2309 within ~2 s of TX
keyup. Same outcome as Run 1: stuck in COLLECT, feedback=0, no calc()
ever fires.

**Visual confirmation TX is on the air**

- Two-tone humps visible in panadapter at ±700 / ±1900 Hz around 28.400.
- Bright streak across waterfall during TX, fading after disengage.
- `MaxTxEnvelope` (popover "Observed") = 0.22..0.23 throughout, so WDSP
  TXA is producing real envelope.

Forward-power meter strip ("FWD ………… 1KW" at top of panadapter) showed
no ticks — TX STAGE METERS panel was not expanded for this run, so a
quantitative forward-power figure is not captured here. Did **not** need
to raise drive/TUN to get TX active; two-tone at drive=21% put a clearly
visible signal on the air. (Could be revisited if the auditor needs the
actual wattmeter reading; both 2-tone tests engaged TX at default sliders
without complaint.)

**Diagnosis (handing to auditor)**

The radio transmits, WDSP TXA produces envelope (`MaxTxEnvelope ≈ 0.22`),
auto-attenuate adjusts `hw_peak`, and the cal state machine advances
`WAIT → COLLECT (4)`. It then **stays in COLLECT forever** because
`FeedbackLevel = info[4] = 0` for the entire duration — calcc has zero
samples to fit, never calls `calc()`, never advances to MOXCHECK / CALC.

This is not a `hw_peak` calibration issue (lowering it didn't help — and
auto-attenuate over-rode the operator setting in <2 s). It is a
**feedback-path delivery** problem: either DDC1/PS feedback frames aren't
arriving from the HL2, or `FeedPsFeedbackBlock` isn't being called, or
the values being passed are zero. Auditor should target the
`Protocol1Client` PS feedback ingest and `WdspDspEngine.FeedPsFeedbackBlock`
plumbing — the symptom is a clean "TX yes / FB no" split, which points at
the receive-side of the PS pipeline, not at WDSP cal config.

Recent commit `5dfa3cd feat(ps): redesign PureSignal settings as live
calibration dashboard` is the suspect window per task #1's notes; the
revert in #229 stripped PS-A master arm persistence but not the rest of
the redesign.

**Bench safety**

Radio confirmed un-keyed at end of every test step (`twoToneEnabled=false`,
`calState=1`). PS disarmed at session end (`psEnabled=false`). Did not
idle in MOX. Antenna constraint respected — TX only on 28.400.

### 2026-05-03 — bench-tester re-test after D1 fix landed

Backend restarted (PID changed; HL2 reconnected to `192.168.100.21:1024`,
psHwPeak reset to default 0.233 on connect via `ApplyPsHwPeakForConnection`).
Same procedure as previous run — VFO 28.400 MHz USB, drive=21%, two-tone.

**Run A — `hw_peak = 0.233` (default), `POST /api/tx/ps/reset` first**

| t | calState (name) | corr | feedbackLevel | hwPeak |
|---|-----------------|------|---------------|--------|
| t=1s | 0 (RESET) | false | 0 | 0.233 |
| t=2s | 4 (COLLECT) | false | 0 | 0.233 |
| t=3..10s | 4 (COLLECT) | false | 0 | 0.233 |
| post-disengage | 1 (WAIT) | false | 0 | 0.233 |

**Run B — `hw_peak = 0.18` explicit, reset before TX**

| t | calState (name) | corr | feedbackLevel | hwPeak |
|---|-----------------|------|---------------|--------|
| t=1s | 1 (WAIT) | false | 0 | 0.18 |
| t=2..10s | 4 (COLLECT) | false | 0 | 0.18 |
| post-disengage | 1 (WAIT) | false | 0 | 0.18 |

**D1 fix confirmed working ✅**

- `hw_peak` stays exactly at the operator-set value across the full
  10 s MOX in both runs. No snapback to `env*1.02 ≈ 0.23`.
- Backend log: **zero** `psAutoAttn.autoCal env=...` lines across both
  runs (was firing every ~1 s under MOX before the fix).

**But COLLECT still doesn't progress ❌** — and the new backend-log
evidence narrows the remaining failure substantially:

```
psAutoAttn.start
psAutoAttn.gate skip=not-keyed             (idle)
wdsp.pscc fed 1 blocks                      ← FIRST 2-tone keyup
wdsp.psState 255->1 info4=0 info6=0x0000 info13=0 info14=0
wdsp.psState 1->2   info4=0 info6=0x0000 info13=0 info14=0
wdsp.psState 2->4   info4=0 info6=0x0000 info13=0 info14=0
wdsp.pscc fed 101 blocks
wdsp.pscc fed 201 blocks
... (keeps incrementing through 1901+ during one 10 s MOX)
wdsp.psState 4->1   info4=0 info6=0x0000 info13=0 info14=0   (un-MOX)
```

So `WdspDspEngine.FeedPsFeedbackBlock` IS being called at the expected
~10 Hz × 1024-sample cadence (`DspPipelineService.StartPsFeedbackPumpP1`,
~line 753, is alive). WDSP's psState machine IS ticking through
255 → 1 → 2 → 4 (WAIT → MOXDELAY → COLLECT). But `info[4] FeedbackLevel`
stays at 0 through 1900+ fed blocks, and `info[6]` stays at 0x0000.

This is **not** a "feedback frames aren't arriving" problem — they are
arriving and being handed to WDSP. It is a **feedback frames carry zero
RxI/RxQ payload** problem (or values too small to register in calcc's
envelope detector).

**Diagnosis update — handing back to auditor / fixer**

The remaining fix should target the P1 PS-feedback frame **producer**
(the Protocol1Client side that writes into `PsFeedbackFrames`), not WDSP
or auto-cal. Three live hypotheses:

1. **HL2 isn't actually emitting feedback IQ on the wire.** DDC1 / PS
   feedback antenna may not be configured even with `psEnabled=true`.
   Needs a `Protocol1Client` packet capture or wire-byte log to confirm.
2. **Frames are produced but `RxI`/`RxQ` are zero-filled.** Could be a
   DDC slot mix-up, a demux bug, or TX-modulator/RX-coupler IQ wired
   backwards into the frame record.
3. **Internal-coupler routing register isn't asserted on the C&C bus**
   when PS arms. (HL2 internal coupler per memory note;
   `psFeedbackSource = "Internal"` was the default in both runs.)

Recommend instrumenting the producer: log `min/max/abs-mean` of
`frame.TxI`, `frame.TxQ`, `frame.RxI`, `frame.RxQ` at the Protocol1Client
emit site for one full 10 s MOX. That immediately distinguishes (1) vs
(2) vs (3): if RxI/RxQ are zero but TxI/TxQ are non-zero, frames are
being assembled with the wrong receive slot or with no receive slot at
all.

Do not re-introduce auto-cal as a workaround. Run A proves the default
0.233 is fine the moment real feedback samples arrive — the previous
"0.18 worked once" memory was very likely the same underlying delivery
bug expressed differently, not a true `hw_peak` calibration issue.

**Bench safety**

Radio confirmed un-keyed at end of every step (`twoToneEnabled=false`).
PS disarmed at session end (`psEnabled=false`). Backend log preserved
at `/tmp/zeus-server-postfix.log` plus the background-task output file
for the auditor's reference.

### 2026-05-03 — D5 log triplet capture (post-D1 fix verification)

Per team-lead's request, captured the auditor's D5 log triplet during a
clean ~5 s MOX window on 28.400 MHz USB, drive=21%, hw_peak=0.18.

**State machine (API state, ~1 Hz):**

| t | calState | feedbackLevel | psCorrecting | hwPeak |
|---|----------|---------------|--------------|--------|
| t=1s | 1 (WAIT) | 0 | false | 0.18 |
| t=2..5s | 4 (COLLECT) | 0 | false | 0.18 |
| post | 1 (WAIT) | 0 | false | 0.18 |

**D5 log triplet (all three lines fired during the same MOX window):**

```
wdsp.psState 255->1 info4=0 info6=0x0000 info13=0 info14=0
wdsp.pscc fed 1 blocks
wdsp.psState 1->2  info4=0 info6=0x0000 info13=0 info14=0
wdsp.psState 2->4  info4=0 info6=0x0000 info13=0 info14=0
p1.ps.fb DDC2(rx) peak=0.0001 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=3990
wdsp.pscc fed 101 blocks
wdsp.pscc fed 201 blocks
p1.ps.fb DDC2(rx) peak=0.0001 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=4180
wdsp.pscc fed 301 blocks
wdsp.pscc fed 401 blocks
p1.ps.fb DDC2(rx) peak=0.0001 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=4370
wdsp.pscc fed 501 blocks
wdsp.pscc fed 601 blocks
p1.ps.fb DDC2(rx) peak=0.0001 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=4560
wdsp.pscc fed 701 blocks
wdsp.pscc fed 801 blocks
p1.ps.fb DDC2(rx) peak=0.0001 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=4750
wdsp.pscc fed 901 blocks
wdsp.psState 4->1 info4=0 info6=0x0000 info13=0 info14=0
```

(`psAutoAttn.autoCal` lines: **zero** — D1 fix neutralisation confirmed
again.)

**D5 decision-table mapping**

The team-lead's table is:

- Both DDC peaks > 0 + pscc fed firing + state past LCOLLECT → D1 fixed.
- DDC3 > 0, DDC2 ≈ 0 → D2 (gateware not routing PA loopback).
- `p1.ps.fb` absent → D2 (parser never entered / wire selector missing).
- hw_peak still snaps → fix didn't take effect.

What we actually see is a fourth pattern, more severe than the listed
D2 case:

- ✅ hw_peak holds (D1 mechanically fixed).
- ✅ `p1.ps.fb` IS firing — parser entered.
- ✅ `wdsp.pscc fed` IS firing — blocks reach WDSP.
- ✅ `wdsp.psState` IS ticking 255 → 1 → 2 → 4 (WAIT → MOXDELAY → COLLECT).
- ❌ DDC2(rx) peak ≈ 0.0001 (noise-floor; mean = 0.0000).
- ❌ DDC3(tx) peak = 0.0000 exactly (mean = 0.0000) — **TX modulator IQ
  is zero in the feedback frame even though we are clearly transmitting
  a two-tone signal at this moment** (visually confirmed on the
  panadapter in the prior bench session).
- ❌ Consequently `info[4] FeedbackLevel = 0` for the entire MOX window;
  COLLECT never advances.

This maps to **D2** (or a near-relative of it): the wire-side / frame-
assembly side of the P1 PS feedback path is delivering zero-magnitude
samples in BOTH the rx-coupler slot AND the tx-modulator slot, despite
the parser's per-block bookkeeping working (block counter advances
~190 / sec, matches expected 192 kHz / 1024 sample cadence).

DDC3 = 0 is the louder signal here — the TX-modulator IQ that feeds
into psccF should never be zero while WDSP TXA is producing envelope.
Either the Protocol1Client parser is putting samples in the wrong
fields of the `PsFeedbackFrame` record, or the upstream frame producer
isn't capturing the TX baseband at the assembly point, or the slot
mapping at the wire selector is misconfigured.

**Per team-lead's instructions: handing back to fixer. Do NOT mark
task #4 completed.** This is not D1's fix being insufficient — D1 IS
fixed and verifiable. The remaining defect lives in the P1 feedback
producer / wire selector and needs its own targeted change.

Suggested next step for fixer (or the auditor, depending on who owns
the wire-side code): check `Protocol1Client`'s PS-feedback frame
assembly. Specifically (a) what register/wire bit selects which DDC
slots feed the PS pipeline on HL2, (b) whether the Zeus parser is
reading those slots from the right byte offsets, and (c) whether the
TX-modulator IQ is captured at the right point in the TX path before
it's stuffed into the feedback frame. The 0.0001 vs 0.0000 split
between DDC2 and DDC3 is consistent: DDC2 picks up trace ADC noise
floor, DDC3 sees literal zero.

**Bench safety**

Radio confirmed un-keyed at end of every test step. PS disarmed at
session end. Single MOX window of ~5 s on 28.400 only.

#### Raw `grep "p1.ps.fb"` output (per team-lead's #5 request)

`/tmp/zeus-server-postfix.log` was empty — that first `nohup dotnet run`
backgrounded via shell exited before binding. The live backend log lives
in the Bash background-task output file (PID `82806`, `dotnet run` task
id `btkkrtjhz`). All 25 captured `p1.ps.fb` lines, verbatim, across the
two MOX windows in this session:

```
p1.ps.fb DDC2(rx) peak=0.0001 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=190
p1.ps.fb DDC2(rx) peak=0.0001 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=380
p1.ps.fb DDC2(rx) peak=0.0000 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=570
p1.ps.fb DDC2(rx) peak=0.0001 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=760
p1.ps.fb DDC2(rx) peak=0.0001 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=950
p1.ps.fb DDC2(rx) peak=0.0001 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=1140
p1.ps.fb DDC2(rx) peak=0.0001 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=1330
p1.ps.fb DDC2(rx) peak=0.0000 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=1520
p1.ps.fb DDC2(rx) peak=0.0001 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=1710
p1.ps.fb DDC2(rx) peak=0.0001 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=1900
p1.ps.fb DDC2(rx) peak=0.0001 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=2090
p1.ps.fb DDC2(rx) peak=0.0001 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=2280
p1.ps.fb DDC2(rx) peak=0.0000 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=2470
p1.ps.fb DDC2(rx) peak=0.0001 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=2660
p1.ps.fb DDC2(rx) peak=0.0001 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=2850
p1.ps.fb DDC2(rx) peak=0.0001 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=3040
p1.ps.fb DDC2(rx) peak=0.0000 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=3230
p1.ps.fb DDC2(rx) peak=0.0001 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=3420
p1.ps.fb DDC2(rx) peak=0.0000 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=3610
p1.ps.fb DDC2(rx) peak=0.0001 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=3800
p1.ps.fb DDC2(rx) peak=0.0001 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=3990
p1.ps.fb DDC2(rx) peak=0.0001 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=4180
p1.ps.fb DDC2(rx) peak=0.0001 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=4370
p1.ps.fb DDC2(rx) peak=0.0001 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=4560
p1.ps.fb DDC2(rx) peak=0.0001 mean=0.0000 | DDC3(tx) peak=0.0000 mean=0.0000 | blocks=4750
```

**Maps to team-lead's middle-ish branch, but worse:** the `p1.ps.fb`
line is present (parser entered, blocks counter advancing at the
expected ~190 blocks/s rate) — but **DDC3(tx) peak is also literal
zero**, not just DDC2(rx). That rules out the simple "rx slot empty,
tx slot fine" failure mode. Both slots contain zero-magnitude data
across all 25 samples; DDC2 occasionally registers 0.0001 (likely
quantisation noise on a single 24-bit sample), DDC3 is always exactly
0.0000.

Closest fit on the team-lead's three-way: option (b) — frames assembled
but rx slot empty — extended to "frames assembled but BOTH rx and tx
slots empty." Suggests either a wire-byte / DDC-config issue that
disables both slots, or a Zeus-side parser bug that's reading from the
wrong byte offsets and landing on zero-padded regions of the EP-6 frame.
fixer / mi0bot-spec follow-up via task #5 is the right next step.

### 2026-05-03 — PA-output drop verification *(SMOKING GUN — re-frames the diagnosis)*

Operator's claim: "nothing changed, no power when pure signal is on."
Team-lead asked for a quantitative FWD/REF wattage comparison.
Backend was freshly brought up by `/run`; HL2 connected on
`192.168.100.21:1024` at 28.400 MHz USB. Sliders as left by `/run`:
DRV = 100 %, TUN = 100 %.

Power readings sampled by attaching a small JS WebSocket sniffer in the
page to `/ws`, parsing the `TxMetersV2Frame` (tag 0x16) wire format
directly. Each measurement is the latest received frame after ~1.5 s of
MOX.

| Test | psEnabled | Mode | FeedbackSrc | FwdW | RefW | SWR |
|------|-----------|------|-------------|------|------|-----|
| Idle (no MOX) | false | — | — | 0.0 W | 0.0 W | 1.0 |
| **Test 1: 2-tone, PS OFF** | **false** | — | Internal | **3.281 W** | **0.113 W** | **1.46** |
| Idle | false | — | — | 0.0 W | 0.0 W | 1.0 |
| **Test 2: 2-tone, PS ON, Auto** | **true** | Auto | Internal | **0.0000154 W** | 0.0000152 W | 1.0 |
| Idle | true | Auto | Internal | 0.0 W | 0.0 W | 1.0 |
| **Test 3: 2-tone, PS ON, Single/Manual** | **true** | Single | Internal | **0.0000155 W** | 0.0000150 W | 1.0 |
| Idle | true | Single | Internal | 0.0 W | 0.0 W | 1.0 |
| **Test 4: 2-tone, PS ON, Auto, External** | **true** | Auto | **External** | **0.0000096 W** | 0.0000152 W | 1.0 |
| Final idle | false | — | Internal | 0.0 W | 0.0 W | 1.0 |

**Conclusion: armed PS mutes the PA, end of story.**

- `psEnabled = true` drops forward power from **3.281 W → ~15 µW**, a
  ~213 000× reduction. Reflected power drops similarly
  (0.113 W → ~15 µW). This is a **full mute**, not partial attenuation,
  and not a meter display bug — the residual ~15 µW reading is coupler
  noise floor.
- The mute is **independent of cal mode** (Auto vs Single both ~15 µW)
  and **independent of feedback source** (Internal vs External both
  ~10–15 µW). Whatever Zeus does on PS-arm, it stops the PA from
  radiating.
- This **completely re-explains the D5 finding.** DDC2(rx) and DDC3(tx)
  peaks were ~0 not because of a parser bug or a wire-selector bug —
  they were ~0 because the radio wasn't actually transmitting to begin
  with. The two-tone humps visible on the panadapter in earlier sessions
  are the WDSP TX panadapter (analyzer source = TX-IQ), a pre-PA
  WDSP-internal scope; it is unaffected by whether the HL2 actually
  keys its PA. So the panadapter trace mis-led prior sessions into
  thinking RF was on the air during PS-armed MOX.

**Where to look next** (handing back to team-lead / fixer; do **not**
mark task #4 / #6 completed unilaterally — this needs a maintainer-led
choice on which seam to fix):

The fact that the mute is independent of cal mode AND of feedback source
points at the PS master-arm path itself, not at any per-state cal logic.
Likely suspects, in order of probability:

1. **PS arm is dropping the HL2's `MOX` C&C bit on the wire**, even
   though Zeus thinks MOX is on (TxService says "MOX on", drive
   metering still claims envelope, WDSP TXA still produces IQ, but the
   HL2 itself is in receive). Audit: log the C&C0 byte that
   Protocol1Client emits on PS arm vs PS disarm during MOX, compare
   bit positions.
2. **PS arm is forcing an on-wire register that gates the PA / drives
   the C2 drive byte to zero on HL2.** mi0bot/Thetis writes a specific
   PSEnabled wire byte sequence; Zeus may be writing it with the wrong
   bit polarity or to the wrong C&C slot, killing the drive byte.
3. **PS arm is rerouting the TX-IQ ring** away from
   `Protocol1Client.TxLoopAsync` (e.g. into a PS-only feedback ring) so
   the radio gets zero-amplitude IQ even though WDSP is producing it.
   Quick check: arm PS, MOX, hit `/api/tx/diag` — if `ring.RecentMag` is
   near-zero or `ring.TotalRead` isn't advancing while
   `ring.TotalWritten` is, that confirms.

Recommend the auditor / fixer instrument **`Protocol1Client.TxLoopAsync`**
to print, once per second under MOX, the actual `c0/c1/c2/c3/c4` bytes
being written when PS is armed vs disarmed. The two-line diff between
those captures will name the offending wire bit.

**Bench safety**

- DRV at 100 % and TUN at 100 % were the slider positions left by the
  fresh `/run` start; not lowered for these tests because all four
  measurements are relative comparisons on the same antenna and the
  HL2's own PA limits cap output at ~3.3 W on 10 m here.
- Each MOX held ~1.5 s. Un-MOX confirmed by `FwdW = 0` between every
  test step. Final state: PS disarmed (`psEnabled=false`), two-tone off,
  feedback source restored to Internal, VFO still on 28.400 — antenna
  constraint respected throughout.

### 2026-05-03 — fixer's PS-encoder bisect — MUTE STILL PRESENT

fixer commented out two writes from commit 1e53807 in
`Zeus.Protocol1/ControlFrame.cs`:
1. `WriteAttenuatorPayload` — the `c14[1] |= 1<<6` (puresignal_run, C2[6]
   in the 0x14 frame) on HL2 + `PsEnabled`.
2. `WritePredistortionPayload` — body zeroed (already a no-op per
   `Protocol1Client.PhaseRegisters` comment that says predistortion
   isn't in the HL2 round-robin).

Backend killed and restarted with the change in place. After a clean
disconnect+reconnect (necessary — see "Init quirk" below), 2-tone test
on 28.400 MHz USB at default sliders (DRV=21%, TUN=59%):

| Test | psEnabled | FwdW | RefW | SWR |
|------|-----------|------|------|-----|
| 2-tone, PS OFF | false | **0.521 W** | 0.010 W | 1.0 |
| 2-tone, PS ON, Auto, Internal | true | **0.0000082 W** | 0.0000139 W | 1.0 |

PS-arm still drops Fwd ~64 000× (0.521 W → ~8 µW). Per fixer's own
"if TUN+PS_on still produces ~zero FWD → those writes aren't the bug"
clause, **the bisect FAILED**: the two commented-out writes are not the
PA-mute cause. The mute lives in some other PS-on code path.

This result is consistent with the auditor's M1 hypothesis (the
`tx_step_attn` C4 encoding in `WriteAttenuatorPayload`) being the
likely culprit instead — the writes I neutralised here were
`puresignal_run` (C2[6]) and `predistortion` (no-op), neither of which
overlap M1's C4 byte. M1's single-line fix candidate at lines 2253-2289
of this file should be the next bisect target.

**Init quirk worth recording** (cost ~5 min in the middle of this
test): after killing the previous `dotnet run` PID and starting a fresh
one, the new backend connected to the HL2 fine (`status=Connected`,
discovery healthy), but `Protocol1Client.TxLoopAsync` wasn't reading
from the TX-IQ ring — `/api/tx/diag` showed `ring.totalRead = 0` while
`totalWritten` and `dropped` advanced together (consumer stopped,
producer still running). 2-tone produced ~15 µW under that wedged
state regardless of PS arm. The fix was a manual `POST /api/disconnect`
followed by `POST /api/connect` — after that `totalRead` advanced
normally (`recentMag ≈ 20 600`) and TX power behaved. Future bench
runs that kill the backend mid-session should disconnect+reconnect via
the API before measuring power, not just check that `/api/state`
reports `Connected`.

**Bench safety**

Two short MOX windows (~1.5 s each); `FwdW=0` verified between every
step. Final state: PS disarmed, two-tone off, VFO 28.400.

**Handing back to fixer.** Their note "I will revert and we look at the
next suspect" is the right path. Recommend the next bisect target be
the auditor's M1 candidate (`tx_step_attn` C4 encoding) rather than
shotgunning around `SetPsEnabled` indirect effects.

### 2026-05-03 — M1 patch verification — STILL MUTED

fixer landed M1 patch in `Zeus.Protocol1/ControlFrame.cs:WriteAttenuatorPayload`:
- MOX: `c4 = 0x40 | clamp(31 - txatt, 0, 59)` (default `0x5F` at txatt=0).
- !MOX: `c4 = 0x40 | clamp(60 - db, 0, 60)` (preserves max-RX-gain encoding).
- Side-aware clamp (0..59 for MOX, 0..60 for !MOX) — keeps
  `ControlFrame_Attenuator_Hl2_WritesExtendedGainByte(db: 0, expectedC4: 0x7C)` green.
- 0x0a[22] puresignal_run write and `WritePredistortionPayload` body
  restored (gating bisect reverted).
- `dotnet build` clean; 732/732 tests pass per fixer.

Backend killed and restarted. Required disconnect+reconnect after first
connect to clear the `TxLoopAsync ring.totalRead = 0` wedge (init quirk
documented above). After that: DRV=21%, TUN=59%, VFO 28.400 MHz USB.

| Test | psEnabled | FwdW | RefW | SWR |
|------|-----------|------|------|-----|
| **TUN, PS OFF** | false | **2.695 W** | 0.118 W | 1.53 |
| **TUN, PS ON, Auto, Internal** | true | **0.0000052 W** | 0.0000142 W | 1.0 |

PS-arm still drops Fwd from 2.695 W → ~5 µW (~520 000×). **M1 alone is
NOT the PA-mute root cause.** The mute is still binary on `psEnabled`,
exactly the same shape as the pre-M1 numbers in the
`PA-output drop verification` table — just at a different drive level.

For comparison across all three bench runs in this session:

| Configuration | DRV | TUN | PS-OFF FwdW | PS-ON FwdW | Drop |
|---------------|-----|-----|-------------|------------|------|
| Pre-anything (2-tone) | 100 % | 100 % | 3.281 W | 0.0000154 W | 213 000× |
| Post-fixer-bisect (2-tone) | 21 % | 59 % | 0.521 W | 0.0000082 W | 64 000× |
| **Post-M1 (TUN)** | 21 % | 59 % | **2.695 W** | **0.0000052 W** | **520 000×** |

The PS-on tail is consistent at ~5–15 µW (coupler noise floor)
regardless of which fix has been tried — M1 didn't move the needle on
the PS-on side. M1 may still be a correct mi0bot-alignment fix in its
own right (and it explains why TUN PS-OFF baseline at the same DRV/TUN
sliders now yields 2.695 W vs the earlier 0.521 W — the PA encoding
became more efficient post-M1) but it does not address task #6.

**Next bisect target candidates** (handing back per fixer's runbook):

1. **M3** (lower-confidence per audit) — `0x12` DriveFilter C2/C3/C4
   bits beyond `cc[2] |= 0x08`. Mi0bot writes additional bits the audit
   couldn't enumerate without spec citation. If a `pa_enabled` bit
   lives there and PS-arm clears it, that fits the symptom.
2. **M4** (lower-confidence per audit) — `0x1c` AdcRouting C3 =
   `tx_step_attn` duplicate. Mi0bot writes the same TX step attenuator
   into both 0x14 C4 and 0x1c C3; Zeus only writes the 0x14 path. If
   HL2 gateware AND-combines them, Zeus's `0x1c C3 = 0` might produce a
   different effective PGA value when PS is armed (because some
   PS-active firmware path may switch which register dominates).
3. **`PsAutoAttenuateService` 3-state dance** — even with cal mode set
   to Auto/Internal default, the `SetNewValues` / `RestoreOperation`
   cycle may be writing a PA-related register that gates output.
   Worth instrumenting. D1's fix neutralised `hw_peak` auto-cal but the
   service itself still has live state machinery for `Hl2AutoAttState`
   transitions (see `PsAutoAttenuateService.cs`).
4. **mi0bot's `puresignal_run` does something the spec doc doesn't
   spell out** — e.g. flipping bit 22 might require a paired register
   write (e.g. enabling `xmit` in the TX-side gateware) that Zeus
   misses. Cross-check `mi0bot/openhpsdr-thetis/networkproto1.c` for
   any state that depends on `puresignal_run` having transitioned
   1→0→1 at startup.

**Bench safety**

Two short MOX windows (~1.5 s each); `FwdW=0` verified between every
step. Final state: PS disarmed (`psEnabled=false`), TUN off, VFO still
on 28.400 — antenna constraint respected throughout.

**Task #6 is NOT resolved.** Recommend fixer NOT mark it completed
yet — the symptom is unchanged. M1 may still be the right fix to keep
landed (it passes the "is the C4 encoding correct per mi0bot" audit)
but it doesn't unmute the PA on PS-arm.

### 2026-05-03 — wire-side C&C dump (task #7)

fixer landed `p1.cc.dump*` instrumentation in
`Zeus.Protocol1/Protocol1Client.TxLoopAsync`. **Behaviour quirk
observed:** the dump appears to be one-shot per backend lifetime —
the trigger only fires on the *first* qualifying MOX edge after
process start; subsequent MOX edges and PS toggles in the same
backend instance did not re-arm the dump in this session. To capture
both PS-off and PS-on, I had to:

1. Restart backend, leave PS off → first TUN engaged → Burst A captured.
2. Restart backend, **arm PS before any TUN** → first TUN engaged →
   Burst B captured.

Both captures at 28.400 MHz USB after the disconnect+reconnect
init-quirk workaround.

#### Burst A — `psOn=False, mox=True, psArmed=False`, sweepLen=4

```
p1.cc.dump.start trigger=mox-edge psOn=False mox=True psArmed=False board=HermesLite2 sweepLen=4
p1.cc.dump phase=01 slot=first  reg=TxFreq      C0=0x03 C1=0x01 C2=0xB1 C3=0x59 C4=0x80
p1.cc.dump phase=01 slot=second reg=DriveFilter C0=0x13 C1=0xF0 C2=0x08 C3=0x00 C4=0x00
p1.cc.dump phase=02 slot=first  reg=Attenuator  C0=0x15 C1=0x00 C2=0x00 C3=0x00 C4=0x5F
p1.cc.dump.annot   reg=Attenuator phase=02 slot=first C2.bit6=puresignal_run=0 C4=0x5F tx_step_attn=31(=txatt 0)
p1.cc.dump phase=02 slot=second reg=TxFreq      C0=0x03 C1=0x01 C2=0xB1 C3=0x59 C4=0x80
p1.cc.dump phase=03 slot=first  reg=TxFreq      C0=0x03 C1=0x01 C2=0xB1 C3=0x59 C4=0x80
p1.cc.dump phase=03 slot=second reg=Config      C0=0x01 C1=0x02 C2=0xC0 C3=0x00 C4=0x04
p1.cc.dump phase=00 slot=first  reg=TxFreq      C0=0x03 C1=0x01 C2=0xB1 C3=0x59 C4=0x80
p1.cc.dump phase=00 slot=second reg=RxFreq      C0=0x05 C1=0x01 C2=0xB1 C3=0x59 C4=0x80
p1.cc.dump.end psOn=False mox=True
```

Unique register payloads (de-duplicated; `TxFreq`/`RxFreq*` are
freq-only and identical across phases):
- `TxFreq`      `C0=0x03 C1=0x01 C2=0xB1 C3=0x59 C4=0x80`
- `RxFreq`      `C0=0x05 C1=0x01 C2=0xB1 C3=0x59 C4=0x80`
- `DriveFilter` `C0=0x13 C1=0xF0 C2=0x08 C3=0x00 C4=0x00`
- `Attenuator`  `C0=0x15 C1=0x00 C2=0x00 C3=0x00 C4=0x5F`  ← C2.bit6 (puresignal_run) = 0
- `Config`      `C0=0x01 C1=0x02 C2=0xC0 C3=0x00 C4=0x04`

#### Burst B — `psOn=True, mox=True, psArmed=True`, sweepLen=16

```
p1.cc.dump.start trigger=mox-edge psOn=True mox=True psArmed=True board=HermesLite2 sweepLen=16
p1.cc.dump phase=04 slot=first  reg=RxFreq      C0=0x05 C1=0x01 C2=0xB1 C3=0x59 C4=0x80
p1.cc.dump phase=04 slot=second reg=RxFreq3     C0=0x09 C1=0x01 C2=0xB1 C3=0x59 C4=0x80
p1.cc.dump phase=05 slot=first  reg=RxFreq2     C0=0x07 C1=0x01 C2=0xB1 C3=0x59 C4=0x80
p1.cc.dump phase=05 slot=second reg=RxFreq4     C0=0x0B C1=0x01 C2=0xB1 C3=0x59 C4=0x80
p1.cc.dump phase=06 slot=first  reg=AdcRouting  C0=0x1D C1=0x04 C2=0x00 C3=0x00 C4=0x00
p1.cc.dump.annot   reg=AdcRouting phase=06 slot=first C1=0x04 cntrl1 RX0->ADC0 RX1->ADC1 RX2->ADC0 RX3->ADC0 C3=0x00 (tx_step_attn duplicate, mi0bot writes; Zeus=0)
p1.cc.dump phase=06 slot=second reg=TxFreq      C0=0x03 C1=0x01 C2=0xB1 C3=0x59 C4=0x80
p1.cc.dump phase=07 slot=first  reg=Config      C0=0x01 C1=0x02 C2=0xC0 C3=0x00 C4=0x1C
p1.cc.dump phase=07 slot=second reg=TxFreq      C0=0x03 C1=0x01 C2=0xB1 C3=0x59 C4=0x80
p1.cc.dump phase=08 slot=first  reg=TxFreq      C0=0x03 C1=0x01 C2=0xB1 C3=0x59 C4=0x80
p1.cc.dump phase=08 slot=second reg=RxFreq3     C0=0x09 C1=0x01 C2=0xB1 C3=0x59 C4=0x80
p1.cc.dump phase=09 slot=first  reg=RxFreq      C0=0x05 C1=0x01 C2=0xB1 C3=0x59 C4=0x80
p1.cc.dump phase=09 slot=second reg=RxFreq4     C0=0x0B C1=0x01 C2=0xB1 C3=0x59 C4=0x80
p1.cc.dump phase=10 slot=first  reg=Attenuator  C0=0x15 C1=0x00 C2=0x40 C3=0x00 C4=0x5F
p1.cc.dump.annot   reg=Attenuator phase=10 slot=first C2.bit6=puresignal_run=1 C4=0x5F tx_step_attn=31(=txatt 0)
p1.cc.dump phase=10 slot=second reg=RxFreq3     C0=0x09 C1=0x01 C2=0xB1 C3=0x59 C4=0x80
p1.cc.dump phase=11 slot=first  reg=TxFreq      C0=0x03 C1=0x01 C2=0xB1 C3=0x59 C4=0x80
p1.cc.dump phase=11 slot=second reg=DriveFilter C0=0x13 C1=0x90 C2=0x08 C3=0x00 C4=0x00
p1.cc.dump phase=12 slot=first  reg=RxFreq2     C0=0x07 C1=0x01 C2=0xB1 C3=0x59 C4=0x80
p1.cc.dump phase=12 slot=second reg=RxFreq3     C0=0x09 C1=0x01 C2=0xB1 C3=0x59 C4=0x80
p1.cc.dump phase=13 slot=first  reg=RxFreq4     C0=0x0B C1=0x01 C2=0xB1 C3=0x59 C4=0x80
p1.cc.dump phase=13 slot=second reg=TxFreq      C0=0x03 C1=0x01 C2=0xB1 C3=0x59 C4=0x80
p1.cc.dump phase=14 slot=first  reg=AdcRouting  C0=0x1D C1=0x04 C2=0x00 C3=0x00 C4=0x00
p1.cc.dump.annot   reg=AdcRouting phase=14 slot=first C1=0x04 cntrl1 RX0->ADC0 RX1->ADC1 RX2->ADC0 RX3->ADC0 C3=0x00 (tx_step_attn duplicate, mi0bot writes; Zeus=0)
p1.cc.dump phase=14 slot=second reg=RxFreq3     C0=0x09 C1=0x01 C2=0xB1 C3=0x59 C4=0x80
p1.cc.dump phase=15 slot=first  reg=Config      C0=0x01 C1=0x02 C2=0xC0 C3=0x00 C4=0x1C
p1.cc.dump phase=15 slot=second reg=RxFreq4     C0=0x0B C1=0x01 C2=0xB1 C3=0x59 C4=0x80
p1.cc.dump phase=00 slot=first  reg=TxFreq      C0=0x03 C1=0x01 C2=0xB1 C3=0x59 C4=0x80
p1.cc.dump phase=00 slot=second reg=RxFreq3     C0=0x09 C1=0x01 C2=0xB1 C3=0x59 C4=0x80
p1.cc.dump phase=01 slot=first  reg=TxFreq      C0=0x03 C1=0x01 C2=0xB1 C3=0x59 C4=0x80
p1.cc.dump phase=01 slot=second reg=RxFreq4     C0=0x0B C1=0x01 C2=0xB1 C3=0x59 C4=0x80
p1.cc.dump phase=02 slot=first  reg=Attenuator  C0=0x15 C1=0x00 C2=0x40 C3=0x00 C4=0x5F
p1.cc.dump.annot   reg=Attenuator phase=02 slot=first C2.bit6=puresignal_run=1 C4=0x5F tx_step_attn=31(=txatt 0)
p1.cc.dump phase=02 slot=second reg=TxFreq      C0=0x03 C1=0x01 C2=0xB1 C3=0x59 C4=0x80
p1.cc.dump phase=03 slot=first  reg=TxFreq      C0=0x03 C1=0x01 C2=0xB1 C3=0x59 C4=0x80
p1.cc.dump phase=03 slot=second reg=DriveFilter C0=0x13 C1=0x90 C2=0x08 C3=0x00 C4=0x00
p1.cc.dump.end psOn=True mox=True
```

Unique register payloads (de-duplicated):
- `TxFreq`      `C0=0x03 C1=0x01 C2=0xB1 C3=0x59 C4=0x80`
- `RxFreq`      `C0=0x05 C1=0x01 C2=0xB1 C3=0x59 C4=0x80`
- `RxFreq2`     `C0=0x07 C1=0x01 C2=0xB1 C3=0x59 C4=0x80`
- `RxFreq3`     `C0=0x09 C1=0x01 C2=0xB1 C3=0x59 C4=0x80`
- `RxFreq4`     `C0=0x0B C1=0x01 C2=0xB1 C3=0x59 C4=0x80`
- `AdcRouting`  `C0=0x1D C1=0x04 C2=0x00 C3=0x00 C4=0x00`  ← C3=0; mi0bot writes tx_step_attn duplicate here
- `DriveFilter` `C0=0x13 C1=0x90 C2=0x08 C3=0x00 C4=0x00`
- `Attenuator`  `C0=0x15 C1=0x00 C2=0x40 C3=0x00 C4=0x5F`  ← C2.bit6 (puresignal_run) = 1
- `Config`      `C0=0x01 C1=0x02 C2=0xC0 C3=0x00 C4=0x1C`

#### Diff PS-off → PS-on (the headline)

| Register | Field | PS-off | PS-on | Δ |
|----------|-------|--------|-------|---|
| Attenuator  | C2 | 0x00     | 0x40     | bit 6 set (puresignal_run, expected on PS arm) |
| DriveFilter | C1 | **0xF0** | **0x90** | bits 5 and 6 cleared (`0b1111_0000` → `0b1001_0000`) |
| Config      | C4 | **0x04** | **0x1C** | bits 2, 3, 4 set (`0b0000_0100` → `0b0001_1100`) |

Unchanged between captures (worth noting because they bracket the diff):
- All `TxFreq` / `RxFreq*` payloads (28.400 MHz on both LO chains).
- `Attenuator` C4 = `0x5F` = `0x40 | 31` → tx_step_attn = 31, txatt = 0
  (M1's MOX-side encoding is identical in both captures — M1 is doing
  what it was supposed to).
- `AdcRouting` C1 = `0x04` (RX0→ADC0, RX1→ADC1, RX2→ADC0, RX3→ADC0).
- `AdcRouting` C3 = `0x00` (mi0bot writes a tx_step_attn duplicate
  here per the audit's M4 candidate; Zeus writes 0 in both captures
  — so this is invariant of PS state, not the toggling bit).
- `DriveFilter` C2 = `0x08`, C3 = `0x00`, C4 = `0x00`.

#### Round-robin phase difference

Burst A (PS off, sweepLen=4) cycles phases `01 → 02 → 03 → 00`. Burst
B (PS on, sweepLen=16) cycles `04 → 05 → … → 15 → 00 → 01 → 02 → 03`.
Phase numbers correspond to `Protocol1Client.PhaseRegisters`
round-robin slots; expanding from 4 to 16 phases adds the PS-related
register slots (extra `RxFreq2/3/4`, `AdcRouting`, plus a second copy
of `Attenuator` / `Config` / `DriveFilter` / `TxFreq`) into the
rotation. This is by design — it's the mechanism that makes
`puresignal_run` live on the wire when PS is armed.

#### Bench safety

Two short MOX windows (~1.5–2.5 s each); both `p1.cc.dump.end` lines
present, capture windows closed cleanly. Final state: PS disarmed
(`psEnabled=false`), TUN off, two-tone off, VFO 28.400. Antenna
constraint respected.

**Per fixer's instruction, no fix speculation here.** The three Δ rows
above are handed to team-lead / mi0bot-spec for side-by-side comparison
against the canonical mi0bot register table. The DriveFilter C1 and
Config C4 differences in particular are large enough to plausibly
explain the PA mute, but that is the auditor's call to make against
the mi0bot reference.

### 2026-05-03 — wire-side C&C dump v3 (controlled, single-lifetime)

After fixer fixed the trigger-arm bug (the previous "multi-shot" was
silently consuming edges that fired during an in-progress sweep) and
added a `p1.cc.dump.heartbeat` 1 Hz log, both invariance-controlled
captures fit into a single backend lifetime. Heartbeats made one
diagnostic instantly visible: when the TX-IQ ring was wedged (the
init quirk), the heartbeat reported `psOn=False mox=False` even while
I'd just armed PS via `/api/tx/ps`. A second `/api/disconnect` +
`/api/connect` cycle unstuck the consumer (`recentMag` 0 → 32767),
heartbeats started tracking actual API state, and the multi-shot dump
fired cleanly on every subsequent edge.

This run captured 8 dumps in one backend lifetime: ring-verify
(seq=1, 2), Burst A (seq=3 ps-edge arm, **seq=4 PS-on MOX (broken)**,
seq=5 PS-on un-MOX), ps-edge disarm (seq=6), Burst B
(**seq=7 PS-off MOX (control)**, seq=8 PS-off un-MOX). Drive sliders
untouched throughout (PA byte=240 from `pa.recompute pct=100` —
slider was at 100 % from a prior session, never adjusted). VFO 28.400
USB.

**Power readings (FwdW via TxMetersV2Frame sniffer on /ws):**

| seq | label | psOn | mox | FwdW | RefW | SWR |
|-----|-------|------|-----|------|------|-----|
| 4 | Burst A — PS-on MOX (broken) | true | true | **0.0000155 W** | 0.0000139 W | 1.0 |
| 7 | Burst B — PS-off MOX (control) | false | true | **5.515 W** | 0.233 W | 1.52 |

**Same backend lifetime, identical drive level, ~356 000× drop on
PS-arm.** This conclusively answers team-lead's invariance question:
the mute is not a PA-recompute artifact, not a backend-restart
artifact. It's a real bypass triggered solely by `psEnabled=true`.

**Wire-side diff (seq=4 PS-on vs seq=7 PS-off, single-lifetime):**

| Register | Field | seq=7 PS-off | seq=4 PS-on | Δ |
|----------|-------|--------------|-------------|---|
| Attenuator  | C2 | `0x00` | `0x40` | bit 6 set — **puresignal_run** (expected, mi0bot 0x0a[22]) |
| DriveFilter | C1 | `0xF0` | `0xF0` | **no change** ← differs from v1 dump |
| Config      | C4 | `0x04` | `0x1C` | bits 2, 3, 4 set (`0b0000_0100` → `0b0001_1100`) |

**Important correction to the v1 dump entry above:** the
`DriveFilter C1: 0xF0 → 0x90` diff reported earlier was a stale-state
/ wedged-ring artifact across two backend lifetimes, **not a real
PS-arm-induced wire change**. In the controlled single-lifetime
capture here, DriveFilter C1 is `0xF0` in both PS-on and PS-off MOX.
Treat the v1 DriveFilter row as superseded.

That leaves **Config C4 (`0x04` → `0x1C`)** as the only unexplained
diff when PS is armed — `Attenuator C2 = 0x40` (puresignal_run) is
the intended signal, not the bug.

**The mi0bot-spec / auditor question becomes very narrow:**

> When `puresignal_run` is asserted, what bits does mi0bot write to
> register 0x00 (Config) C4? Specifically, are bits 2, 3, 4 supposed
> to be set, or is Zeus erroneously OR-ing them in?

Notable identical bytes (none change between PS-on and PS-off in the
controlled capture):
- `Attenuator C4 = 0x5F` (M1's `0x40 | 31` MOX-side encoding stable)
- `AdcRouting  C1 = 0x04` (RX0→ADC0, RX1→ADC1, RX2→ADC0, RX3→ADC0)
- `AdcRouting  C3 = 0x00` (mi0bot's tx_step_attn-duplicate slot —
  Zeus writes 0; not toggling, but the audit's M4 candidate that
  Zeus may need to set; orthogonal to the mute)
- `DriveFilter C2 = 0x08` (PA-enable bit)
- All `TxFreq` / `RxFreq*` payloads.

#### Heartbeat diagnostic (the bonus from fixer's fix)

When the consumer ring was wedged after the first connect, the TX-loop
heartbeat reported `psOn=False mox=False lastPs=False lastMox=False`
through every action (arm PS, TUN ON, etc.) until I disconnected and
reconnected a second time. Once unstuck, the heartbeat tracked actual
state — e.g. during seq=4 it correctly reads
`psOn=True mox=True lastPs=True lastMox=True remain=0 seq=4`.
**This is exactly the visibility we needed**: a heartbeat with stuck
`psOn=False mox=False` while the API shows otherwise is the
unambiguous fingerprint of the wedged TX consumer, and any future
bench-tester now has a one-grep way to diagnose it.

#### Bench safety

3 short MOX windows (~1.5 s each); UN-MOX confirmed by `FwdW=0`
between every step (and again after the final UN-TUN). Final state:
PS disarmed (`psEnabled=false`), TUN off, two-tone off, VFO 28.400 —
antenna constraint respected throughout.

**Per fixer's instruction, no fix speculation.** Single Δ for
mi0bot-spec to chase: `Config C4: 0x04 → 0x1C` when PS is armed.

### 2026-05-03 — convergence discriminator capture (PS-on MOX)

Operator reports PA mute is fixed. Now investigating why PS still
isn't applying corrections. Captured the auditor's four-line
discriminator during a 10 s PS-on TUN burst on 28.400 MHz USB,
DRV=21%, TUN=59%, drv byte=240 (PA at full).

**API-state sampling during the burst:**

| t | psCalState | psFeedbackLevel | psCorrecting |
|---|------------|-----------------|--------------|
| t=1..10 s | 4 (COLLECT) | 0 | false |
| post un-MOX | 1 (WAIT) | 0 | false |

State machine never advanced past LCOLLECT. With Correcting=false
and CalState stuck at 4, we can infer info[5] CalibrationAttempts
also stayed 0 (no calc() invocations) — `wdsp.psState` log format
doesn't expose info5 directly.

**1) `tx.peaks` — count: 0 lines during burst**

Not present. `tx.peaks` is logged from `TxAudioIngest.cs:289`, the
mic / audio ingest path. Under TUN (which engages WDSP's PostGen
tone internally — bypassing mic ingest), this line is expected to
be silent. So this is "n/a" for the matrix, not a data point.

**2) `p1.tx.rate` (wire-side TX byte rate, ~1 Hz, 23 lines during burst):**

```
p1.tx.rate pkts=381 in 1000ms = 381 pkt/s (target 381) | wire: peak=32767/32767 mean=16383 firstI=32767 firstQ=0 drv=240
... (10 lines at peak=32767 during MOX) ...
p1.tx.rate ... wire: peak=0/32767 mean=0 firstI=0 firstQ=0 drv=240   (post un-MOX)
```

TX IQ is hitting the wire at **full scale (peak=32767)** for the
entire MOX window. `firstI=32767, firstQ=0` is constant — that's
the TUN tone (a steady carrier on the I axis). Packet rate
381 pkt/s matches the protocol target. drv byte=240 (PA on).
**Wire-side TX is healthy.**

**3) `p1.ps.fb` (PS-feedback parser output, ~1 Hz, 11 lines during burst):**

```
p1.ps.fb DDC2(rx) peak=0.2252 mean=0.1341 | DDC3(tx) peak=0.2340 mean=0.1354 | blocks=2850
p1.ps.fb DDC2(rx) peak=0.2241 mean=0.1338 | DDC3(tx) peak=0.2340 mean=0.1354 | blocks=3040
... (steady through 4750 blocks) ...
p1.ps.fb DDC2(rx) peak=0.2231 mean=0.1330 | DDC3(tx) peak=0.2340 mean=0.1354 | blocks=4750
```

**Both DDC2 and DDC3 are now non-zero with realistic envelope.**
DDC2(rx) peak ~0.223 mean ~0.133. DDC3(tx) peak 0.234 mean 0.135.
Block counter advances at the expected ~190 blocks/sec
(192 kHz / 1024 sample). **Feedback samples ARE being parsed
correctly off the wire — completely different from the v1 dump
which showed both peaks ≈ 0.0001.** The parser/wire side of PS
feedback is healthy now.

**4) `wdsp.psState` (edge-triggered, 2 lines during burst):**

```
wdsp.psState 1->4 info4=0 info6=0x0000 info13=0 info14=0    (TUN ON)
wdsp.psState 4->1 info4=0 info6=0x0000 info13=0 info14=0    (TUN OFF)
```

State transitioned WAIT → COLLECT on TUN ON, COLLECT → WAIT on
TUN OFF. **`info4=0` (FeedbackLevel) throughout the entire 10 s
window** — even though `p1.ps.fb` shows DDC2 peak ≈ 0.22 reaching
the parser. `info14=0` (Correcting) — calcc never started
applying a correction curve.

**Mapping to the auditor's discriminator table:**

| `tx.peaks iq` | `p1.tx.rate peak` | `p1.ps.fb DDC2 peak` | wdsp info4 | Diagnosis |
|---------------|-------------------|----------------------|------------|-----------|
| n/a (TUN bypasses mic ingest) | **32767/32767** | **0.223** | **0** | **Samples reach the parser but don't reach calcc — gap between Protocol1Client and WDSP's pscc** |

This is a new discriminator state, sharper than any of the
"All flowing / not flowing" branches in team-lead's table:

> Samples enter `Protocol1Client.PsFeedbackFrames` correctly →
> `DspPipelineService.StartPsFeedbackPumpP1` reads them →
> `engine.FeedPsFeedbackBlock(txI, txQ, rxI, rxQ)` is called
>   (`wdsp.pscc fed N blocks` lines were firing in earlier sessions
>   at the right cadence — not re-checked this burst, but the
>   underlying flow is the same code) →
> but WDSP's calcc envelope detector reads zero (`info4 = 0`).

This matches mi0bot-spec's earlier "pscc tx_buf source bug"
hypothesis closely — the values being passed into
`FeedPsFeedbackBlock`'s `txI / txQ / rxI / rxQ` arguments are
likely either zero (e.g. wrong source array), swapped, or scaled
wrong. The auditor / mi0bot-spec should compare what mi0bot
pushes into `psccF` vs what Zeus passes at the
`WdspDspEngine.FeedPsFeedbackBlock` seam.

A small instrumentation experiment that would settle it: add a
`wdsp.pscc.in` log at the entry of
`WdspDspEngine.FeedPsFeedbackBlock` reporting peak/mean of the
four float arrays it received. If those peak/mean values match
`p1.ps.fb DDC2/DDC3` (≈ 0.22), the samples are reaching WDSP
correctly and the bug is inside WDSP/calcc input-mapping. If they
read zero, the bug is in the pump between `PsFeedbackFrames` and
`FeedPsFeedbackBlock`.

#### Bench safety

Single 10 s MOX window via TUN. UN-MOX confirmed by post-burst
state (`twoTone=false`, `psCalState=1`). Final state: PS disarmed,
TUN off, VFO 28.400 — antenna constraint respected throughout.

**Conclusion:** the PS feedback path now delivers real envelope
samples to the Protocol1Client parser (DDC2/DDC3 peaks ~0.22)
but they don't register in WDSP's calcc envelope detector
(`info4 = 0` consistently). The seam to instrument next is
`WdspDspEngine.FeedPsFeedbackBlock` arguments at the entry point.
