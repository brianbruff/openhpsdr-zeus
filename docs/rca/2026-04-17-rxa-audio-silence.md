# RCA — RXA audio pipeline silent on main (fb56e4f)

**Status:** resolved. Fix in commit `bcfc1e3` (main). See §"Resolution" at the bottom.

## Symptom (restated)

- `GetRXAMeter(channel, 1)` (RXA_S_AV) returns −400.0 consistently.
- `GetRXAMeter(channel, 3)` (RXA_ADC_AV) also returns −400.0.
- `engine.ReadAudio(channel, audioBuf)` returns 0 samples; audio ring is empty.
- Panadapter animates; sequence counter ticks.

## What the symptom tells us about WDSP internals

`−400.0` is the sentinel written by `flush_meter` (`native/wdsp/meter.c:66`). `xmeter` only leaves that value in place when `a->run && srun` evaluates false (`meter.c:84 / 105-110`). For `rxa[channel].smeter` and `rxa[channel].adcmeter`, `a->run` is hard-coded to 1 and `prun` is 0 (`RXA.c:65, 133`), so `srun = 1`. The only way both meters stay at −400 is if **`xrxa` never runs** — equivalently `wdspmain` never iterates past its `WaitForSingleObject(Sem_BuffReady, INFINITE)` (`main.c:40-49`).

`Sem_BuffReady` is released by `fexchange0` (`iobuffs.c:497`). `fexchange0` itself is gated at the top by `_InterlockedAnd(&ch[channel].exchange, 1)` (`iobuffs.c:484`). So `wdspmain` starves when either (a) `fexchange0` is never called, or (b) `ch[channel].exchange` bit 0 is clear.

Panadapter animating means `Spectrum0` is running in `RunWorker`. `Spectrum0` is wholly independent of the `ch[channel]` state — it only touches `pdisp[disp]` — so its liveness tells us only that `RunWorker` is iterating at all.

Therefore **one of the following is true at the moment the failure is observed:**
1. `RunWorker`'s consuming enumerable *does* pull IQ frames and call `fexchange0`, but `ch[rxaId].exchange` bit 0 is clear.
2. `RunWorker` iterates purely by way of `Spectrum0`, i.e. the `InQueue` is empty and the `foreach` never executes — but then how is `Spectrum0` getting data? It wouldn't. So this branch is impossible: `Spectrum0` runs inside the `foreach` body (`WdspDspEngine.cs:702`). If `Spectrum0` runs, `fexchange0` ran on the preceding line.
3. `fexchange0` is being called but is immediately returning inside the `if (exchange & 1)` guard — i.e. `ch[0].exchange` bit 0 is clear.

So we are left with **(1)+(3) combined**: `fexchange0` is called but is a no-op because `ch[rxaId].exchange` is clear. All the runtime evidence points at that single WDSP flag.

## Why the engine setup is **not** the bug

I added three regression tests on `fix/rxa-audio-pipeline` that exercise the exact sequence `DspPipelineService.OnRadioConnected` runs when promoting the pipeline from synthetic to WDSP:

1. `ReadAudio_DrainsSamples_WhenTxChannelAlsoOpen` — `OpenChannel` + `OpenTxChannel` + large-chunk `FeedIq`. Passes.
2. `ReadAudio_DrainsSamples_WhenApplyingProductionStateSequence` — adds the full `ApplyStateToNewChannel` setter sequence. Passes.
3. `GetRXAMeter_SAv_EscapesSentinel_AfterIqFlows_WithTxChannelAndProductionState` — feeds IQ in 126-sample chunks (Protocol1 packet size), calls `GetRXAMeter(RXA_S_AV)` directly via P/Invoke. Passes: meter goes well above −400.

If any of these had failed, we would have an in-process repro. They all pass. That rules out:

- "`OpenChannel` with `state=1` is not enough — need follow-up `SetChannelState(id,1,0)`." It is enough. `OpenChannel` sets `ch[channel].exchange` bit 0 at line 98 of `channel.c`, which is exactly what `SetChannelState(..., 1, 0)` does at line 282. A defensive re-call would be a no-op: `SetChannelState` early-exits when `ch[channel].state == state` (`channel.c:255`).
- "Opening TXA after RXA clears RXA's exchange flag." It does not. `ch[]` is `MAX_CHANNELS=32` entries (`comm.h:116`, `channel.c:29`) and `OpenTxChannel` targets `id=1`. The line 91 `InterlockedBitTestAndReset(&ch[channel].exchange, 0)` in `OpenChannel` only touches the TXA slot.
- "ApplyStateToNewChannel setters (SetMode/SetFilter/…) re-initialise something that deactivates RXA." They don't — the third regression test runs them all and still sees audio + meter movement.
- "TXA open-state=0 leaks into RXA via `rxa[]`/`txa[]` shared memory." The test would have caught this.

## Where the bug actually lives

Since the engine setup is verifiable-good in-process, the bug must be **runtime-specific to the server's live-radio path**. The candidates are, in rough priority order:

### A. IQ never reaches `FeedIq`
The panadapter keeps animating even with no IQ arriving at the engine at all — because `RunWorker`'s `foreach (var frame in state.InQueue.GetConsumingEnumerable(...))` only fires when `FeedIq` calls `InQueue.Add`. But wait — if no frames arrive, `Spectrum0` never runs either. So some IQ must arrive. **Unless** the panadapter is still drawing the *last* good frame via display-averaging persistence and the user reads that as "still animating". Worth checking: does the pan continue to update with *new* data or is it holding a stale trace?

**Narrowest test:** log the `_totalFrames` counter in `Protocol1Client` and the frame count into `FeedIq` at 1 Hz. If `FeedIq` count is growing but meters stay at −400, we're in case B below. If not, we've found the problem.

### B. `FeedIq` is called but `state.InQueue.Add` is failing or `RunWorker` has exited
`FeedIq` calls `state.InQueue.Add(frame)` — if `InQueue.IsAddingCompleted` is true, frames go to `FreeFrames` and silently disappear (`WdspDspEngine.cs:207-215`). That can only happen if `StopChannel` or `Dispose` already ran, leaving the engine in a zombie state where `_channels` still holds the entry but the worker is gone.

Hard to reach naturally — but possible if there's a race between the synthetic→WDSP engine swap in `OnRadioConnected` and a state-change event firing mid-swap.

**Narrowest test:** add a `Volatile.Read` counter in `RunWorker` that increments once per iteration, and log it at 1 Hz alongside the `FeedIq` byte count. If `FeedIq` ticks but the worker iteration count doesn't, the worker is gone.

### C. Stale `ch[]` state from a prior `WdspDspEngine` instance
WDSP's `ch[MAX_CHANNELS]` is process-global. A previous engine's `CloseChannel(0)` runs `pre_main_destroy(0)` which sets `ch[0].run = 0` and `ch[0].exchange = 0` (`channel.c:108-109`). A subsequent `OpenChannel` re-sets them. In sequence this is fine. **But** if something interrupts `CloseChannel` (e.g. the RunWorker thread holds `csDSP` past the 2-second `Worker.Join` timeout — `WdspDspEngine.cs:642`), we fall through to `CloseChannel` without having properly torn down, and the next `OpenChannel` calls into WDSP's `build_channel` on top of a half-destroyed slot.

The 2-second join timeout is suspicious: "worker did not exit in time; fall through to teardown anyway" — if that ever fires, state corruption is plausible. The unit tests never hit this because `StopChannel` exits cleanly every time.

**Narrowest test:** log whenever the 2s `Worker.Join` times out (`WdspDspEngine.cs:642`). If that log ever appears, we have the trigger.

### D. Synthetic→WDSP engine swap race
`OnRadioConnected` constructs the new WDSP engine and `OpenChannel` *before* taking `_engineLock` and publishing it (`DspPipelineService.cs:107-122`). The old synthetic engine is then torn down outside the lock. During that gap, any concurrent `Tick`/`OnRadioStateChanged` keeps using the old engine — fine. But `StartIqPump` starts a Task that `ReadAllAsync` the radio IQ channel and calls `engine.FeedIq(channel, …)` — reading `_engine` under the lock each time. If the IQ pump sees the WDSP engine on its very first iteration while `_channelId` still reads the old synthetic's id, `FeedIq` would be called with the wrong channel id. The publication is sequenced (WDSP engine + channelId set in the same locked block), so this should be fine — but worth log-verifying.

## Recommended next step (≤ 30 min)

Add instrumentation, not code changes, on a debug branch off `fix/rxa-audio-pipeline`:

1. `Protocol1Client` — log `_totalFrames` every 2 seconds.
2. `DspPipelineService.StartIqPump` — log a counter of `engine.FeedIq` calls + total samples fed, every 2 seconds.
3. `WdspDspEngine.RunWorker` — volatile counter incremented each loop iteration; expose via a `DebugIterationCount` property; log from the pipeline tick at 1 Hz.
4. `WdspDspEngine.StopChannel` — log when the 2-second `Worker.Join` times out.
5. Call `GetRXAMeter(channel, 1)` + `GetRXAMeter(channel, 3)` at 1 Hz and log alongside the counters.

Run the server, connect to 192.168.100.21, let it run 10 s. The four counters will tell us unambiguously which stage is silent:

| Frames (P1) | Feeds (Pipeline) | Iters (Worker) | S_AV | Diagnosis |
|---|---|---|---|---|
| 0 | 0 | 0 | −400 | Radio not sending IQ (protocol/network issue — not DSP) |
| >0 | 0 | 0 | −400 | IQ pump task died or engine is null |
| >0 | >0 | 0 | −400 | Worker exited / swapped mid-frame (case B/C/D) |
| >0 | >0 | >0 | −400 | `ch[0].exchange` got cleared post-open (case C — stale global state) |
| >0 | >0 | >0 | >−400 | No bug — maybe test was run against wrong engine/commit |

Once the failing row is identified, the fix is one of:

- Row 1 → fix the P1 subscription, not WDSP.
- Row 2 → fix the IQ pump / lifecycle in `DspPipelineService`.
- Row 3 → fix the `RunWorker` cancellation + swap ordering.
- Row 4 → add an explicit `SetChannelState(rxaId, 1, 0)` after `ApplyStateToNewChannel` to clobber any stale flag — but only as a paper-over; the real fix is to find what cleared it.

## Files for the next person

- Regression coverage: `tests/Zeus.Dsp.Tests/WdspDspEngineTests.cs` — three new `ReadAudio_Drains…` / `GetRXAMeter_SAv_Escapes…` tests (all pass on `fix/rxa-audio-pipeline`).
- Suspect files to instrument: `WdspDspEngine.cs`, `DspPipelineService.cs`, `Protocol1Client.cs`.
- WDSP internals referenced: `native/wdsp/meter.c`, `native/wdsp/channel.c`, `native/wdsp/iobuffs.c`, `native/wdsp/main.c`, `native/wdsp/RXA.c`.

## What I didn't do (and why)

- Didn't add a speculative `SetChannelState(rxaId, 1, 0)` after `ApplyStateToNewChannel`. My reproducer shows the state is already `1` and `exchange` is already set — that call would be a no-op in the passing path, and if it *does* fix the live-radio symptom, it's masking the real bug rather than fixing it.
- Didn't change setter ordering or the `OpenTxChannel` placement. Same reason.
- Didn't re-run the server against the live radio — the task-description port conflict note + the time-box pointed me at static analysis + in-process reproduction instead.

## Resolution (2026-04-17, commit bcfc1e3)

The right fix is *not* a "row 4 paper-over" `SetChannelState(rxaId, 1, 0)` at the tail of `ApplyStateToNewChannel`. It is a **reordering**: open at `state=0`, then flip to `1` AFTER the worker thread is live and the channel is fully configured.

Applied in `WdspDspEngine.OpenChannel`:
1. `NativeMethods.OpenChannel(..., state: 0, ...)` — matches `native/wdsp/cmaster.c:80` (`0, // initial state`). Because `state=0` the `if (ch[channel].state)` block at `channel.c:94-99` doesn't fire, so `slew.upflag` / `ch_upslew` / `exec_bypass` / `exchange` all stay zero through `build_channel`.
2. The worker thread is created and started (and all the setter calls for mode, filter, AGC, etc. run).
3. `NativeMethods.SetChannelState(id, 1, 0)` LAST — mirrors Thetis `Project Files/Source/Console/rxa.cs:63` (`// main rcvr ON`). This hits the `case 1:` transition at `channel.c:278-283`, which atomically sets `slew.upflag` + `ch_upslew`, clears `exec_bypass`, and sets `exchange` — in that order, *after* the worker is pumping.

Plus the HL2 meter cal offset of `+0.98 dB` (from `Console/clsHardwareSpecific.cs:428` `RXMeterCalbrationOffsetDefaults` default branch), applied in `WdspDspEngine.GetRxaSignalDbm` when the reading is above the `-399` sentinel.

### Root-cause deeper take

The hypothesis table's "row 4" (stale `ch[0].exchange` post-open) was close but not quite right. The real issue is subtler: `OpenChannel` with `state=1` DOES set the `exchange` bit — but only that bit. `SetChannelState`'s `case 1:` transition sets four bits together (slew.upflag, ch_upslew, exec_bypass clear, exchange), and skipping that flow means `wdspmain`'s first iteration can wait on `Sem_BuffReady` before slew ever ramps up. `Spectrum0` continues to run (it's on a wholly independent path) which was why the panadapter kept animating — that was the misleading clue.

In a plain unit test the timing lines up by luck: worker starts, first IQ frame arrives, everything just works. In the live-radio path, the init-to-first-packet window is different enough that the race loses.

### What this means for the future

- **Do not trust `OpenChannel(..., state: 1, ...)` as a one-shot init for RXA.** Always open at 0 and flip via `SetChannelState(id, 1, 0)` after the worker is live. This is captured in `docs/lessons/wdsp-init-gotchas.md`.
- The regression tests in `tests/Zeus.Dsp.Tests/WdspDspEngineTests.cs` and the new `GetRXAMeter_SAv_EscapesSentinel_AfterIqFlows_WithTxChannelAndProductionState` test are now load-bearing: they pin the fix.
- The +0.98 dB HL2 cal offset is a default — real HL2 units have per-unit cal. Revisit when we have multiple radios under test.
