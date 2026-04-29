// PluginHostManager.cs — singleton owner of the sidecar lifecycle.
//
// Responsibilities (Phase 1):
//   - launch the zeus-plughost sidecar binary on demand
//   - watch for unexpected exit (SIGKILL is the load-bearing acceptance
//     gate from docs/proposals/vst-host.md) and restart with backoff
//   - own the host-side ShmRing pair and ControlChannel
//   - validate at startup that BlockHeader is exactly 64 bytes — a
//     mismatch here means the C# and C++ sides have desynchronized and
//     we MUST fail loud rather than silently corrupt audio
//
// Phase 2 will add:
//   - the actual control-channel transport (Win32 named pipe / UDS)
//   - the Hello / Goodbye / Heartbeat exchange
//   - block round-tripping via the SHM rings
//
// Phase 1 ships a TryProcess that returns false (bypass) until the
// sidecar handshake actually runs. The intent is to land the skeleton +
// the lifecycle plumbing now, then wire the audio path on the
// seam-wiring branch without touching this file's public surface.

using System;
using System.Threading;
using System.Threading.Tasks;
using Zeus.PluginHost.Ipc;
using Zeus.PluginHost.Native;

namespace Zeus.PluginHost;

public sealed class PluginHostManager : IPluginHost, IDisposable
{
    private readonly IPluginHostLog _log;
    private readonly object _gate = new();

    // Backoff schedule for unexpected sidecar exits, in milliseconds.
    // Capped at 30 s to prevent a chronically-failing binary from
    // pegging the CPU. The schedule is intentionally short-on-the-front
    // so a SIGKILL during a TX test recovers within one second.
    private static readonly int[] s_backoffMs = { 100, 250, 500, 1_000, 5_000, 30_000 };
    private int _backoffStep;

    private SidecarProcess? _process;
    private CancellationTokenSource? _runCts;
    private bool _disposed;

    public PluginHostManager(IPluginHostLog? log = null)
    {
        _log = log ?? NullPluginHostLog.Instance;

        // Wire-format guard. If the C# struct layout drifts from the C++
        // BlockHeader (e.g. someone adds a field without bumping the
        // protocol version), every audio block thereafter would silently
        // corrupt. Fail at construction instead.
        var actualSize = System.Runtime.InteropServices.Marshal.SizeOf<BlockHeader>();
        if (actualSize != 64)
        {
            throw new InvalidOperationException(
                $"BlockHeader layout drift — C# and C++ must agree on 64 bytes " +
                $"(got {actualSize}). See Zeus.PluginHost/Ipc/BlockHeader.cs and " +
                $"openhpsdr-zeus-plughost/src/audio/block_format.h.");
        }
    }

    /// <inheritdoc />
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _process is { IsAlive: true } && !_disposed;
            }
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_process is { IsAlive: true })
            {
                return Task.CompletedTask; // idempotent
            }

            var path = SidecarLocator.Locate(_log);
            if (path == null)
            {
                throw new InvalidOperationException(
                    "PluginHostManager: zeus-plughost binary not found. " +
                    $"Set {SidecarLocator.EnvVarName} or build the sidecar at " +
                    "~/Projects/openhpsdr-zeus-plughost/build/.");
            }

            var proc = SidecarProcess.Launch(path, _log);
            proc.Exited += OnSidecarExited;
            _process = proc;

            // TODO(phase2): open the control channel + SHM rings here,
            // then await the Hello handshake with a timeout. Today the
            // sidecar pass-through binary doesn't speak control, so we
            // only verify the process spawned successfully.
            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken ct)
    {
        SidecarProcess? proc;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_disposed) return;
            proc = _process;
            cts = _runCts;
            _process = null;
            _runCts = null;
        }
        if (cts != null)
        {
            try { cts.Cancel(); } catch { /* shutdown race */ }
            cts.Dispose();
        }
        if (proc == null) return;

        // TODO(phase2): send Goodbye over the control channel, wait up
        // to 500 ms for a clean exit, then fall through to Kill. For
        // Phase 1 we go straight to the kill path because there is no
        // control channel yet.
        try
        {
            await proc.KillAsync(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        }
        finally
        {
            proc.Exited -= OnSidecarExited;
            proc.Dispose();
        }
    }

    /// <inheritdoc />
    public bool TryProcess(ReadOnlySpan<float> input, Span<float> output, int frames)
    {
        if (_disposed) return false;

        // Phase 1: no audio is actually round-tripped through the sidecar
        // yet (the SHM ring is wired in this branch, the control plane
        // and the audio worker are not). Returning false here means
        // callers fall through to their bypass path. Once the sidecar
        // pump exists, this becomes:
        //
        //   1. acquire a slot on the host->sidecar ring
        //   2. memcpy input into the slot's payload
        //   3. publish; wait on the wakeup primitive
        //   4. read on the sidecar->host ring
        //   5. memcpy payload into output; release
        //
        // Until then we only sanity-check the spans so callers don't
        // observe an inconsistent skeleton.
        if (input.Length < frames || output.Length < frames)
        {
            return false;
        }
        if (!IsRunning)
        {
            return false;
        }
        return false;
    }

    private void OnSidecarExited(object? sender, SidecarExitedEventArgs e)
    {
        // Unexpected exit — the SIGKILL gate. We log loudly, clear the
        // process slot, and let the next TryProcess / StartAsync caller
        // notice that IsRunning has flipped to false.
        //
        // Auto-restart with backoff is intentionally NOT done from this
        // event handler in Phase 1: the caller (DspPipelineService, on
        // the seam-wiring branch) is the right place to decide whether
        // to retry, because it knows whether the radio is even
        // streaming. Here we only mark the slot empty.
        _log.LogWarning(
            $"PluginHostManager: sidecar pid={e.ProcessId} exited (code={e.ExitCode}); " +
            "marking host as not running. Caller may invoke StartAsync to relaunch.");

        lock (_gate)
        {
            if (_process != null && _process.ProcessId == e.ProcessId)
            {
                _process.Exited -= OnSidecarExited;
                _process.Dispose();
                _process = null;
            }
        }

        // Update the backoff step the next StartAsync will honour. The
        // step is a hint for callers that explicitly want the
        // "exponential backoff on flapping sidecar" behaviour; today
        // nothing reads it, but it's exposed below for the seam-wiring
        // branch to consume.
        var step = Volatile.Read(ref _backoffStep);
        if (step < s_backoffMs.Length - 1)
        {
            Volatile.Write(ref _backoffStep, step + 1);
        }
    }

    /// <summary>
    /// Current backoff hint in milliseconds. Resets to the first slot
    /// on a successful Hello handshake (Phase 2). Exposed for the
    /// pipeline service so it can space restart attempts.
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
        _disposed = true;

        SidecarProcess? proc;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            proc = _process;
            cts = _runCts;
            _process = null;
            _runCts = null;
        }
        if (cts != null)
        {
            try { cts.Cancel(); } catch { /* shutdown race */ }
            cts.Dispose();
        }
        if (proc != null)
        {
            proc.Exited -= OnSidecarExited;
            proc.Dispose();
        }
    }
}
