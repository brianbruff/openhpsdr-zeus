// SidecarLifecycleTests.cs — Phase 1.5 process-supervision gate.
//
// What this validates (from docs/proposals/vst-host.md, the "SIGKILL gate"):
//   (a) Zeus.Server-style host process stays up across a sidecar SIGKILL.
//   (e) The sidecar relaunches cleanly afterwards.
//
// What this does NOT validate, and intentionally so:
//   (b) TX audio degrades gracefully — needs real shm cross-process and a
//       working TryProcess audio loop. Phase 1's PluginHostManager.TryProcess
//       returns false unconditionally; the sidecar's shm is heap-backed.
//   (c) MOX state remains coherent — needs the radio pipeline running.
//   (d) SignalR doesn't disconnect — needs Zeus.Server actually hosted.
// Those land with Phase 2.
//
// The tests deliberately use a small in-test process wrapper rather than
// SidecarProcess.Launch, because SidecarProcess does not (yet) accept argv
// overrides — see the TODO in Zeus.PluginHost/Native/SidecarProcess.cs.
// Re-using SidecarLocator keeps the binary-resolution logic identical to
// production. See the report at the end of the Phase 1.5 task for the API
// gap notes.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zeus.PluginHost.Native;

namespace Zeus.PluginHost.Tests;

public sealed class SidecarLifecycleTests : IDisposable
{
    private static readonly TimeSpan HeartbeatWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(2);

    private readonly string? _binaryPath;

    public SidecarLifecycleTests()
    {
        _binaryPath = SidecarLocator.Locate();
    }

    private static void SkipIfBinaryMissing(string? path)
    {
        Skip.If(
            path is null || !File.Exists(path),
            "zeus-plughost sidecar binary not found. " +
            "Build it at ~/Projects/openhpsdr-zeus-plughost/build/zeus-plughost " +
            "or set ZEUS_PLUGHOST_BIN to its absolute path.");
    }

    [SkippableFact]
    public async Task Sidecar_LaunchesAndIsAlive()
    {
        SkipIfBinaryMissing(_binaryPath);

        IdleSidecar? sidecar = null;
        try
        {
            sidecar = IdleSidecar.Launch(_binaryPath!);
            var sawHeartbeat = await sidecar.WaitForHeartbeatAsync(HeartbeatWait)
                .ConfigureAwait(false);
            Assert.True(sawHeartbeat,
                $"expected --idle heartbeat on stderr within {HeartbeatWait}");
            Assert.True(sidecar.IsRunning, "expected IsRunning == true post-heartbeat");
        }
        finally
        {
            if (sidecar != null)
            {
                await sidecar.StopAsync(ExitWait).ConfigureAwait(false);
                Assert.False(sidecar.IsRunning,
                    "expected IsRunning == false after StopAsync");
                sidecar.Dispose();
            }
        }
    }

    [SkippableFact]
    public async Task Sidecar_SurvivesSIGKILL_AndExitedEventFires()
    {
        SkipIfBinaryMissing(_binaryPath);

        IdleSidecar? sidecar = null;
        try
        {
            sidecar = IdleSidecar.Launch(_binaryPath!);

            var sawHeartbeat = await sidecar.WaitForHeartbeatAsync(HeartbeatWait)
                .ConfigureAwait(false);
            Assert.True(sawHeartbeat, "no heartbeat before SIGKILL");

            var exitedTcs = new TaskCompletionSource<int>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            sidecar.Exited += (_, code) => exitedTcs.TrySetResult(code);

            // Capture the "Zeus.Server analogue" PID — i.e. this test
            // process — so we can prove it stays alive past the kill.
            var hostPid = Environment.ProcessId;

            // Issue SIGKILL via the managed Process API. On Linux this
            // sends SIGKILL; on Windows it's TerminateProcess. Either way
            // the child can't catch it.
            sidecar.KillForcibly();

            var won = await Task.WhenAny(exitedTcs.Task, Task.Delay(ExitWait))
                .ConfigureAwait(false);
            Assert.Same(exitedTcs.Task, won);
            Assert.False(sidecar.IsRunning,
                "expected IsRunning == false once Exited fires");

            // The supervisor invariant: the host process (us) is still
            // alive. If the SIGKILL had taken Zeus.Server with it (e.g.
            // via a misconfigured signal forward), Process.GetProcessById
            // would throw. This is the (a) half of the SIGKILL gate.
            using (var self = Process.GetProcessById(hostPid))
            {
                Assert.False(self.HasExited,
                    "host (test) process must stay alive after sidecar SIGKILL");
            }
        }
        finally
        {
            sidecar?.Dispose();
        }
    }

    [SkippableFact]
    public async Task Sidecar_RelaunchesCleanly_AfterSIGKILL()
    {
        SkipIfBinaryMissing(_binaryPath);

        IdleSidecar? first = null;
        IdleSidecar? second = null;
        try
        {
            first = IdleSidecar.Launch(_binaryPath!);
            Assert.True(
                await first.WaitForHeartbeatAsync(HeartbeatWait)
                    .ConfigureAwait(false),
                "no heartbeat from first sidecar");
            var pid1 = first.ProcessId;

            var firstExitTcs = new TaskCompletionSource<int>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            first.Exited += (_, code) => firstExitTcs.TrySetResult(code);

            first.KillForcibly();
            var won = await Task.WhenAny(firstExitTcs.Task, Task.Delay(ExitWait))
                .ConfigureAwait(false);
            Assert.Same(firstExitTcs.Task, won);

            // Relaunch — fresh process, fresh PID, alive again.
            second = IdleSidecar.Launch(_binaryPath!);
            Assert.True(
                await second.WaitForHeartbeatAsync(HeartbeatWait)
                    .ConfigureAwait(false),
                "no heartbeat from second sidecar");
            var pid2 = second.ProcessId;

            Assert.NotEqual(pid1, pid2);
            Assert.True(second.IsRunning, "second sidecar must be running");
        }
        finally
        {
            if (second != null)
            {
                await second.StopAsync(ExitWait).ConfigureAwait(false);
                second.Dispose();
            }
            first?.Dispose();
        }
    }

    [SkippableFact]
    public void Sidecar_BackoffSurfaceIsExposed()
    {
        SkipIfBinaryMissing(_binaryPath);

        // Light verification: the manager exposes a non-negative backoff
        // hint that callers can read after a kill. We do not exercise
        // the full kill-and-rearm loop here; that needs control-channel
        // wiring (Phase 2). This test only proves the surface exists.
        using var manager = new PluginHostManager();
        var backoff = manager.CurrentBackoffMs;
        Assert.True(
            backoff >= 0,
            $"CurrentBackoffMs must be non-negative, got {backoff}");
    }

    /// <summary>
    /// Class teardown: any leaked sidecar process corrupts CI/dev boxes.
    /// Force-kill orphans and surface a clear failure if any remained.
    /// </summary>
    public void Dispose()
    {
        var leaked = Process.GetProcessesByName("zeus-plughost");
        try
        {
            if (leaked.Length == 0) return;

            foreach (var proc in leaked)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(2000);
                    }
                }
                catch
                {
                    // Best-effort; fall through to the assertion below.
                }
            }

            // After cleanup, surface the leak. Throwing from Dispose is
            // ugly but the alternative (silent orphan accumulation) is
            // worse in a CI/dev loop.
            throw new InvalidOperationException(
                $"SidecarLifecycleTests leaked {leaked.Length} zeus-plughost " +
                "process(es). Killed them in teardown, but a test path is " +
                "skipping its finally-block. Fix the test before running again.");
        }
        finally
        {
            foreach (var proc in leaked)
            {
                proc.Dispose();
            }
        }
    }

    /// <summary>
    /// Minimal in-test sidecar wrapper. Mirrors enough of SidecarProcess
    /// for the lifecycle tests but accepts argv (production
    /// SidecarProcess.Launch does not — see TODO in that file). Using
    /// our own launcher keeps the test isolated from any future Phase 2
    /// changes to SidecarProcess that might add shm/control plumbing.
    /// </summary>
    private sealed class IdleSidecar : IDisposable
    {
        private readonly Process _process;
        private readonly TaskCompletionSource<bool> _firstHeartbeatTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _disposed;

        public int ProcessId { get; }

        public bool IsRunning
        {
            get
            {
                if (_disposed) return false;
                try { return !_process.HasExited; }
                catch (InvalidOperationException) { return false; }
            }
        }

        /// <summary>Fires once with the exit code when the sidecar exits.</summary>
        public event EventHandler<int>? Exited;

        private IdleSidecar(Process process)
        {
            _process = process;
            ProcessId = process.Id;
            _process.EnableRaisingEvents = true;
            _process.Exited += (_, _) =>
            {
                int code;
                try { code = _process.ExitCode; }
                catch { code = -1; }
                Exited?.Invoke(this, code);
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                if (e.Data.Contains("plughost idle pid=", StringComparison.Ordinal))
                {
                    _firstHeartbeatTcs.TrySetResult(true);
                }
            };
            _process.OutputDataReceived += (_, _) => { };
            _process.BeginErrorReadLine();
            _process.BeginOutputReadLine();
        }

        public static IdleSidecar Launch(string binaryPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = binaryPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--idle");

            var proc = new Process { StartInfo = psi };
            if (!proc.Start())
            {
                proc.Dispose();
                throw new InvalidOperationException(
                    $"Process.Start returned false for {binaryPath} --idle");
            }
            return new IdleSidecar(proc);
        }

        public Task<bool> WaitForHeartbeatAsync(TimeSpan timeout)
        {
            return Task.Run(async () =>
            {
                var winner = await Task.WhenAny(
                    _firstHeartbeatTcs.Task,
                    Task.Delay(timeout)).ConfigureAwait(false);
                return winner == _firstHeartbeatTcs.Task && _firstHeartbeatTcs.Task.Result;
            });
        }

        /// <summary>
        /// Send SIGKILL / TerminateProcess. Process.Kill() on Linux uses
        /// SIGKILL by default for forced kills; on Windows it's
        /// TerminateProcess. The child cannot catch either.
        /// </summary>
        public void KillForcibly()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already exited — race between HasExited and Kill.
            }
        }

        /// <summary>
        /// Best-effort SIGTERM-then-Kill. Used by tests that want a
        /// clean shutdown rather than a SIGKILL.
        /// </summary>
        public async Task StopAsync(TimeSpan timeout)
        {
            if (_disposed) return;
            try
            {
                if (!_process.HasExited)
                {
                    // Process.Kill is a forced kill on both OSes; for the
                    // clean-stop path we send a SIGTERM-equivalent: on
                    // Linux that's the close-stdin trick or
                    // posix_kill; on Windows we just fall back to Kill
                    // since CloseMainWindow does not apply to a
                    // console-only process. The sidecar honours SIGTERM,
                    // so on Linux we use it directly.
                    if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                    {
                        try
                        {
                            // 15 == SIGTERM
                            sys_kill(_process.Id, 15);
                        }
                        catch
                        {
                            _process.Kill(entireProcessTree: true);
                        }
                    }
                    else
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                }
            }
            catch (InvalidOperationException) { /* exited already */ }

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { _process.Kill(entireProcessTree: true); } catch { }
            }
        }

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "kill",
            SetLastError = true)]
        private static extern int sys_kill(int pid, int sig);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (!_process.HasExited)
                {
                    try { _process.Kill(entireProcessTree: true); } catch { }
                }
            }
            catch { /* HasExited can throw if handle is already closed */ }
            _process.Dispose();
        }
    }
}
