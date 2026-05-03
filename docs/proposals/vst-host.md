# Zeus VST / Plugin Host — Architecture Decision Record

Status: accepted (Phase 1 in progress)
Branch: `VST-Experimental` (local prototype until Phase 1 SIGKILL gate passes)
Authors: KB2UKA, with maintainer (Brian, EI6LF) reserving final authority on
visual design, UX, and operator-visible defaults.

## 1. Context

This ADR closes the design loop on issue #106 (VST host) and clarifies its
relationship to #185 (Brian's broader plugin PRD). The bot's proposal under
#185 suggested a single in-process `AssemblyLoadContext` for all server-side
plugins. That is correct for native .NET widget / amp plugins but wrong for
VST: it forbids 32-bit plugin support on a 64-bit host and gives no crash
isolation when a malformed plugin faults. Both models will coexist — `.NET`
plugins inside `Zeus.Server` via ALC, and audio plugins (VST3, CLAP,
eventually VST2) in an out-of-process sidecar. The operator requirement is
"run all VST plugins, both 32-bit and 64-bit, on Windows / macOS / Linux."
Zeus is GPLv2-or-later (`LICENSE` at the repo root); every choice below is
constrained by that.

## 2. Decisions locked

- **Out-of-process sidecar, one per loaded plugin.** A 64-bit host
  cannot load a 32-bit plugin DLL/SO/dylib on any of the three
  platforms; per-plugin processes also give crash isolation, the
  load-bearing Phase 1 acceptance gate (SIGKILL the sidecar mid-TX,
  server stays up).
- **Sidecar stack.** Bare C++ with Steinberg `vst3sdk` (MIT since
  October 2025), the `CLAP SDK` (MIT), and the clean-room `Vestige`
  VST2 header (LGPL). JUCE is rejected — dual license incompatible
  with GPLv2+. The Steinberg VST2 SDK is rejected — Steinberg
  withdrew distribution rights for new hosts in 2024.
- **Per-architecture sidecar binaries.** Windows ships
  `zeus-plughost-x64` and `zeus-plughost-x86`; macOS and Linux ship
  `zeus-plughost-x64` and `zeus-plughost-arm64`. `Zeus.Server`
  probes plugin bitness / architecture and forks the matching
  sidecar.
- **GUI ownership.** Each plugin's editor opens as a top-level
  native window in the sidecar process. No HWND / NSView / X11
  reparenting into the web frontend. Zeus UI exposes "Edit" and
  "Bypass" toggles routed over the control channel.
- **TX insertion.** Inside `WdspDspEngine.ProcessTxBlock` under
  `_txaLock`, post-Leveler and **pre-CFC**. CFC must remain last.
- **RX insertion.** Inside `DspPipelineService.Tick`, between
  `engine.ReadAudio()` and `StreamingHub.Broadcast(...)`, under
  `AudioGate`. Phase 1 format: 48 kHz mono float32.
- **Bit-identical-when-OFF.** A single boolean per block
  short-circuits the entire plugin path — no memcpy, no resample,
  no IPC. Verified by a hash-equality unit test (chain disabled
  vs. chain absent).
- **Persistence.** Global save-on-change, matching the NR4 model
  from #79. No explicit save button.
- **Distribution.** Optional addon, downloaded separately, not
  bundled with the Zeus installer. Matches Brian's #185 framing.
- **Repo layout.** Sidecar lives in a separate repo, working title
  `openhpsdr-zeus-plughost`. Default license proposed GPLv2+ to
  match Zeus; flagged below for Brian's review.

## 3. IPC wire format (Phase 1)

**Audio plane.** Two SPSC lock-free shared-memory rings per plugin
instance, one per direction (host -> sidecar, sidecar -> host). Each
block begins with a 64-byte cache-line aligned header
`{ uint64 seq, uint32 frames, uint32 channels, uint32 sampleRate,
uint32 flags, byte[40] reserved }` followed by `frames * channels`
float32 samples in **planar** order (channel-major). Phase 1 fixes the
geometry at 256 frames @ 48 000 Hz mono. Ring depth is 8 blocks per
direction; under/over-runs are reported via the control plane.

**Control plane.** Win32 named pipe on Windows, Unix-domain socket
elsewhere, carrying length-prefixed CBOR messages. Phase 1 messages:
`Hello` (handshake — version + caps), `Goodbye` (graceful shutdown),
`Heartbeat` (1 Hz, both directions), `LogLine` (sidecar -> host).
Plugin-load and parameter-change messages are deferred to Phase 2.

**Wakeup primitive.** Linux `futex`, Win32 `NamedEvent`, Mach
semaphore on macOS — abstracted behind a single C++ class. The audio
thread does no other syscalls and never allocates or locks.

## 4. Phase plan

- **Phase 1 (current).** Skeleton — Linux x64 host code in
  `Zeus.Server`, Linux x64 sidecar binary, audio plane passes samples
  through unchanged (no plugin loaded). Acceptance: SIGKILL the
  sidecar mid-TX, server stays up, audio degrades gracefully (silence
  on the plugin slot, chain bypassed), sidecar relaunchable without
  restarting `Zeus.Server`.
- **Phase 2.** Load a VST3 plugin in the sidecar. Add control-plane
  lifecycle messages (`LoadPlugin`, `UnloadPlugin`, `SetParam`,
  `GetState`, `SetState`). Single-slot insert UI.
- **Phase 3.** Win64 and macOS arm64 sidecar binaries. CMake is
  cross-platform from day one; Linux only ships first because it is
  KB2UKA's dev box.
- **Phase 4.** CLAP host alongside VST3 in the same sidecar binary.
- **Phase 5.** Win32 sidecar binary for legacy 32-bit VST plugins,
  using the Vestige VST2 header. Satisfies the "all 32-bit plugins"
  requirement.
- **Phase 6.** Multi-slot chain UI — multiple inserts per chain, per
  chain bypass, named presets, persisted alongside band state.

## 5. Pending decisions for Brian's review

- Sidecar repo license — default GPLv2+ to match Zeus, but a permissive
  license (MIT) would broaden reuse by other HPSDR projects. Maintainer
  call.
- macOS Apple Silicon VST2 — Apple dropped Carbon, and Steinberg never
  shipped a Cocoa VST2 SDK; 32-bit VST2 on Apple Silicon is therefore
  unsupported. Confirm we accept that limit rather than chase a
  Rosetta-only x86_64 sidecar.
- LV2 (Linux-only) — currently deferred indefinitely.
- Browser-bridged GUI — deferred to "maybe never". The native sidecar
  window is the Phase 2 UX.

## 6. License posture and attribution

- Zeus: GPLv2-or-later. Compatible.
- `vst3sdk` (Steinberg): MIT since October 2025. Compatible.
- CLAP SDK: MIT. Compatible.
- Vestige VST2 header: LGPL clean-room. Compatible with GPLv2+.
- JUCE: rejected on license grounds.
- Steinberg VST2 SDK: rejected on distribution-rights grounds.

Operators load proprietary VST plugins at runtime through a generic
host interface. Per the FSF position on host/plugin boundaries, this
is not derivative-work creation — the plugin is dynamically loaded
user content, not Zeus source. No plugin-license restriction is
imposed on operators.

## 7. Out of scope

- In-process VST loading (forbids 32-bit; no crash isolation).
- Remote / network plugin host (AudioGridder pattern). Sidecar is
  local-only.
- Audio Unit (AU) on macOS.
- LV2 in Phase 1.
- Plugin sandboxing / threat-model. Per Brian's framing on #185, that
  is the installer's problem.
