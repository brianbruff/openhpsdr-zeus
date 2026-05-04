# Zeus.PluginHost

Host-side .NET skeleton for the Zeus VST / CLAP plugin sidecar. Paired
with the C++ sidecar at `~/Projects/openhpsdr-zeus-plughost/`.

**Status:** Phase 1 skeleton — no plugin loading, no actual audio
round-trip yet. The IPC types exist, the sidecar process gets launched
and watched, and the SHM ring is wire-compatible with the C++ side. The
control-plane `Hello` handshake and the audio-plane block round-trip
are wired in on the seam-wiring branch.

## Architecture

See `docs/proposals/vst-host.md` (the ADR) for the full picture. The
short version:

- **Out-of-process sidecar** — one per loaded plugin. 64-bit hosts can't
  load 32-bit plugin DLLs, and a sidecar isolates plugin crashes from
  the Zeus.Server process. The Phase 1 SIGKILL-during-TX recovery test
  is the load-bearing acceptance gate.
- **Audio plane** — two SPSC lock-free shared-memory rings per plugin
  instance, 64-byte aligned `BlockHeader` followed by planar float32
  payload. Phase 1 geometry: 256 frames, 48 kHz, mono.
- **Control plane** — Win32 named pipe / Unix-domain socket carrying
  length-prefixed CBOR (Phase 2). Phase 1 here ships the framing layer
  and message types only; CBOR encoding is deferred.

## Phase 1 entry points (`IPluginHost`)

- `bool IsRunning { get; }` — sidecar process alive + handshake done.
- `Task StartAsync(CancellationToken)` — launch + handshake.
- `Task StopAsync(CancellationToken)` — graceful shutdown, then kill.
- `bool TryProcess(ReadOnlySpan<float> input, Span<float> output, int frames)`
  — round-trip one audio block. Returns `false` on bypass, dead
  sidecar, or ring full; callers fall through to their own bypass path.

## Wiring it up

`AddZeusPluginHost(this IServiceCollection)` is exposed but
**deliberately not called from `Zeus.Server/Program.cs` yet** — the DI
registration happens on the seam-wiring branch.

## Sidecar binary discovery

`SidecarLocator.Locate()` resolves the binary in this order:

1. `ZEUS_PLUGHOST_BIN` environment variable.
2. Sibling checkout: walk up looking for
   `openhpsdr-zeus-plughost/build/zeus-plughost`.
3. Bare name on `PATH`.
