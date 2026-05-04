// RoundTripTests.cs — Phase 2 entry gate.
//
// Validates the cross-process IPC end-to-end: .NET host creates shm rings
// + named semaphores + AF_UNIX control socket, launches the C++ sidecar,
// completes the Hello/HelloAck handshake, runs `TryProcess` repeatedly and
// asserts the bit-identical pass-through behavior. Plus a SIGKILL-mid-flow
// gate, a relaunch-after-crash gate, and a leak-check gate that the suite
// runs LAST (ordering enforced by xunit's test-collection ordering).
//
// Tests skip if the sidecar binary isn't present locally — the build is a
// separate developer responsibility (see openhpsdr-zeus-plughost README).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zeus.PluginHost.Native;

namespace Zeus.PluginHost.Tests;

[TestCaseOrderer("Zeus.PluginHost.Tests.PriorityOrderer", "Zeus.PluginHost.Tests")]
public sealed class RoundTripTests : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan StopTimeout  = TimeSpan.FromSeconds(3);

    private readonly string? _binaryPath;

    public RoundTripTests()
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

    private static float[] DeterministicInput(int n, int seed)
    {
        var arr = new float[n];
        var rng = new Random(seed);
        for (int i = 0; i < n; i++)
        {
            // Sine + uniform noise — enough variation that a missing
            // memcpy or off-by-one would produce a hash mismatch.
            var sine = MathF.Sin(2.0f * MathF.PI * (i / 32.0f));
            var noise = (float)(rng.NextDouble() * 2.0 - 1.0) * 0.1f;
            arr[i] = sine * 0.5f + noise;
        }
        return arr;
    }

    private static byte[] HashFloats(ReadOnlySpan<float> samples)
    {
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples);
        return SHA256.HashData(bytes);
    }

    [SkippableFact, TestPriority(1)]
    public async Task RoundTrip_PassesAudioUnchanged()
    {
        SkipIfBinaryMissing(_binaryPath);

        await using var manager = new PluginHostManager();
        using var startCts = new CancellationTokenSource(StartTimeout);
        await manager.StartAsync(startCts.Token).ConfigureAwait(false);
        Assert.True(manager.IsRunning, "manager should be running after StartAsync");

        var input = DeterministicInput(256, seed: 42);
        var output = new float[256];
        var ok = manager.TryProcess(input, output, 256);
        Assert.True(ok, "TryProcess returned false on round-trip 0");

        // Per-sample assertion produces a more useful failure message than a
        // raw SHA256 mismatch when something diverges (it surfaces the first
        // index that drifted). We hash afterwards as a stronger guarantee.
        for (int i = 0; i < 256; i++)
        {
            Assert.True(input[i] == output[i],
                $"sample {i} differs: in={input[i]:F6} out={output[i]:F6}");
        }
        var inHash  = HashFloats(input);
        var outHash = HashFloats(output);
        Assert.Equal(inHash, outHash);

        using var stopCts = new CancellationTokenSource(StopTimeout);
        await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
    }

    [SkippableFact, TestPriority(2)]
    public async Task RoundTrip_LatencyUnder50ms()
    {
        SkipIfBinaryMissing(_binaryPath);

        await using var manager = new PluginHostManager();
        using var startCts = new CancellationTokenSource(StartTimeout);
        await manager.StartAsync(startCts.Token).ConfigureAwait(false);
        Assert.True(manager.IsRunning);

        var input  = DeterministicInput(256, seed: 7);
        var output = new float[256];

        const int kRuns = 100;
        var times = new double[kRuns];
        var sw = new Stopwatch();
        for (int i = 0; i < kRuns; i++)
        {
            sw.Restart();
            var ok = manager.TryProcess(input, output, 256);
            sw.Stop();
            Assert.True(ok, $"TryProcess returned false on iteration {i}");
            times[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(times);
        var p50 = times[kRuns / 2];
        var p95 = times[(kRuns * 95) / 100];
        var p99 = times[(kRuns * 99) / 100];

        Console.WriteLine(
            $"RoundTrip_LatencyUnder50ms: p50={p50:F3} ms p95={p95:F3} ms p99={p99:F3} ms");
        Assert.True(p99 < 50.0,
            $"p99 latency {p99:F3} ms exceeds Phase 2 budget of 50 ms");

        using var stopCts = new CancellationTokenSource(StopTimeout);
        await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
    }

    [SkippableFact, TestPriority(3)]
    public async Task SIGKILL_DuringAudioFlow_HostStaysUp()
    {
        SkipIfBinaryMissing(_binaryPath);

        await using var manager = new PluginHostManager();
        using var startCts = new CancellationTokenSource(StartTimeout);
        await manager.StartAsync(startCts.Token).ConfigureAwait(false);
        Assert.True(manager.IsRunning);

        var startPid = manager.CurrentProcessId;
        Assert.NotNull(startPid);

        var hostPid = Environment.ProcessId;
        var exitedTcs = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        manager.SidecarExited += (_, e) => exitedTcs.TrySetResult(e.ExitCode);

        var input = DeterministicInput(256, seed: 99);
        var output = new float[256];

        // Drive ~10 successful round-trips to confirm the loop is alive.
        for (int i = 0; i < 10; i++)
        {
            var ok = manager.TryProcess(input, output, 256);
            Assert.True(ok, $"TryProcess returned false on warm-up iter {i}");
        }

        // SIGKILL the sidecar via Process.Kill (no signal handler can catch).
        using (var p = Process.GetProcessById(startPid!.Value))
        {
            p.Kill(entireProcessTree: true);
        }

        // SidecarExited should fire within ~1 s.
        var exitWon = await Task.WhenAny(exitedTcs.Task, Task.Delay(2000))
            .ConfigureAwait(false);
        Assert.Same(exitedTcs.Task, exitWon);

        // Subsequent TryProcess returns false within 100 ms (semaphore timeout).
        var sw = Stopwatch.StartNew();
        bool sawFalse = false;
        while (sw.ElapsedMilliseconds < 1000)
        {
            if (!manager.TryProcess(input, output, 256))
            {
                sawFalse = true;
                break;
            }
        }
        sw.Stop();
        Assert.True(sawFalse, "TryProcess should return false after sidecar SIGKILL");
        Assert.False(manager.IsRunning, "IsRunning should flip to false after exit");

        // Host (this test process) is still alive.
        using (var self = Process.GetProcessById(hostPid))
        {
            Assert.False(self.HasExited);
        }

        using var stopCts = new CancellationTokenSource(StopTimeout);
        await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
    }

    [SkippableFact, TestPriority(4)]
    public async Task RestartAfterCrash_ResumesRoundTrip()
    {
        SkipIfBinaryMissing(_binaryPath);

        await using var manager = new PluginHostManager();
        using var startCts1 = new CancellationTokenSource(StartTimeout);
        await manager.StartAsync(startCts1.Token).ConfigureAwait(false);
        var pid1 = manager.CurrentProcessId;
        Assert.NotNull(pid1);

        // Kill the first sidecar.
        var exitedTcs = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        manager.SidecarExited += (_, e) => exitedTcs.TrySetResult(e.ExitCode);
        using (var p = Process.GetProcessById(pid1!.Value))
        {
            p.Kill(entireProcessTree: true);
        }
        await Task.WhenAny(exitedTcs.Task, Task.Delay(2000)).ConfigureAwait(false);

        // Stop cleans up the dead process state. Then restart fresh.
        using (var stopCts = new CancellationTokenSource(StopTimeout))
        {
            await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
        }

        using var startCts2 = new CancellationTokenSource(StartTimeout);
        await manager.StartAsync(startCts2.Token).ConfigureAwait(false);
        var pid2 = manager.CurrentProcessId;
        Assert.NotNull(pid2);
        Assert.NotEqual(pid1.Value, pid2!.Value);
        Assert.True(manager.IsRunning);

        var input = DeterministicInput(256, seed: 13);
        var output = new float[256];
        for (int i = 0; i < 10; i++)
        {
            var ok = manager.TryProcess(input, output, 256);
            Assert.True(ok, $"TryProcess returned false on iter {i} after restart");
        }
        var inHash  = HashFloats(input);
        var outHash = HashFloats(output);
        Assert.Equal(inHash, outHash);

        using var stopCts2 = new CancellationTokenSource(StopTimeout);
        await manager.StopAsync(stopCts2.Token).ConfigureAwait(false);
    }

    [SkippableFact, TestPriority(99)]
    public async Task NoLeakedShmOrSocket_AfterTeardown()
    {
        SkipIfBinaryMissing(_binaryPath);

        // Bring up + tear down once to exercise the cleanup path.
        await using (var manager = new PluginHostManager())
        {
            using var startCts = new CancellationTokenSource(StartTimeout);
            await manager.StartAsync(startCts.Token).ConfigureAwait(false);
            using var stopCts = new CancellationTokenSource(StopTimeout);
            await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
        }

        // Allow a moment for kernel inode reclaim.
        await Task.Delay(100).ConfigureAwait(false);

        var devShm = Directory.Exists("/dev/shm")
            ? Directory.GetFiles("/dev/shm", "*zeus-plughost*")
            : Array.Empty<string>();
        var tmpSocks = Directory.Exists("/tmp")
            ? Directory.GetFiles("/tmp", "zeus-plughost-*.sock")
            : Array.Empty<string>();

        Assert.True(devShm.Length == 0,
            "leaked /dev/shm entries: " + string.Join(", ", devShm));
        Assert.True(tmpSocks.Length == 0,
            "leaked /tmp socket files: " + string.Join(", ", tmpSocks));
    }

    public void Dispose()
    {
        // Belt-and-suspenders: kill any leaked sidecar.
        var leaked = Process.GetProcessesByName("zeus-plughost");
        try
        {
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
                    // best-effort
                }
            }
        }
        finally
        {
            foreach (var p in leaked) p.Dispose();
        }

        // Also unlink any stragglers we own.
        try
        {
            if (Directory.Exists("/dev/shm"))
            {
                foreach (var f in Directory.GetFiles("/dev/shm", "*zeus-plughost*"))
                {
                    try { File.Delete(f); } catch { /* best-effort */ }
                }
            }
            if (Directory.Exists("/tmp"))
            {
                foreach (var f in Directory.GetFiles("/tmp", "zeus-plughost-*.sock"))
                {
                    try { File.Delete(f); } catch { /* best-effort */ }
                }
            }
        }
        catch { /* best-effort */ }
    }
}

/// <summary>Test-priority attribute for ordering tests within RoundTripTests.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class TestPriorityAttribute : Attribute
{
    public int Priority { get; }
    public TestPriorityAttribute(int priority) { Priority = priority; }
}

/// <summary>
/// xunit test-case orderer. Reads <see cref="TestPriorityAttribute"/> from
/// each test method and sorts ascending so the "no leaks" check (priority 99)
/// runs LAST in the class, after all setup/teardown of the audio-flow tests.
/// </summary>
public sealed class PriorityOrderer : Xunit.Sdk.ITestCaseOrderer
{
    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
        where TTestCase : Xunit.Abstractions.ITestCase
    {
        return testCases.OrderBy(tc =>
        {
            var attr = tc.TestMethod.Method.GetCustomAttributes(
                typeof(TestPriorityAttribute).AssemblyQualifiedName!).FirstOrDefault();
            return attr?.GetNamedArgument<int>(nameof(TestPriorityAttribute.Priority)) ?? int.MaxValue;
        });
    }
}
