# VST Plugin Host — Phase 2 IPC Wire Spec

Status: pinned (Phase 2 entry)
Branch: `VST-Experimental` (Zeus) / `main` (openhpsdr-zeus-plughost)
Companion ADR: `docs/proposals/vst-host.md`

This document pins the cross-process IPC contract between the Zeus .NET host
(`Zeus.PluginHost`) and the C++ sidecar (`zeus-plughost`) for Phase 2. Both
ends MUST agree on every detail in this file; drift here will silently
corrupt audio with no runtime warning. If you change anything, bump the
`protocolVersion` field in the Hello message and update both implementations
in the same commit.

Phase 2 only delivers a working pass-through round-trip. Plugin loading,
parameter messages, and per-block latency tightening are Phase 3.

## 1. Naming

A unique instance suffix `{pid}-{counter}` distinguishes one host's rings,
semaphores, and control socket from any other host running on the same
machine. `pid` is the .NET host process id; `counter` is a process-local
monotonically increasing 32-bit integer that increments on every sidecar
launch.

| Resource          | Name format                                      | POSIX type             |
| ----------------- | ------------------------------------------------ | ---------------------- |
| Host->sidecar shm | `/zeus-plughost-{pid}-{counter}-h2s`             | `shm_open`             |
| Sidecar->host shm | `/zeus-plughost-{pid}-{counter}-s2h`             | `shm_open`             |
| Host->sidecar sem | `/zeus-plughost-{pid}-{counter}-h2s-sem`         | `sem_open`             |
| Sidecar->host sem | `/zeus-plughost-{pid}-{counter}-s2h-sem`         | `sem_open`             |
| Control socket    | `/tmp/zeus-plughost-{pid}-{counter}.sock`        | AF_UNIX `SOCK_STREAM`  |

POSIX shm/sem names start with a single `/`, contain no further `/`, and stay
under 250 characters. Linux glibc maps them to `/dev/shm/<name without slash>`
and `/dev/shm/sem.<name without slash>` respectively, which is the path the
.NET side uses to attach via `MemoryMappedFile.CreateFromFile`.

## 2. Audio plane (shared memory rings)

One ring per direction. Wire layout is identical in both directions; only
the producer/consumer roles differ.

### 2.1 Sizing

```
RingControlBlock          = 64 bytes (one cache line, indices + counters)
slot count                = 8
slot bytes                = 64 (BlockHeader) + 256 frames * 1 channel * 4 = 1088 bytes
ring bytes (logical)      = 64 + 8 * 1088 = 8768 bytes
ring bytes (mmap rounded) = round_up(8768, 4096) = 12288 bytes
```

Both ends `ftruncate(fd, 12288)` and `mmap(NULL, 12288, ...)`. The host is
the creator; the sidecar opens the same name and attaches.

### 2.2 RingControlBlock (offset 0..159)

The control block is exactly 160 bytes. Both sides MUST agree on this — if
the C++ struct gets `alignas(64)` it pads up to 192 (because the natural
size is 160 and `alignas` rounds the total up to a multiple of 64) while
the C# `[StructLayout(Size = 160)]` does not, and the slots will be at
different offsets in the mapping. We caught this once in Phase 2 entry and
the symptom was "sidecar processes block, host reads slot full of zeros."
Use the explicit 8-byte alignment that comes from `std::atomic<uint64_t>`;
the head/tail fields land on 64-byte boundaries naturally because the
mapping is page-aligned.

| Offset | Size | Field          | Notes                                   |
| -----: | ---: | -------------- | --------------------------------------- |
|      0 |    8 | head (u64)     | writer index, atomic, monotonic         |
|      8 |   56 | pad0           | cache-line fill                         |
|     64 |    8 | tail (u64)     | reader index, atomic, monotonic         |
|     72 |   56 | pad1           | cache-line fill                         |
|    128 |    4 | slotCount      | 8                                       |
|    132 |    4 | slotBytes      | 1088                                    |
|    136 |    4 | frames         | 256                                     |
|    140 |    4 | channels       | 1                                       |
|    144 |    4 | sampleRate     | 48000                                   |
|    148 |   12 | reserved[3]    | zero                                    |

Total prefix = 160 bytes. Slots start at offset 160.

### 2.3 Slots

8 slots * 1088 bytes each. Each slot is one `BlockHeader` (64 bytes,
defined in `audio/block_format.h`) followed by `frames * channels * 4`
bytes of planar float32. Phase 2 = 256 * 1 * 4 = 1024 payload bytes.

### 2.4 Discipline

SPSC, one writer + one reader per direction. The semaphore (Section 3) is
the synchronization edge; head/tail use Volatile reads + writes only.

- Writer:
  1. Read `head`, `tail`. If `head - tail >= slotCount`, ring is full
     (drop or wait per caller policy).
  2. Compute slot at `head & (slotCount - 1)`. Write header + payload.
  3. `Volatile.Write(head, head + 1)` (release semantics).
  4. `sem_post` on the matching wakeup semaphore.
- Reader:
  1. `sem_timedwait` on the matching wakeup semaphore (50 ms Phase 2).
  2. Read `tail`, `head`. If `head == tail`, ring is empty (timeout
     wake or spurious; loop).
  3. Compute slot at `tail & (slotCount - 1)`. Read header + payload.
  4. `Volatile.Write(tail, tail + 1)` (release semantics).

Phase 2 audio loop is single-block per `TryProcess` call; a block is
produced, the corresponding block is consumed before the call returns.

## 3. Wakeup primitive (POSIX named semaphores)

Each ring has its own named semaphore.

- `/zeus-plughost-{pid}-{counter}-h2s-sem` — host posts when a block is
  available for the sidecar. Sidecar waits.
- `/zeus-plughost-{pid}-{counter}-s2h-sem` — sidecar posts when a block is
  available for the host. Host waits.

C++ uses `sem_open(name, O_CREAT, 0600, 0)`, `sem_post`, `sem_timedwait`,
`sem_close`, `sem_unlink`. .NET P/Invokes the same symbols via `libc.so.6`
(Linux) or `libc.dylib` (macOS). Windows is not supported in Phase 2;
`Wakeup` throws `PlatformNotSupportedException` until Phase 3 brings a
named-Event implementation.

Initial value is 0 (creator only). Each `sem_post` raises the count by one;
`sem_timedwait` decrements by one or returns ETIMEDOUT.

The host creates both semaphores. The sidecar opens by name (no `O_CREAT`).
On host shutdown, the host calls `sem_unlink` on each name after the sidecar
has exited.

## 4. Control plane (AF_UNIX SOCK_STREAM)

### 4.1 Topology

- Path: `/tmp/zeus-plughost-{pid}-{counter}.sock`.
- Server: .NET host. Binds + listens BEFORE forking the sidecar.
- Client: sidecar. Connects after start with retry (50 ms intervals,
  total 2 s timeout).
- One concurrent connection per host. The sidecar's audio loop runs on a
  separate thread from the control read loop; both share an atomic exit
  flag.

### 4.2 Framing

Every message is:

```
+--------------------+--------+----------------------+
| length (u32 le)    | type   | payload              |
+--------------------+--------+----------------------+
| 4 bytes            | 1 byte | length - 1 bytes     |
```

`length` is the byte count of `type + payload` (i.e. `length >= 1`).
Maximum payload is 1 MiB (defensive cap; Phase 2 messages are all <100 B).

### 4.3 Phase 2 message types

| Tag | Name      | Direction       | Payload                                |
| --: | --------- | --------------- | -------------------------------------- |
|  01 | Hello     | sidecar -> host | u32 protoVer, u32 sampleRate, u32 framesPerBlock, u32 channels |
|  02 | HelloAck  | host -> sidecar | (none)                                 |
|  03 | Heartbeat | bidirectional   | (none, optional in Phase 2)            |
|  04 | Goodbye   | host -> sidecar | (none)                                 |
|  05 | LogLine   | sidecar -> host | UTF-8 bytes (plain text)               |

All multi-byte integers are little-endian.

`Hello` payload (16 bytes):

```
[protocolVersion u32=1][sampleRate u32=48000][framesPerBlock u32=256][channels u32=1]
```

The sidecar sends `Hello` immediately after connecting. The host validates,
sends `HelloAck`, and the sidecar then enters its audio loop. If the host
rejects (e.g. version mismatch), it closes the socket and the sidecar exits.

Plugin lifecycle messages (`LoadPlugin`, `SetParam`, `GetState`, `SetState`)
are reserved for Phase 3 and intentionally absent here.

## 5. Lifecycle

### 5.1 Startup

1. Host computes `{pid}-{counter}`.
2. Host binds the AF_UNIX socket and starts an `accept()` listener.
3. Host creates both shm regions via `shm_open` + `ftruncate(12288)` + `mmap`.
4. Host creates both named semaphores via `sem_open(O_CREAT)` with initial 0.
5. Host launches the sidecar with argv:
   - `--shm-name {pid}-{counter}` (the suffix; sidecar appends `-h2s`/`-s2h`)
   - `--control-pipe /tmp/zeus-plughost-{pid}-{counter}.sock`
6. Sidecar connects to the control socket (retry, 2 s timeout).
7. Sidecar opens both shm regions by name + maps them.
8. Sidecar opens both named semaphores by name.
9. Sidecar sends `Hello`. Host validates and sends `HelloAck`. Host
   considers the plugin host running.
10. Sidecar enters audio loop.

### 5.2 Audio loop (sidecar)

```
while (!stop):
    if not sem_timedwait(h2s-sem, 50 ms): continue   # poll stop flag
    in = h2s.Read()
    out = s2h.Acquire()
    copy header + payload (Phase 2 = bit-identical pass-through)
    s2h.Publish(out)
    h2s.Release(in)
    sem_post(s2h-sem)
```

### 5.3 TryProcess (host)

```
acquire = h2s.Acquire()
if !acquire: return false           # ring full (sidecar fell behind)
write header + payload
h2s.Publish()
sem_post(h2s-sem)
if !sem_timedwait(s2h-sem, 50 ms): return false   # sidecar dead or stuck
read = s2h.Read()
copy payload to output
s2h.Release()
return true
```

### 5.4 Shutdown

- Graceful (host-driven): host sends `Goodbye`, waits 500 ms for the
  sidecar to close the socket cleanly. If still alive, host kills it.
- Crash (sidecar SIGKILL or uncaught throw): host's next
  `sem_timedwait(s2h-sem)` returns ETIMEDOUT. Host detects the dead
  process via the `Process.Exited` event and flips `IsRunning = false`.
- Crash (host crash): kernel cleans up the host's process resources;
  the sidecar's next `sem_timedwait(h2s-sem)` times out, then its
  control-socket read returns 0 (EOF), and the sidecar exits cleanly.

### 5.5 Cleanup

The host is the sole owner of the names. After the sidecar exits (any
path), the host calls:

- `shm_unlink` on both shm names
- `sem_unlink` on both sem names
- `unlink` on the AF_UNIX socket path

The sidecar never unlinks. If the host crashes without unlinking, the
operator is expected to clean stale entries from `/dev/shm` and
`/dev/shm/sem.*` and `/tmp/`. Phase 2 tests assert zero leakage on every
run.

## 6. Phase 2 simplifications (called out so Phase 3 doesn't repeat them)

- Synchronous I/O on the control socket. Phase 3 will likely move to
  async with cancellation tokens.
- 50 ms semaphore timeout. Phase 3 needs ~5 ms to keep up with real TX
  audio at 48 kHz / 256 frames per block (~5.3 ms per block).
- Allocation in `TryProcess` is acceptable. Phase 3 must move to
  preallocated buffers to satisfy the realtime contract.
- Hardcoded geometry: 256 frames, 1 channel, 48000 Hz, 1 plugin slot.
  Phase 3 negotiates these via Hello.
- Heartbeat is optional. Phase 3 enforces 1 Hz with a deadman timer for
  the silent-sidecar case (sidecar deadlocked but not exited).
- No realtime scheduling priority. Phase 4 will pin the audio thread.

## 7. Error handling matrix (Phase 2)

| Condition                          | Sidecar response       | Host response                       |
| ---------------------------------- | ---------------------- | ----------------------------------- |
| Socket connect fails               | exit(2)                | timeout in StartAsync, throw        |
| Hello/HelloAck mismatch            | exit(3)                | close socket, throw                 |
| h2s ring full (writer)             | n/a                    | TryProcess returns false            |
| s2h ring empty after timeout       | n/a                    | TryProcess returns false            |
| Sidecar crashes mid-loop           | (kernel cleans fds)    | Exited event fires, IsRunning=false |
| Goodbye received                   | drain + exit(0)        | Process.Exited fires                |
| Control socket EOF unexpectedly    | exit(0) cleanly        | Exited event fires                  |

## 8. Cross-platform shape

Phase 2 ships Linux only. macOS is expected to work with one symbol
swap (`libc.so.6` -> `libc.dylib`); the spec is otherwise identical.
Windows requires a different wakeup primitive (named events) and
shared-memory backing (named file mappings) — Phase 3 deliverable.
