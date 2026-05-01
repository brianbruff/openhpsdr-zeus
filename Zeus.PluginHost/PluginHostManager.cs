// PluginHostManager.cs — singleton owner of the sidecar lifecycle.
//
// Phase 2 responsibilities:
//   - generate unique shm/sem/socket names per launch (PID + counter)
//   - create the host->sidecar + sidecar->host shm rings
//   - create the matching named POSIX wakeup semaphores
//   - bind + listen on the AF_UNIX control socket BEFORE the sidecar starts
//   - launch the sidecar with --shm-name / --control-pipe argv
//   - accept the sidecar connection, validate Hello, send HelloAck
//   - expose TryProcess(input, output, frames) that round-trips one block
//   - on StopAsync: send Goodbye, wait 500 ms, kill if alive, unlink names
//   - on dispose / process exit: same cleanup path
//   - re-broadcast SidecarExited so callers can react to crashes
//
// SIGKILL of the sidecar is the load-bearing acceptance gate. When it
// happens mid-stream, sem_timedwait on s2h-sem times out within 50 ms
// (Phase 2 timeout); TryProcess returns false; Process.Exited fires;
// IsRunning flips to false.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Zeus.PluginHost.Chain;
using Zeus.PluginHost.Ipc;
using Zeus.PluginHost.Native;

namespace Zeus.PluginHost;

public sealed class PluginHostManager : IPluginHost, IDisposable, IAsyncDisposable
{
    private readonly IPluginHostLog _log;
    private readonly object _gate = new();

    // Backoff schedule for unexpected sidecar exits, in milliseconds.
    private static readonly int[] s_backoffMs = { 100, 250, 500, 1_000, 5_000, 30_000 };
    private int _backoffStep;

    // Process-local instance counter — combined with PID it gives a
    // unique suffix per sidecar launch even if launches happen in rapid
    // succession.
    private static int s_globalCounter;

    private SidecarProcess? _process;
    private CancellationTokenSource? _runCts;
    private string? _instanceSuffix;
    private string? _h2sShmName;
    private string? _s2hShmName;
    private string? _h2sSemName;
    private string? _s2hSemName;
    private string? _socketPath;
    private ShmRing? _h2sRing;
    private ShmRing? _s2hRing;
    private Wakeup? _h2sSem;
    private Wakeup? _s2hSem;
    private ControlChannel? _control;

    // Phase 2 round-trip: protect TryProcess from concurrent callers. The
    // SHM rings are SPSC and assume a single writer + single reader; the
    // public IPluginHost contract does not promise thread affinity, so we
    // serialize at the manager level.
    private readonly SemaphoreSlim _txGate = new(1, 1);

    // Plugin lifecycle: control-plane I/O is synchronous and serialized
    // by this gate. Held across the LoadPlugin/UnloadPlugin send + the
    // matching reply receive so two concurrent callers can't interleave
    // their request/response pairs on the shared ControlChannel.
    private readonly SemaphoreSlim _controlGate = new(1, 1);

    // Slot table. Always sized to ChainConstants.MaxSlots; empty slots
    // have ChainSlot.Plugin == null. Replaced wholesale (immutable record
    // semantics) when a slot transitions; the public Slots property
    // returns a snapshot of this array.
    private readonly ChainSlot[] _slots = new ChainSlot[ChainConstants.MaxSlots];

    // Master enable. False == bit-identical pass-through regardless of
    // slot population. Stored under _gate for snapshot consistency with
    // Slots; the sidecar is the source of truth.
    private bool _chainEnabled;

    /// <summary>
    /// Default LoadPlugin timeout: VST3 module load can be slow on first
    /// call (the SDK lazily resolves a few symbols, the plugin itself
    /// may run validate code in its factory). 10 s is generous; the
    /// caller can supply a tighter CancellationToken if they want.
    /// </summary>
    public TimeSpan LoadPluginTimeout { get; init; } = TimeSpan.FromSeconds(10);

    private bool _disposed;

    /// <summary>Re-broadcast of <see cref="SidecarProcess.Exited"/>.</summary>
    public event EventHandler<SidecarExitedEventArgs>? SidecarExited;

    /// <inheritdoc />
    public event EventHandler<EditorClosedEventArgs>? SlotEditorClosed;

    /// <inheritdoc />
    public event EventHandler<EditorResizedEventArgs>? SlotEditorResized;

    /// <inheritdoc />
    public event EventHandler<ParamChangedEventArgs>? SlotParamChanged;

    /// <summary>Current sidecar PID, or null if no sidecar is running.</summary>
    public int? CurrentProcessId
    {
        get
        {
            lock (_gate)
            {
                return _process?.IsAlive == true ? _process.ProcessId : (int?)null;
            }
        }
    }

    public PluginHostManager(IPluginHostLog? log = null)
    {
        _log = log ?? NullPluginHostLog.Instance;

        // Wire-format guard. If the C# struct layout drifts from the C++
        // BlockHeader, every audio block thereafter would silently corrupt.
        var actualSize = Marshal.SizeOf<BlockHeader>();
        if (actualSize != 64)
        {
            throw new InvalidOperationException(
                $"BlockHeader layout drift — C# and C++ must agree on 64 bytes " +
                $"(got {actualSize}). See Zeus.PluginHost/Ipc/BlockHeader.cs and " +
                $"openhpsdr-zeus-plughost/src/audio/block_format.h.");
        }

        // Slot table starts as 8 empty slots. The C++ side does the same.
        for (int i = 0; i < _slots.Length; i++)
        {
            _slots[i] = new ChainSlot(
                Index: i,
                Plugin: null,
                Bypass: false,
                Parameters: Array.Empty<PluginParameter>());
        }
    }

    /// <inheritdoc />
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _process is { IsAlive: true } && !_disposed
                    && _h2sRing != null && _s2hRing != null
                    && _h2sSem != null && _s2hSem != null
                    && _control != null && _control.IsConnected;
            }
        }
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken ct)
    {
        ThrowIfDisposed();

        // Snapshot existing state under the lock; do the slow allocation
        // path outside the lock so we don't hold it across syscalls.
        lock (_gate)
        {
            if (_process is { IsAlive: true } && IsRunning)
            {
                return; // idempotent
            }
        }

        var path = SidecarLocator.Locate(_log);
        if (path == null)
        {
            throw new InvalidOperationException(
                "PluginHostManager: zeus-plughost binary not found. " +
                $"Set {SidecarLocator.EnvVarName} or build the sidecar at " +
                "~/Projects/openhpsdr-zeus-plughost/build/.");
        }

        var pid = Environment.ProcessId;
        var counter = Interlocked.Increment(ref s_globalCounter);
        var suffix = $"{pid}-{counter}";

        var h2sShm = $"/zeus-plughost-{suffix}-h2s";
        var s2hShm = $"/zeus-plughost-{suffix}-s2h";
        var h2sSem = $"/zeus-plughost-{suffix}-h2s-sem";
        var s2hSem = $"/zeus-plughost-{suffix}-s2h-sem";
        var sock   = $"/tmp/zeus-plughost-{suffix}.sock";

        ShmRing? h2sRing = null;
        ShmRing? s2hRing = null;
        Wakeup?  h2sSemHandle = null;
        Wakeup?  s2hSemHandle = null;
        ControlChannel? channel = null;
        SidecarProcess? proc = null;

        try
        {
            // Create shared resources before forking the sidecar so it can
            // open them by name immediately.
            h2sRing = ShmRing.CreateNamed(h2sShm,
                Phase2Frames, Phase2Channels, Phase2SampleRate, Phase2SlotCount);
            s2hRing = ShmRing.CreateNamed(s2hShm,
                Phase2Frames, Phase2Channels, Phase2SampleRate, Phase2SlotCount);

            h2sSemHandle = Wakeup.Create(h2sSem);
            s2hSemHandle = Wakeup.Create(s2hSem);

            channel = ControlChannel.Bind(sock);

            proc = SidecarProcess.Launch(path, _log, new[]
            {
                "--shm-name",     suffix,
                "--control-pipe", sock,
            });
            proc.Exited += OnSidecarExited;

            // Accept the sidecar's connect within 2 s, then receive Hello +
            // ACK with HelloAck. Total budget: 4 s.
            await channel.AcceptAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);

            var frame = await channel.ReceiveAsync(ct).ConfigureAwait(false)
                ?? throw new IOException("PluginHostManager: sidecar closed before Hello");
            if (frame.Tag != ControlTag.Hello)
            {
                throw new IOException(
                    $"PluginHostManager: expected Hello, got {frame.Tag}");
            }
            var hello = HelloMessage.Decode(frame.Payload);
            if (hello.ProtocolVersion != 1 ||
                hello.SampleRate     != Phase2SampleRate ||
                hello.FramesPerBlock != Phase2Frames ||
                hello.Channels       != Phase2Channels)
            {
                throw new IOException(
                    $"PluginHostManager: Hello mismatch (proto={hello.ProtocolVersion} " +
                    $"rate={hello.SampleRate} frames={hello.FramesPerBlock} ch={hello.Channels})");
            }

            await channel.SendAsync(ControlTag.HelloAck, ReadOnlyMemory<byte>.Empty, ct)
                .ConfigureAwait(false);

            // Wire the channel's async editor events through as our own.
            channel.EditorClosed  += OnChannelEditorClosed;
            channel.EditorResized += OnChannelEditorResized;
            channel.ParamChanged  += OnChannelParamChanged;

            lock (_gate)
            {
                _process        = proc;
                _instanceSuffix = suffix;
                _h2sShmName     = h2sShm;
                _s2hShmName     = s2hShm;
                _h2sSemName     = h2sSem;
                _s2hSemName     = s2hSem;
                _socketPath     = sock;
                _h2sRing        = h2sRing;
                _s2hRing        = s2hRing;
                _h2sSem         = h2sSemHandle;
                _s2hSem         = s2hSemHandle;
                _control        = channel;
                _runCts?.Dispose();
                _runCts = new CancellationTokenSource();
                _backoffStep = 0;
            }
            // Drop any leftover re-chunking state from a previous sidecar
            // run. Held under _txGate (uncontended at start) so concurrent
            // TryProcess calls observe a coherent reset.
            await _txGate.WaitAsync(ct).ConfigureAwait(false);
            try { ResetRechunkRings(); }
            finally { _txGate.Release(); }

            _log.LogInformation(
                $"PluginHostManager: handshake OK pid={proc.ProcessId} suffix={suffix}");
        }
        catch
        {
            // Tear down anything we partially built.
            try { proc?.Dispose(); } catch { }
            try { channel?.Dispose(); } catch { }
            try { h2sSemHandle?.Dispose(); } catch { }
            try { s2hSemHandle?.Dispose(); } catch { }
            try { h2sRing?.Dispose(); } catch { }
            try { s2hRing?.Dispose(); } catch { }
            // Best-effort name cleanup on partial-init failure.
            ShmRing.Unlink(h2sShm);
            ShmRing.Unlink(s2hShm);
            Wakeup.Unlink(h2sSem);
            Wakeup.Unlink(s2hSem);
            try { if (File.Exists(sock)) File.Delete(sock); } catch { }
            throw;
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken ct)
    {
        // Auto-unload every loaded slot BEFORE tearing the sidecar down.
        // Best-effort: a dead sidecar can't reply, and the kernel reaps
        // its mappings either way.
        try
        {
            int[] occupied;
            lock (_gate)
            {
                var indices = new List<int>();
                for (int i = 0; i < _slots.Length; i++)
                {
                    if (_slots[i].Plugin != null) indices.Add(i);
                }
                occupied = indices.ToArray();
            }
            foreach (var i in occupied)
            {
                using var unloadCts = CancellationTokenSource
                    .CreateLinkedTokenSource(ct);
                unloadCts.CancelAfter(TimeSpan.FromMilliseconds(500));
                try
                {
                    await UnloadSlotAsync(i, unloadCts.Token)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Sidecar may be dead between iterations — keep going.
                }
            }
        }
        catch
        {
            // Sidecar may be dead — fall through to teardown.
        }

        SidecarProcess? proc;
        ControlChannel? channel;
        ShmRing? h2sRing;
        ShmRing? s2hRing;
        Wakeup? h2sSemHandle;
        Wakeup? s2hSemHandle;
        string? h2sShm, s2hShm, h2sSem, s2hSem, sock;
        CancellationTokenSource? cts;

        lock (_gate)
        {
            if (_disposed) return;
            for (int i = 0; i < _slots.Length; i++)
            {
                _slots[i] = new ChainSlot(
                    Index: i,
                    Plugin: null,
                    Bypass: false,
                    Parameters: Array.Empty<PluginParameter>());
            }
            _chainEnabled = false;
            proc           = _process;
            channel        = _control;
            h2sRing        = _h2sRing;
            s2hRing        = _s2hRing;
            h2sSemHandle   = _h2sSem;
            s2hSemHandle   = _s2hSem;
            h2sShm         = _h2sShmName;
            s2hShm         = _s2hShmName;
            h2sSem         = _h2sSemName;
            s2hSem         = _s2hSemName;
            sock           = _socketPath;
            cts            = _runCts;
            _process = null; _control = null;
            _h2sRing = null; _s2hRing = null;
            _h2sSem = null;  _s2hSem = null;
            _h2sShmName = null; _s2hShmName = null;
            _h2sSemName = null; _s2hSemName = null;
            _socketPath = null; _instanceSuffix = null;
            _runCts = null;
        }

        if (cts != null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        if (channel != null)
        {
            channel.EditorClosed  -= OnChannelEditorClosed;
            channel.EditorResized -= OnChannelEditorResized;
            channel.ParamChanged  -= OnChannelParamChanged;
        }

        // Send Goodbye if the channel is up; tolerate any failure.
        if (channel != null && channel.IsConnected)
        {
            try
            {
                using var goodbyeCts = CancellationTokenSource
                    .CreateLinkedTokenSource(ct);
                goodbyeCts.CancelAfter(TimeSpan.FromMilliseconds(500));
                await channel.SendAsync(ControlTag.Goodbye,
                    ReadOnlyMemory<byte>.Empty, goodbyeCts.Token).ConfigureAwait(false);
            }
            catch
            {
                // Channel may already be closed (sidecar dead).
            }
        }

        if (proc != null)
        {
            proc.Exited -= OnSidecarExited;
            try
            {
                await proc.KillAsync(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
            }
            finally
            {
                proc.Dispose();
            }
        }

        // Cleanup IPC resources. Order: control channel first (closes the
        // socket), then rings + sems (host owns the names and unlinks).
        try { channel?.Dispose(); } catch { }
        try { h2sRing?.Dispose(); } catch { }
        try { s2hRing?.Dispose(); } catch { }
        try { h2sSemHandle?.Dispose(); } catch { }
        try { s2hSemHandle?.Dispose(); } catch { }

        if (h2sShm != null) ShmRing.Unlink(h2sShm);
        if (s2hShm != null) ShmRing.Unlink(s2hShm);
        if (h2sSem != null) Wakeup.Unlink(h2sSem);
        if (s2hSem != null) Wakeup.Unlink(s2hSem);
        if (sock != null)
        {
            try { if (File.Exists(sock)) File.Delete(sock); } catch { }
        }
    }

    // Re-chunking buffers. The sidecar wire format is fixed at 256 frames
    // per block (Phase2Frames), but WDSP TX feeds blocks of 1024 (P1 mic) or
    // 512 (P2 mic). We accumulate caller input into _inRing, drain it in
    // 256-frame batches through the sidecar round-trip, and accumulate
    // sidecar responses into _outRing for the caller to drain. Both rings
    // are owned by _txGate-protected sections so SPSC discipline on the
    // shared-memory rings is preserved.
    //
    // Initial-fill latency: when the rings are empty, the first call may
    // produce fewer output samples than requested (the sidecar's first
    // 256-frame response can't satisfy a 1024-frame request) and the
    // method returns false → caller treats as bypass for that block. After
    // the first round-trip the output ring has 256 frames; subsequent
    // calls fill from that buffered set + new round-trips. Steady-state
    // latency is at most 255 samples at 48 kHz (~5.3 ms) — acceptable for
    // TX since the operator never monitors through the same path.
    //
    // Sized at 4096 floats each (16 KiB) — comfortably larger than any
    // expected caller block (P1 1024) plus 256 of overhead.
    private const int RechunkRingCapacity = 4096;
    private readonly float[] _inRing = new float[RechunkRingCapacity];
    private readonly float[] _outRing = new float[RechunkRingCapacity];
    private int _inRingCount;   // logical length, samples [0, _inRingCount) live
    private int _outRingCount;  // same

    /// <summary>Maximum block size accepted by <see cref="TryProcess"/>.
    /// Blocks larger than this are rejected to keep the re-chunking ring
    /// bounded. WDSP's TX block sizes (P1=1024, P2=512) are well under
    /// this limit.</summary>
    public const int MaxFramesPerCall = 2048;

    /// <inheritdoc />
    public bool TryProcess(ReadOnlySpan<float> input, Span<float> output, int frames)
    {
        if (_disposed) return false;
        if (frames <= 0) return false;
        if (frames > MaxFramesPerCall) return false;
        if (input.Length < frames || output.Length < frames) return false;

        // Snapshot the IPC handles under the lock; we operate on them
        // outside the lock so a concurrent StartAsync/StopAsync isn't
        // blocked on the round-trip.
        ShmRing? h2s; ShmRing? s2h; Wakeup? h2sSemHandle; Wakeup? s2hSemHandle;
        SidecarProcess? proc;
        lock (_gate)
        {
            if (!IsRunning) return false;
            h2s = _h2sRing;
            s2h = _s2hRing;
            h2sSemHandle = _h2sSem;
            s2hSemHandle = _s2hSem;
            proc = _process;
        }
        if (h2s == null || s2h == null || h2sSemHandle == null
            || s2hSemHandle == null || proc == null)
        {
            return false;
        }

        // Serialize so SPSC discipline on the shared-memory rings holds AND
        // the re-chunking rings stay coherent across overlapped callers.
        if (!_txGate.Wait(0))
        {
            return false; // another caller is mid-flight
        }
        try
        {
            // Fast path: caller's block is exactly the sidecar block size
            // AND nothing is currently buffered. Skip the rings entirely.
            if (frames == (int)Phase2Frames && _inRingCount == 0 && _outRingCount == 0)
            {
                return TryProcessOneBlock(h2s, s2h, h2sSemHandle, s2hSemHandle,
                    input, output, frames);
            }

            // Refuse if the input ring would overflow. Caller treats as
            // bypass; we don't drop intermediate state because that would
            // shift the chain's stream alignment.
            if (_inRingCount + frames > RechunkRingCapacity)
            {
                return false;
            }

            // Append caller input to the in-ring.
            input.Slice(0, frames).CopyTo(_inRing.AsSpan(_inRingCount, frames));
            _inRingCount += frames;

            // Drain in 256-frame batches while we have a full block AND the
            // output ring has room. Rings are sized for ~16 batches each so
            // a single 1024-sample call (4 batches) never trips this guard.
            int batch = (int)Phase2Frames;
            Span<float> batchOut = stackalloc float[(int)Phase2Frames];
            while (_inRingCount >= batch && _outRingCount + batch <= RechunkRingCapacity)
            {
                bool ok = TryProcessOneBlock(h2s, s2h, h2sSemHandle, s2hSemHandle,
                    _inRing.AsSpan(0, batch), batchOut, batch);
                if (!ok)
                {
                    // Sidecar didn't respond on this batch. Drop the input
                    // we just consumed (otherwise the next call would
                    // re-send it, and a stuck sidecar would stay stuck).
                    Array.Copy(_inRing, batch, _inRing, 0, _inRingCount - batch);
                    _inRingCount -= batch;
                    return false;
                }
                // Compact in-ring (consumed `batch` from the head).
                Array.Copy(_inRing, batch, _inRing, 0, _inRingCount - batch);
                _inRingCount -= batch;
                // Append batch output.
                batchOut.CopyTo(_outRing.AsSpan(_outRingCount, batch));
                _outRingCount += batch;
            }

            // Do we have enough output to satisfy the caller?
            if (_outRingCount < frames)
            {
                return false; // priming — caller falls back to bypass
            }

            // Pop `frames` from out-ring head into caller output.
            _outRing.AsSpan(0, frames).CopyTo(output);
            Array.Copy(_outRing, frames, _outRing, 0, _outRingCount - frames);
            _outRingCount -= frames;
            return true;
        }
        finally
        {
            _txGate.Release();
        }
    }

    // One sidecar round-trip at the wire-fixed Phase2Frames size. Caller
    // owns _txGate. Returns false on ring-full, timeout, or spurious wake.
    private unsafe bool TryProcessOneBlock(
        ShmRing h2s, ShmRing s2h,
        Wakeup h2sSemHandle, Wakeup s2hSemHandle,
        ReadOnlySpan<float> input, Span<float> output, int frames)
    {
        BlockHeader* slot = h2s.Acquire();
        if (slot == null) return false; // ring full

        slot->Seq        = (ulong)Environment.TickCount64;
        slot->Frames     = (uint)frames;
        slot->Channels   = Phase2Channels;
        slot->SampleRate = Phase2SampleRate;
        slot->Flags      = (uint)BlockFlags.None;
        for (int i = 0; i < 40; i++) slot->Reserved[i] = 0;

        float* payload = ShmRing.PayloadOf(slot);
        for (int i = 0; i < frames; i++) payload[i] = input[i];

        h2s.Publish(slot);
        h2sSemHandle.Post();

        // Wait for the sidecar's response with the Phase 2 budget. If the
        // sidecar is dead or stuck, the timeout fires and the caller falls
        // back to the bypass path.
        if (!s2hSemHandle.TimedWait(TimeSpan.FromMilliseconds(50)))
        {
            return false;
        }

        BlockHeader* response = s2h.Read();
        if (response == null) return false; // spurious wake

        float* respPayload = ShmRing.PayloadOf(response);
        for (int i = 0; i < frames; i++) output[i] = respPayload[i];
        s2h.Release(response);
        return true;
    }

    // Reset the re-chunking state when the sidecar comes up or goes down,
    // so a stale chunk from a previous run doesn't leak into the new one.
    private void ResetRechunkRings()
    {
        // Caller owns _txGate; we only zero the counts (no need to clear the
        // backing arrays — only the [0, count) prefix is read).
        _inRingCount = 0;
        _outRingCount = 0;
    }

    /// <inheritdoc />
    public LoadedPluginInfo? CurrentPlugin
    {
        get
        {
            lock (_gate)
            {
                return _slots[0].Plugin;
            }
        }
    }

    /// <inheritdoc />
    public int MaxChainSlots => ChainConstants.MaxSlots;

    /// <inheritdoc />
    public bool IsChainEnabled
    {
        get
        {
            lock (_gate)
            {
                return _chainEnabled;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ChainSlot> Slots
    {
        get
        {
            lock (_gate)
            {
                // Snapshot copy — caller mustn't see live mutations.
                var copy = new ChainSlot[_slots.Length];
                Array.Copy(_slots, copy, _slots.Length);
                return copy;
            }
        }
    }

    /// <inheritdoc />
    public Task<LoadPluginOutcome> LoadPluginAsync(
        string path, CancellationToken ct = default)
        => LoadSlotAsync(0, path, ct);

    /// <inheritdoc />
    public Task UnloadPluginAsync(CancellationToken ct = default)
        => UnloadSlotAsync(0, ct);

    // -- Phase 3a slot APIs ----------------------------------------------

    /// <inheritdoc />
    public async Task<LoadPluginOutcome> LoadSlotAsync(
        int slotIdx, string path, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (slotIdx < 0 || slotIdx >= ChainConstants.MaxSlots)
        {
            // Surface invalid-slot uniformly with a status==6 outcome. The
            // sidecar would reply the same way, but failing fast on the
            // host side avoids burning a control round-trip.
            return new LoadPluginOutcome(false, null,
                $"slot index out of range (0..{ChainConstants.MaxSlots - 1}): {slotIdx}");
        }
        if (string.IsNullOrEmpty(path))
        {
            return new LoadPluginOutcome(false, null, "path is empty");
        }

        ControlChannel channel;
        lock (_gate)
        {
            if (!IsRunning || _control == null)
            {
                return new LoadPluginOutcome(false, null,
                    "plugin host is not running");
            }
            channel = _control;
        }

        await _controlGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // If a plugin is already in this slot, unload it first so
            // CurrentPlugin / Slots[i].Plugin are honest while the new
            // load is in flight.
            bool slotOccupied;
            lock (_gate) { slotOccupied = _slots[slotIdx].Plugin != null; }
            if (slotOccupied)
            {
                try
                {
                    await UnloadSlotInternalAsync(channel, (byte)slotIdx, ct)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort.
                }
            }

            var req = new SlotLoadPluginRequest((byte)slotIdx, path);
            await channel.SendAsync(ControlTag.SlotLoadPlugin, req.Encode(), ct)
                .ConfigureAwait(false);

            using var loadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            loadCts.CancelAfter(LoadPluginTimeout);
            var frame = await ReceiveExpected(channel,
                ControlTag.SlotLoadPluginResult, loadCts.Token).ConfigureAwait(false);
            if (frame == null)
            {
                return new LoadPluginOutcome(false, null,
                    "sidecar closed before SlotLoadPluginResult");
            }
            var result = SlotLoadPluginResult.Decode(frame.Value.Payload);
            if (result.Status == 0)
            {
                var info = new LoadedPluginInfo(
                    result.Name ?? string.Empty,
                    result.Vendor ?? string.Empty,
                    result.Version ?? string.Empty);
                lock (_gate)
                {
                    var prev = _slots[slotIdx];
                    _slots[slotIdx] = prev with
                    {
                        Plugin = info,
                        Parameters = Array.Empty<PluginParameter>(),
                    };
                }
                _log.LogInformation(
                    $"PluginHostManager: slot {slotIdx} loaded plugin name='{info.Name}' " +
                    $"vendor='{info.Vendor}' version='{info.Version}'");
                return new LoadPluginOutcome(true, info, null);
            }
            _log.LogWarning(
                $"PluginHostManager: slot {slotIdx} LoadPlugin failed " +
                $"status={result.Status} error='{result.Error}'");
            return new LoadPluginOutcome(false, null,
                result.Error ?? $"LoadPlugin failed (status={result.Status})");
        }
        finally
        {
            _controlGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task UnloadSlotAsync(int slotIdx, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (slotIdx < 0 || slotIdx >= ChainConstants.MaxSlots)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIdx),
                $"must be in 0..{ChainConstants.MaxSlots - 1}");
        }
        ControlChannel? channel;
        lock (_gate)
        {
            channel = _control;
            if (channel == null || !channel.IsConnected)
            {
                var prev = _slots[slotIdx];
                _slots[slotIdx] = prev with
                {
                    Plugin = null,
                    Parameters = Array.Empty<PluginParameter>(),
                };
                return;
            }
        }

        await _controlGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await UnloadSlotInternalAsync(channel, (byte)slotIdx, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _controlGate.Release();
        }
    }

    private async Task UnloadSlotInternalAsync(
        ControlChannel channel, byte slotIdx, CancellationToken ct)
    {
        var req = new SlotUnloadPluginRequest(slotIdx);
        await channel.SendAsync(ControlTag.SlotUnloadPlugin, req.Encode(), ct)
            .ConfigureAwait(false);
        // Bumped from 2s → 10s 2026-04-30. Linux VST2 plugins (LSP family,
        // ZAM family) often run background animation/worker threads that
        // are joined inside effClose; on a slow box that easily exceeds
        // the old 2s budget. 10s matches LoadPluginTimeout and gives
        // well-behaved plugins room to clean up properly. The slot's
        // local cache is cleared either way so a timeout doesn't leave
        // the operator-visible state stuck pointing at a half-torn-down
        // plugin.
        using var unloadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        unloadCts.CancelAfter(TimeSpan.FromSeconds(10));
        ControlFrame? frame = null;
        try
        {
            frame = await ReceiveExpected(channel,
                ControlTag.SlotUnloadPluginResult, unloadCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Sidecar didn't reply within the budget OR the HTTP fetch
            // gave up first (browser timeout < our budget). Either way,
            // clear our local cache so the operator can re-attempt or
            // load a different plugin — keeping the slot pinned to a
            // possibly-still-loaded plugin would surface as "won't let
            // me unload it." If the sidecar truly hung in effClose
            // (Linux VST2 plugins with internal worker threads can do
            // this), the operator's escape hatch is the master VST
            // chain toggle, which kills+respawns the sidecar.
            _log.LogWarning(
                $"PluginHostManager: slot {slotIdx} Unload cancelled / timed out; clearing local cache anyway");
        }
        lock (_gate)
        {
            var prev = _slots[slotIdx];
            _slots[slotIdx] = prev with
            {
                Plugin = null,
                Parameters = Array.Empty<PluginParameter>(),
            };
        }
        if (frame != null)
        {
            var result = SlotUnloadPluginResult.Decode(frame.Value.Payload);
            if (result.Status != 0 && result.Status != 1)
            {
                _log.LogWarning(
                    $"PluginHostManager: slot {slotIdx} Unload status={result.Status}");
            }
        }
    }

    /// <inheritdoc />
    public async Task SetSlotBypassAsync(
        int slotIdx, bool bypass, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (slotIdx < 0 || slotIdx >= ChainConstants.MaxSlots)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIdx));
        }
        ControlChannel channel;
        lock (_gate)
        {
            if (!IsRunning || _control == null)
            {
                throw new InvalidOperationException("plugin host is not running");
            }
            channel = _control;
        }
        await _controlGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var req = new SlotSetBypassRequest((byte)slotIdx, bypass);
            await channel.SendAsync(ControlTag.SlotSetBypass, req.Encode(), ct)
                .ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            var frame = await ReceiveExpected(channel,
                ControlTag.SlotSetBypassResult, cts.Token).ConfigureAwait(false);
            if (frame == null) return;
            var result = SlotSetBypassResult.Decode(frame.Value.Payload);
            if (result.Status == 0)
            {
                lock (_gate)
                {
                    var prev = _slots[slotIdx];
                    _slots[slotIdx] = prev with { Bypass = bypass };
                }
            }
            else
            {
                _log.LogWarning(
                    $"PluginHostManager: slot {slotIdx} SetBypass status={result.Status}");
            }
        }
        finally
        {
            _controlGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PluginParameter>> ListSlotParametersAsync(
        int slotIdx, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (slotIdx < 0 || slotIdx >= ChainConstants.MaxSlots)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIdx));
        }
        ControlChannel channel;
        lock (_gate)
        {
            if (!IsRunning || _control == null)
            {
                return Array.Empty<PluginParameter>();
            }
            channel = _control;
        }
        await _controlGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var req = new SlotListParamsRequest((byte)slotIdx);
            await channel.SendAsync(ControlTag.SlotListParams, req.Encode(), ct)
                .ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var frame = await ReceiveExpected(channel,
                ControlTag.SlotParamListResult, cts.Token).ConfigureAwait(false);
            if (frame == null) return Array.Empty<PluginParameter>();
            var result = SlotParamListResult.Decode(frame.Value.Payload);
            if (result.Status != 0)
            {
                if (result.Status != 1)
                {
                    _log.LogWarning(
                        $"PluginHostManager: slot {slotIdx} ListParams " +
                        $"status={result.Status}");
                }
                return Array.Empty<PluginParameter>();
            }
            var list = new List<PluginParameter>(result.Parameters.Count);
            foreach (var p in result.Parameters)
            {
                list.Add(new PluginParameter(
                    p.Id, p.Name, p.Units,
                    p.DefaultValue, p.CurrentValue, p.StepCount,
                    (ParameterFlags)p.Flags));
            }
            // Cache the latest snapshot on the slot so consumers reading
            // ChainSlot.Parameters don't have to round-trip again.
            lock (_gate)
            {
                var prev = _slots[slotIdx];
                _slots[slotIdx] = prev with { Parameters = list };
            }
            return list;
        }
        finally
        {
            _controlGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetSlotParameterAsync(
        int slotIdx, uint paramId, double normalizedValue,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (slotIdx < 0 || slotIdx >= ChainConstants.MaxSlots)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIdx));
        }
        ControlChannel channel;
        lock (_gate)
        {
            if (!IsRunning || _control == null)
            {
                throw new InvalidOperationException("plugin host is not running");
            }
            channel = _control;
        }
        await _controlGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var req = new SlotSetParamRequest((byte)slotIdx, paramId, normalizedValue);
            await channel.SendAsync(ControlTag.SlotSetParam, req.Encode(), ct)
                .ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            var frame = await ReceiveExpected(channel,
                ControlTag.SlotSetParamResult, cts.Token).ConfigureAwait(false);
            if (frame == null) return;
            var result = SlotSetParamResult.Decode(frame.Value.Payload);
            if (result.Status != 0)
            {
                _log.LogWarning(
                    $"PluginHostManager: slot {slotIdx} SetParam id={paramId} " +
                    $"status={result.Status}");
                return;
            }
            // Update the cached parameter list with the actual value (some
            // plugins quantise). If the parameter isn't in the cache, the
            // operator probably hasn't called ListSlotParametersAsync yet
            // — leave the cache alone.
            lock (_gate)
            {
                var prev = _slots[slotIdx];
                if (prev.Parameters.Count > 0)
                {
                    var copy = new List<PluginParameter>(prev.Parameters.Count);
                    foreach (var p in prev.Parameters)
                    {
                        copy.Add(p.Id == paramId
                            ? p with { CurrentValue = result.ActualValue }
                            : p);
                    }
                    _slots[slotIdx] = prev with { Parameters = copy };
                }
            }
        }
        finally
        {
            _controlGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<EditorOpenOutcome> ShowSlotEditorAsync(
        int slotIdx, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (slotIdx < 0 || slotIdx >= ChainConstants.MaxSlots)
        {
            return new EditorOpenOutcome(false, null, null,
                $"slot index out of range (0..{ChainConstants.MaxSlots - 1}): {slotIdx}");
        }
        ControlChannel channel;
        lock (_gate)
        {
            if (!IsRunning || _control == null)
            {
                return new EditorOpenOutcome(false, null, null,
                    "plugin host is not running");
            }
            channel = _control;
        }
        await _controlGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var req = new SlotShowEditorRequest((byte)slotIdx);
            await channel.SendAsync(ControlTag.SlotShowEditor, req.Encode(), ct)
                .ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var frame = await ReceiveExpected(channel,
                ControlTag.SlotShowEditorResult, cts.Token).ConfigureAwait(false);
            if (frame == null)
            {
                return new EditorOpenOutcome(false, null, null,
                    "sidecar closed before SlotShowEditorResult");
            }
            var result = SlotShowEditorResult.Decode(frame.Value.Payload);
            if (result.Status == 0)
            {
                return new EditorOpenOutcome(true,
                    (int)result.Width, (int)result.Height, null);
            }
            string err = result.Status switch
            {
                1 => "no-plugin-loaded",
                2 => "plugin-has-no-editor",
                3 => "platform-not-supported",
                4 => "attach-failed",
                5 => "other",
                6 => "invalid-slot-index",
                7 => "gui-thread-init-failed",
                _ => $"status={result.Status}",
            };
            _log.LogWarning(
                $"PluginHostManager: slot {slotIdx} ShowEditor status={result.Status}");
            return new EditorOpenOutcome(false, null, null, err);
        }
        finally
        {
            _controlGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> HideSlotEditorAsync(
        int slotIdx, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (slotIdx < 0 || slotIdx >= ChainConstants.MaxSlots)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIdx));
        }
        ControlChannel channel;
        lock (_gate)
        {
            if (!IsRunning || _control == null)
            {
                return false;
            }
            channel = _control;
        }
        await _controlGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var req = new SlotHideEditorRequest((byte)slotIdx);
            await channel.SendAsync(ControlTag.SlotHideEditor, req.Encode(), ct)
                .ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var frame = await ReceiveExpected(channel,
                ControlTag.SlotHideEditorResult, cts.Token).ConfigureAwait(false);
            if (frame == null) return false;
            var result = SlotHideEditorResult.Decode(frame.Value.Payload);
            return result.Status == 0;
        }
        finally
        {
            _controlGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetChainEnabledAsync(
        bool enabled, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ControlChannel channel;
        lock (_gate)
        {
            if (!IsRunning || _control == null)
            {
                throw new InvalidOperationException("plugin host is not running");
            }
            channel = _control;
        }
        await _controlGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var req = new SetChainEnabledRequest(enabled);
            await channel.SendAsync(ControlTag.SetChainEnabled, req.Encode(), ct)
                .ConfigureAwait(false);
            // 10s budget — was 2s pre-Wave 7. The sidecar's controlReader is
            // single-threaded; if a slow Load/Unload (e.g. a Linux VST2
            // plugin whose effClose blocks on internal threads) is in flight,
            // the SetChainEnabled reply queues behind it. 10s gives well-
            // behaved plugins room to finish so the operator's chain toggle
            // doesn't fail mid-cleanup.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var frame = await ReceiveExpected(channel,
                ControlTag.SetChainEnabledResult, cts.Token).ConfigureAwait(false);
            if (frame == null) return;
            var result = SetChainEnabledResult.Decode(frame.Value.Payload);
            if (result.Status == 0)
            {
                lock (_gate) { _chainEnabled = enabled; }
            }
            else
            {
                _log.LogWarning(
                    $"PluginHostManager: SetChainEnabled status={result.Status}");
            }
        }
        finally
        {
            _controlGate.Release();
        }
    }

    // Receive the next ControlChannel frame, skipping over Heartbeat /
    // LogLine messages that arrive interleaved with the request/reply
    // pair we are awaiting. Async editor events (0x34 / 0x35) are
    // dispatched via the channel's event API and skipped. Phase 3 GUI
    // wave will likely move the reads to a dedicated background pump
    // so async events don't depend on a sync request being in flight.
    private static async Task<ControlFrame?> ReceiveExpected(
        ControlChannel channel, ControlTag expected, CancellationToken ct)
    {
        while (true)
        {
            var frame = await channel.ReceiveAsync(ct).ConfigureAwait(false);
            if (frame == null) return null;
            if (frame.Value.Tag == expected) return frame;
            // Heartbeat / LogLine / unrelated tags: keep draining.
            if (frame.Value.Tag == ControlTag.Heartbeat ||
                frame.Value.Tag == ControlTag.LogLine)
            {
                continue;
            }
            // Async editor events arrive unsolicited — dispatch + skip.
            if (channel.DispatchAsyncIfApplicable(frame.Value))
            {
                continue;
            }
            // Unexpected tag — surface as an error rather than spinning.
            throw new System.IO.IOException(
                $"PluginHostManager: expected {expected}, got {frame.Value.Tag}");
        }
    }

    private void OnChannelEditorClosed(object? sender, EditorClosedEventArgs e)
    {
        SlotEditorClosed?.Invoke(this, e);
    }

    private void OnChannelEditorResized(object? sender, EditorResizedEventArgs e)
    {
        SlotEditorResized?.Invoke(this, e);
    }

    // Wave 7 — sidecar reported an editor-driven (or plugin-internal) param
    // change. Update the cached ChainSlot.Parameters snapshot so the next
    // persistence flush picks up the new value, then re-fire so listeners
    // (VstHostHostedService for ScheduleSave; future SignalR push) see it.
    private void OnChannelParamChanged(object? sender, ParamChangedEventArgs e)
    {
        // Update the in-memory parameter cache. We mutate the slot record
        // under the same _gate the rest of the chain mutators use so a
        // concurrent ListSlotParameters refresh / Snapshot doesn't see a
        // torn record. If the slot has no cached parameters yet, ignore
        // — ListSlotParameters refreshes the cache on next call.
        if (e.SlotIdx < 0 || e.SlotIdx >= ChainConstants.MaxSlots) return;
        lock (_gate)
        {
            var prev = _slots[e.SlotIdx];
            if (prev.Plugin is null) return;
            if (prev.Parameters.Count == 0) goto raise;
            // Find the param and rebuild the list with the new value.
            // Scan inline — parameter lists are typically <100 entries
            // and this fires per knob-tick, so a dictionary cache would
            // add complexity for negligible gain.
            var list = new List<PluginParameter>(prev.Parameters.Count);
            bool found = false;
            foreach (var p in prev.Parameters)
            {
                if (p.Id == e.ParamId)
                {
                    list.Add(p with { CurrentValue = e.NormalizedValue });
                    found = true;
                }
                else
                {
                    list.Add(p);
                }
            }
            if (found)
            {
                _slots[e.SlotIdx] = prev with { Parameters = list };
            }
        }
        raise:
        SlotParamChanged?.Invoke(this, e);
    }

    private void OnSidecarExited(object? sender, SidecarExitedEventArgs e)
    {
        _log.LogWarning(
            $"PluginHostManager: sidecar pid={e.ProcessId} exited (code={e.ExitCode}); " +
            "marking host as not running.");

        lock (_gate)
        {
            if (_process != null && _process.ProcessId == e.ProcessId)
            {
                _process.Exited -= OnSidecarExited;
                // Don't dispose here — StopAsync owns the disposal sequence
                // so we don't double-close shm/sem state under it.
                // We just null the slot so IsRunning flips immediately.
                _process = null;
            }
            // Loaded plugins (if any) cannot survive a sidecar exit; the
            // next Start cycle will need a fresh Load on every slot.
            for (int i = 0; i < _slots.Length; i++)
            {
                _slots[i] = new ChainSlot(
                    Index: i,
                    Plugin: null,
                    Bypass: false,
                    Parameters: Array.Empty<PluginParameter>());
            }
            _chainEnabled = false;
        }

        // Drop the re-chunking buffers — any in-flight stream alignment is
        // moot now. Best-effort lock acquisition: a concurrent TryProcess
        // may still be holding _txGate, in which case it'll observe the
        // (newly-false) IsRunning state and abort harmlessly. We reset
        // unconditionally to keep the next-launch cycle clean.
        if (_txGate.Wait(0))
        {
            try { ResetRechunkRings(); }
            finally { _txGate.Release(); }
        }
        else
        {
            // Tx gate held by a TryProcess racing the exit — that path will
            // bail out on its own IsRunning check; the next StartAsync
            // resets the rings under the gate.
        }

        // Bump backoff hint for any caller that wants to space restarts.
        var step = Volatile.Read(ref _backoffStep);
        if (step < s_backoffMs.Length - 1)
        {
            Volatile.Write(ref _backoffStep, step + 1);
        }

        SidecarExited?.Invoke(this, e);
    }

    /// <summary>
    /// Current backoff hint in milliseconds. Resets to the first slot on
    /// successful Hello handshake. Exposed for the pipeline service so it
    /// can space restart attempts.
    /// </summary>
    public int CurrentBackoffMs
    {
        get
        {
            var step = Volatile.Read(ref _backoffStep);
            return s_backoffMs[Math.Clamp(step, 0, s_backoffMs.Length - 1)];
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PluginHostManager));
    }

    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            // best-effort
        }
        _disposed = true;
        _txGate.Dispose();
        _controlGate.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best-effort
        }
        _disposed = true;
        _txGate.Dispose();
        _controlGate.Dispose();
    }

    // ---- Phase 2 fixed geometry ----------------------------------------

    public const uint Phase2Frames     = 256;
    public const uint Phase2Channels   = 1;
    public const uint Phase2SampleRate = 48000;
    public const uint Phase2SlotCount  = 8;
}
