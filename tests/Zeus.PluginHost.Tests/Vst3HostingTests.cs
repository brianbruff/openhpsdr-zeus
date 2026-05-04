// Vst3HostingTests.cs — Phase 2 (real) gate.
//
// Validates the cross-process VST3 plugin lifecycle: .NET host launches
// the sidecar, sends LoadPlugin with a path provided via the
// ZEUS_TEST_VST3_PATH environment variable, awaits LoadPluginResult,
// then exercises round-trip processing through the loaded plugin.
//
// All "happy path" tests are SkippableFact and skip cleanly when
// ZEUS_TEST_VST3_PATH is unset — the CI environment doesn't ship a
// VST3 plugin, and this test suite is about wiring, not Steinberg
// dependency. The error-path test (invalid path) does NOT skip; it
// must pass in every environment.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zeus.PluginHost.Native;

namespace Zeus.PluginHost.Tests;

[Trait("Category", "Vst3")]
public sealed class Vst3HostingTests : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan StopTimeout  = TimeSpan.FromSeconds(3);

    private const string EnvVarPath  = "ZEUS_TEST_VST3_PATH";
    private const string EnvVarPath2 = "ZEUS_TEST_VST3_PATH_2";

    private readonly string? _binaryPath;

    public Vst3HostingTests()
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

    private static string? VstPath()
    {
        var v = Environment.GetEnvironmentVariable(EnvVarPath);
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private static string? VstPath2()
    {
        var v = Environment.GetEnvironmentVariable(EnvVarPath2);
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private static void SkipIfVst3Missing(string? path)
    {
        Skip.If(string.IsNullOrEmpty(path),
            $"set {EnvVarPath} to an absolute path of a VST3 bundle " +
            "(e.g. /usr/lib/vst3/ZamEQ2.vst3 on Debian/Ubuntu) to run the " +
            "real-plugin test path.");
    }

    [SkippableFact]
    public async Task Vst3_Load_FromEnvironmentPath_Succeeds()
    {
        SkipIfBinaryMissing(_binaryPath);
        var pluginPath = VstPath();
        SkipIfVst3Missing(pluginPath);

        await using var manager = new PluginHostManager();
        using var startCts = new CancellationTokenSource(StartTimeout);
        await manager.StartAsync(startCts.Token).ConfigureAwait(false);
        Assert.True(manager.IsRunning, "manager should be running after StartAsync");

        using var loadCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var outcome = await manager.LoadPluginAsync(pluginPath!, loadCts.Token)
            .ConfigureAwait(false);
        Assert.True(outcome.Ok,
            $"LoadPluginAsync failed: status-error='{outcome.Error}'");
        Assert.NotNull(outcome.Info);
        Console.WriteLine(
            $"Loaded plugin name='{outcome.Info!.Name}' " +
            $"vendor='{outcome.Info.Vendor}' " +
            $"version='{outcome.Info.Version}'");
        Assert.Equal(outcome.Info, manager.CurrentPlugin);

        using var unloadCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await manager.UnloadPluginAsync(unloadCts.Token).ConfigureAwait(false);
        Assert.Null(manager.CurrentPlugin);

        using var stopCts = new CancellationTokenSource(StopTimeout);
        await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task Vst3_LoadProcess100Blocks_NoCrash()
    {
        SkipIfBinaryMissing(_binaryPath);
        var pluginPath = VstPath();
        SkipIfVst3Missing(pluginPath);

        await using var manager = new PluginHostManager();
        using var startCts = new CancellationTokenSource(StartTimeout);
        await manager.StartAsync(startCts.Token).ConfigureAwait(false);

        using var loadCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var outcome = await manager.LoadPluginAsync(pluginPath!, loadCts.Token)
            .ConfigureAwait(false);
        Assert.True(outcome.Ok, $"LoadPluginAsync failed: '{outcome.Error}'");

        // Deterministic input — sine + a small DC offset so a plugin that
        // does ANY processing (filter, gain, dynamics, etc.) produces a
        // bit-different output. We don't assert that, just that we got
        // something back without a crash on each block.
        var input = new float[256];
        var output = new float[256];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = MathF.Sin(2.0f * MathF.PI * (i / 32.0f)) * 0.3f + 0.05f;
        }

        const int kRuns = 100;
        var times = new double[kRuns];
        var sw = new Stopwatch();
        int okCount = 0;
        for (int i = 0; i < kRuns; i++)
        {
            sw.Restart();
            var ok = manager.TryProcess(input, output, 256);
            sw.Stop();
            Assert.True(ok, $"TryProcess returned false on iteration {i}");
            times[i] = sw.Elapsed.TotalMilliseconds;
            if (ok) okCount++;
        }
        Assert.Equal(kRuns, okCount);

        Array.Sort(times);
        var p50 = times[kRuns / 2];
        var p95 = times[(kRuns * 95) / 100];
        var p99 = times[(kRuns * 99) / 100];
        Console.WriteLine(
            $"Vst3_LoadProcess100Blocks_NoCrash: p50={p50:F3} ms " +
            $"p95={p95:F3} ms p99={p99:F3} ms (plugin='{outcome.Info!.Name}')");

        using var unloadCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await manager.UnloadPluginAsync(unloadCts.Token).ConfigureAwait(false);
        using var stopCts = new CancellationTokenSource(StopTimeout);
        await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task Vst3_Reload_DifferentPlugin_Works()
    {
        SkipIfBinaryMissing(_binaryPath);
        var pluginPathA = VstPath();
        SkipIfVst3Missing(pluginPathA);

        await using var manager = new PluginHostManager();
        using var startCts = new CancellationTokenSource(StartTimeout);
        await manager.StartAsync(startCts.Token).ConfigureAwait(false);

        // Load A.
        using var loadCtsA = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var outcomeA = await manager.LoadPluginAsync(pluginPathA!, loadCtsA.Token)
            .ConfigureAwait(false);
        Assert.True(outcomeA.Ok, $"first load failed: '{outcomeA.Error}'");

        var input = new float[256];
        var output = new float[256];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = MathF.Sin(2.0f * MathF.PI * (i / 17.0f)) * 0.2f;
        }
        for (int i = 0; i < 10; i++)
        {
            Assert.True(manager.TryProcess(input, output, 256),
                $"TryProcess returned false on first-plugin iter {i}");
        }

        // Re-load (same path, or a second one if provided). The internal
        // contract is "Unload then Load"; CurrentPlugin is briefly null
        // mid-flight and then non-null again.
        var pluginPathB = VstPath2() ?? pluginPathA;
        using var loadCtsB = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var outcomeB = await manager.LoadPluginAsync(pluginPathB!, loadCtsB.Token)
            .ConfigureAwait(false);
        Assert.True(outcomeB.Ok, $"second load failed: '{outcomeB.Error}'");
        Console.WriteLine(
            $"Vst3_Reload_DifferentPlugin_Works: " +
            $"A='{outcomeA.Info!.Name}' B='{outcomeB.Info!.Name}'");

        for (int i = 0; i < 10; i++)
        {
            Assert.True(manager.TryProcess(input, output, 256),
                $"TryProcess returned false on second-plugin iter {i}");
        }

        using var unloadCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await manager.UnloadPluginAsync(unloadCts.Token).ConfigureAwait(false);
        using var stopCts = new CancellationTokenSource(StopTimeout);
        await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task Vst3_NoLeak_AfterLoadProcessUnload()
    {
        SkipIfBinaryMissing(_binaryPath);
        var pluginPath = VstPath();
        SkipIfVst3Missing(pluginPath);

        await using (var manager = new PluginHostManager())
        {
            using var startCts = new CancellationTokenSource(StartTimeout);
            await manager.StartAsync(startCts.Token).ConfigureAwait(false);

            using var loadCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var outcome = await manager.LoadPluginAsync(pluginPath!, loadCts.Token)
                .ConfigureAwait(false);
            Assert.True(outcome.Ok, $"LoadPluginAsync failed: '{outcome.Error}'");

            var input = new float[256];
            var output = new float[256];
            for (int i = 0; i < input.Length; i++)
            {
                input[i] = MathF.Sin(2.0f * MathF.PI * (i / 64.0f)) * 0.1f;
            }
            for (int i = 0; i < 100; i++)
            {
                Assert.True(manager.TryProcess(input, output, 256),
                    $"TryProcess iter {i}");
            }

            using var unloadCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await manager.UnloadPluginAsync(unloadCts.Token).ConfigureAwait(false);
            using var stopCts = new CancellationTokenSource(StopTimeout);
            await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
        }

        // Allow the kernel a moment to reclaim names.
        await Task.Delay(150).ConfigureAwait(false);

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

    [SkippableFact]
    public async Task Vst3_LoadInvalidPath_ReturnsErrorOutcome_DoesNotCrash()
    {
        // No SkipIfVst3Missing — this test is about the error path and
        // must pass in every environment that has the sidecar built.
        SkipIfBinaryMissing(_binaryPath);

        await using var manager = new PluginHostManager();
        using var startCts = new CancellationTokenSource(StartTimeout);
        await manager.StartAsync(startCts.Token).ConfigureAwait(false);

        using var loadCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var outcome = await manager.LoadPluginAsync(
            "/nonexistent/path.vst3", loadCts.Token).ConfigureAwait(false);
        Assert.False(outcome.Ok);
        Assert.NotNull(outcome.Error);
        Assert.Null(manager.CurrentPlugin);

        // Sidecar must still be alive and pass-through must still work.
        Assert.True(manager.IsRunning,
            "sidecar should be alive after a failed LoadPlugin");
        var input = new float[256];
        var output = new float[256];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (i & 1) == 0 ? 0.25f : -0.25f;
        }
        Assert.True(manager.TryProcess(input, output, 256));
        for (int i = 0; i < input.Length; i++)
        {
            Assert.Equal(input[i], output[i]);
        }

        using var stopCts = new CancellationTokenSource(StopTimeout);
        await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        // Belt-and-suspenders: kill any leaked sidecar.
        var leaked = Process.GetProcessesByName("zeus-plughost").ToArray();
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
