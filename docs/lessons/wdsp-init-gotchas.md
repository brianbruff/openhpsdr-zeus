# WDSP channel & meter init gotchas

Load-bearing patterns for anyone touching `Nereus.Dsp.Wdsp.WdspDspEngine` or the channel lifecycle in `native/wdsp/`. These are costly to rediscover — bookmark this file if you're doing meter / TXA / lifecycle work.

## TL;DR

When opening a WDSP RXA channel:

1. `OpenChannel(..., state: 0, ...)` — **NOT** `state: 1`.
2. Start the worker thread, run all the configuration setters.
3. `SetChannelState(id, 1, 0)` ONCE, LAST.

Do not trust `OpenChannel(..., state: 1, ...)` alone — it's a time-bomb. See §Why below.

## The symptom

`GetRXAMeter(channel, RXA_S_AV)` returns `-400.0` forever. Audio ring empty. Panadapter animates normally (misleading).

## The cause

`SetChannelState`'s `case 1:` transition at `native/wdsp/channel.c:278-283` does **four** things atomically:

```c
InterlockedBitTestAndSet (&a->slew.upflag, 0);           // input slew ramp-up
InterlockedBitTestAndSet (&ch[channel].iob.ch_upslew, 0); // I/O gain ramp-up
InterlockedBitTestAndReset (&ch[channel].iob.pc->exec_bypass, 0); // enable DSP chain
InterlockedBitTestAndSet (&ch[channel].exchange, 0);      // enable fexchange0
```

`OpenChannel`'s line 94-99 block only sets `exchange`. The other three bits stay zero. `wdspmain` (`main.c:40-49`) waits on `Sem_BuffReady` — which IS released by `fexchange0` — but the DSP chain inside `xrxa` is gated by `exec_bypass`, and slew controls the input envelope. Without those flags set, the first few frames can race and the worker never produces audio.

In **unit tests** the timing lines up by luck: worker starts, first IQ frame arrives, everything works. In the **live-radio** path, the init-to-first-packet window is different enough that the race loses. (This is why the in-process regression tests passed for weeks while the live server silently delivered nothing — see `docs/rca/2026-04-17-rxa-audio-silence.md`.)

## The fix (already applied)

`WdspDspEngine.OpenChannel` follows the Thetis pattern:

```csharp
NativeMethods.OpenChannel(channel: id, ..., state: 0, ...);  // state=0
// ... configuration setters (SetRXAMode, SetRXAPanelRun, etc.) ...
// ... start worker thread ...
NativeMethods.SetChannelState(id, 1, 0);  // flip AFTER worker is live
```

References:
- Thetis `Project Files/Source/Console/rxa.cs:63` — `// main rcvr ON`.
- Nereus commit `bcfc1e3` — the fix.

## The `-400` sentinel

`xmeter` in `native/wdsp/meter.c:84-110`:

```c
if (a->run && srun) {
    // … compute averages …
    a->result[a->enum_av] = 10.0 * mlog10(a->avg + 1.0e-40);
} else {
    if (a->enum_av >= 0) a->result[a->enum_av] = -400.0;
}
```

For RXA S-meter and ADC-meter, `a->run` is hard-coded to 1 and `prun` is null (`RXA.c:65,133`). So `-400.0` means **xmeter itself didn't run**, which means `xrxa` didn't run, which means (almost always) `ch[channel].exchange` bit 0 is clear.

**Server policy** (`DspPipelineService.cs`): treat `dbm <= -399.0` as "real meter unavailable" and fall through to a secondary signal (currently audio-RMS, `dbfs - 50 dBm`). The SMeterLive client side doesn't need to know — the server always publishes a usable `rxDbm`.

## The 2-second `Worker.Join` timeout

`WdspDspEngine.StopChannel` joins the worker thread with a 2s timeout and falls through to teardown regardless ("worker did not exit in time; fall through to teardown anyway"). If this ever fires, `ch[]` global state (WDSP's `MAX_CHANNELS=32` process-global array) may be left half-destroyed, and subsequent `OpenChannel` calls can inherit stale flags. Instrument this in future debug sessions — if the 2s log ever appears, you have a trigger for state corruption.

## The HL2 meter calibration offset

`WdspDspEngine.GetRxaSignalDbm` applies `+0.98 dB` on top of WDSP's raw reading, matching Thetis `Console/clsHardwareSpecific.cs:428` `RXMeterCalbrationOffsetDefaults` default branch (ANAN 7000/8000 get `+4.84`, G2 gets `-4.48`, HL2 + everything else gets `+0.98`). Real HL2 units have per-unit cal drift measured in tenths of a dB — revisit when we have multiple radios to cross-reference.

## References

- RCA: `docs/rca/2026-04-17-rxa-audio-silence.md`
- Fix commit: `bcfc1e3` on master
- WDSP internals: `native/wdsp/meter.c:74-110`, `channel.c:78-99,249-288`, `iobuffs.c:478-500`, `main.c:40-49`, `RXA.c:63-145`
- Thetis `Console/rxa.cs:63`, `Console/dsp.cs:876-966`, `Console/clsHardwareSpecific.cs:414-430`
