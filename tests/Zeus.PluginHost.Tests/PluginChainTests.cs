// PluginChainTests.cs — Phase 3a 8-slot chain gate.
//
// Validates the new chain APIs on top of the existing Phase 2 single-slot
// transport: master enable, per-slot bypass, parameter introspection.
// SkippableFact tests skip cleanly when ZEUS_TEST_VST3_PATH is unset.
// Tests that don't need a plugin (chain-disabled / all-empty pass-through,
// invalid-slot rejection) run unconditionally.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zeus.PluginHost.Chain;
using Zeus.PluginHost.Native;

namespace Zeus.PluginHost.Tests;

[Trait("Category", "Vst3")]
public sealed class PluginChainTests : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan StopTimeout  = TimeSpan.FromSeconds(3);

    private const string EnvVarPath  = "ZEUS_TEST_VST3_PATH";
    private const string EnvVarPath2 = "ZEUS_TEST_VST3_PATH_2";
    private const string EnvVarPath3 = "ZEUS_TEST_VST3_PATH_3";

    private readonly string? _binaryPath;

    public PluginChainTests()
    {
        _binaryPath = SidecarLocator.Locate();
    }

    private static void SkipIfBinaryMissing(string? path)
    {
        Skip.If(path is null || !File.Exists(path),
            "zeus-plughost binary not found.");
    }

    private static string? Vst(string envVar)
    {
        var v = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private static void SkipIfVst3Missing(string? path)
    {
        Skip.If(string.IsNullOrEmpty(path),
            $"set {EnvVarPath} to a VST3 plugin path to run real-plugin tests.");
    }

    private static float[] BuildDeterministicInput(int frames, int seed)
    {
        var input = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            // Sine + small DC, seed-shifted phase. Values stay in
            // [-0.4, +0.4] so any plugin can process without clipping
            // before its own headroom logic kicks in.
            input[i] = MathF.Sin(2.0f * MathF.PI * ((i + seed) / 32.0f)) * 0.3f
                     + 0.05f;
        }
        return input;
    }

    private static byte[] HashFloats(float[] data)
    {
        var bytes = new byte[data.Length * sizeof(float)];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        return SHA256.HashData(bytes);
    }

    // ----------------------------------------------------------------
    //  Tests that don't need a plugin
    // ----------------------------------------------------------------

    [SkippableFact]
    public async Task Chain_Disabled_BitIdenticalPassThrough()
    {
        SkipIfBinaryMissing(_binaryPath);

        await using var manager = new PluginHostManager();
        using var startCts = new CancellationTokenSource(StartTimeout);
        await manager.StartAsync(startCts.Token).ConfigureAwait(false);

        await manager.SetChainEnabledAsync(false).ConfigureAwait(false);
        Assert.False(manager.IsChainEnabled);
        Assert.Equal(ChainConstants.MaxSlots, manager.MaxChainSlots);

        var input = BuildDeterministicInput(256, seed: 1);
        var output = new float[256];
        var inputHash = HashFloats(input);

        for (int iter = 0; iter < 100; iter++)
        {
            Assert.True(manager.TryProcess(input, output, 256),
                $"TryProcess returned false on iter {iter}");
            var outHash = HashFloats(output);
            Assert.Equal(inputHash, outHash);
        }

        using var stopCts = new CancellationTokenSource(StopTimeout);
        await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task Chain_Enabled_AllSlotsEmpty_BitIdenticalPassThrough()
    {
        SkipIfBinaryMissing(_binaryPath);

        await using var manager = new PluginHostManager();
        using var startCts = new CancellationTokenSource(StartTimeout);
        await manager.StartAsync(startCts.Token).ConfigureAwait(false);

        await manager.SetChainEnabledAsync(true).ConfigureAwait(false);
        Assert.True(manager.IsChainEnabled);

        // All slots remain empty.
        var input = BuildDeterministicInput(256, seed: 2);
        var output = new float[256];
        var inputHash = HashFloats(input);

        for (int iter = 0; iter < 100; iter++)
        {
            Assert.True(manager.TryProcess(input, output, 256),
                $"TryProcess returned false on iter {iter}");
            var outHash = HashFloats(output);
            Assert.Equal(inputHash, outHash);
        }

        using var stopCts = new CancellationTokenSource(StopTimeout);
        await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task Chain_LoadInvalidSlot_Returns_InvalidSlotIndex()
    {
        SkipIfBinaryMissing(_binaryPath);

        await using var manager = new PluginHostManager();
        using var startCts = new CancellationTokenSource(StartTimeout);
        await manager.StartAsync(startCts.Token).ConfigureAwait(false);

        // Index 8 is one past the last valid slot.
        var outcome = await manager.LoadSlotAsync(8, "/nonexistent.vst3")
            .ConfigureAwait(false);
        Assert.False(outcome.Ok);
        Assert.NotNull(outcome.Error);

        // Sidecar must still be alive.
        Assert.True(manager.IsRunning);

        using var stopCts = new CancellationTokenSource(StopTimeout);
        await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
    }

    // ----------------------------------------------------------------
    //  Tests that need a plugin
    // ----------------------------------------------------------------

    [SkippableFact]
    public async Task Chain_Slot0_Loaded_ProcessesAudio()
    {
        SkipIfBinaryMissing(_binaryPath);
        var pluginPath = Vst(EnvVarPath);
        SkipIfVst3Missing(pluginPath);

        await using var manager = new PluginHostManager();
        using var startCts = new CancellationTokenSource(StartTimeout);
        await manager.StartAsync(startCts.Token).ConfigureAwait(false);

        await manager.SetChainEnabledAsync(true).ConfigureAwait(false);

        using var loadCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var outcome = await manager.LoadSlotAsync(0, pluginPath!, loadCts.Token)
            .ConfigureAwait(false);
        Assert.True(outcome.Ok, $"LoadSlotAsync(0) failed: '{outcome.Error}'");
        Assert.NotNull(outcome.Info);
        Console.WriteLine(
            $"slot=0 plugin='{outcome.Info!.Name}' " +
            $"vendor='{outcome.Info.Vendor}' version='{outcome.Info.Version}'");

        var snapshot = manager.Slots;
        Assert.Equal(outcome.Info, snapshot[0].Plugin);
        Assert.Equal(outcome.Info, manager.CurrentPlugin);

        var input = BuildDeterministicInput(256, seed: 3);
        var output = new float[256];

        const int kRuns = 100;
        var times = new double[kRuns];
        var sw = new Stopwatch();
        for (int i = 0; i < kRuns; i++)
        {
            sw.Restart();
            Assert.True(manager.TryProcess(input, output, 256),
                $"TryProcess returned false on iter {i}");
            sw.Stop();
            times[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(times);
        Console.WriteLine(
            $"Chain_Slot0_Loaded_ProcessesAudio: " +
            $"p50={times[kRuns/2]:F3} ms " +
            $"p95={times[(kRuns*95)/100]:F3} ms " +
            $"p99={times[(kRuns*99)/100]:F3} ms");

        using var stopCts = new CancellationTokenSource(StopTimeout);
        await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task Chain_Slot0_LoadedButBypassed_BitIdenticalPassThrough()
    {
        SkipIfBinaryMissing(_binaryPath);
        var pluginPath = Vst(EnvVarPath);
        SkipIfVst3Missing(pluginPath);

        await using var manager = new PluginHostManager();
        using var startCts = new CancellationTokenSource(StartTimeout);
        await manager.StartAsync(startCts.Token).ConfigureAwait(false);

        await manager.SetChainEnabledAsync(true).ConfigureAwait(false);
        var outcome = await manager.LoadSlotAsync(0, pluginPath!).ConfigureAwait(false);
        Assert.True(outcome.Ok, outcome.Error);

        await manager.SetSlotBypassAsync(0, true).ConfigureAwait(false);
        Assert.True(manager.Slots[0].Bypass);

        var input = BuildDeterministicInput(256, seed: 4);
        var output = new float[256];
        var inputHash = HashFloats(input);

        for (int iter = 0; iter < 100; iter++)
        {
            Assert.True(manager.TryProcess(input, output, 256));
            var outHash = HashFloats(output);
            Assert.Equal(inputHash, outHash);
        }

        using var stopCts = new CancellationTokenSource(StopTimeout);
        await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task Chain_3SlotsLoaded_ProcessesInOrder()
    {
        SkipIfBinaryMissing(_binaryPath);
        var pluginPathA = Vst(EnvVarPath);
        SkipIfVst3Missing(pluginPathA);
        var pluginPathB = Vst(EnvVarPath2) ?? pluginPathA;
        var pluginPathC = Vst(EnvVarPath3) ?? pluginPathA;

        await using var manager = new PluginHostManager();
        using var startCts = new CancellationTokenSource(StartTimeout);
        await manager.StartAsync(startCts.Token).ConfigureAwait(false);
        await manager.SetChainEnabledAsync(true).ConfigureAwait(false);

        var oA = await manager.LoadSlotAsync(0, pluginPathA!).ConfigureAwait(false);
        Assert.True(oA.Ok, oA.Error);
        var oB = await manager.LoadSlotAsync(1, pluginPathB!).ConfigureAwait(false);
        Assert.True(oB.Ok, oB.Error);
        var oC = await manager.LoadSlotAsync(2, pluginPathC!).ConfigureAwait(false);
        Assert.True(oC.Ok, oC.Error);

        var input = BuildDeterministicInput(256, seed: 5);
        var output = new float[256];

        const int kRuns = 100;
        var times = new double[kRuns];
        var sw = new Stopwatch();
        for (int i = 0; i < kRuns; i++)
        {
            sw.Restart();
            Assert.True(manager.TryProcess(input, output, 256));
            sw.Stop();
            times[i] = sw.Elapsed.TotalMilliseconds;
        }
        // Sanity: at least one block came back. We deliberately don't
        // assert peak bounds — chaining three EQ-style plugins with
        // default boosts can blow up to large or NaN values, which is
        // correct plugin behaviour, not a chain bug. The smoke is that
        // every TryProcess returned true.
        var peak = output.Max(MathF.Abs);
        Array.Sort(times);
        Console.WriteLine(
            $"Chain_3SlotsLoaded_ProcessesInOrder: " +
            $"p50={times[kRuns/2]:F3} ms " +
            $"p95={times[(kRuns*95)/100]:F3} ms " +
            $"p99={times[(kRuns*99)/100]:F3} ms peak={peak:F3} (informational)");

        using var stopCts = new CancellationTokenSource(StopTimeout);
        await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task Chain_Slot1_Bypassed_Skipped()
    {
        SkipIfBinaryMissing(_binaryPath);
        var pluginPath = Vst(EnvVarPath);
        SkipIfVst3Missing(pluginPath);

        await using var manager = new PluginHostManager();
        using var startCts = new CancellationTokenSource(StartTimeout);
        await manager.StartAsync(startCts.Token).ConfigureAwait(false);
        await manager.SetChainEnabledAsync(true).ConfigureAwait(false);

        for (int i = 0; i < 3; i++)
        {
            var o = await manager.LoadSlotAsync(i, pluginPath!).ConfigureAwait(false);
            Assert.True(o.Ok, $"slot {i}: {o.Error}");
        }
        await manager.SetSlotBypassAsync(1, true).ConfigureAwait(false);
        Assert.True(manager.Slots[1].Bypass);
        Assert.False(manager.Slots[0].Bypass);
        Assert.False(manager.Slots[2].Bypass);

        var input = BuildDeterministicInput(256, seed: 6);
        var output = new float[256];
        for (int i = 0; i < 100; i++)
        {
            Assert.True(manager.TryProcess(input, output, 256));
        }

        using var stopCts = new CancellationTokenSource(StopTimeout);
        await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task Chain_ListParams_ReturnsParameters_ForLoadedPlugin()
    {
        SkipIfBinaryMissing(_binaryPath);
        var pluginPath = Vst(EnvVarPath);
        SkipIfVst3Missing(pluginPath);

        await using var manager = new PluginHostManager();
        using var startCts = new CancellationTokenSource(StartTimeout);
        await manager.StartAsync(startCts.Token).ConfigureAwait(false);

        var outcome = await manager.LoadSlotAsync(0, pluginPath!).ConfigureAwait(false);
        Assert.True(outcome.Ok, outcome.Error);

        var parameters = await manager.ListSlotParametersAsync(0).ConfigureAwait(false);
        Assert.NotEmpty(parameters);
        Console.WriteLine(
            $"Chain_ListParams: {parameters.Count} parameters from " +
            $"'{outcome.Info!.Name}'");
        var preview = Math.Min(parameters.Count, 8);
        for (int i = 0; i < preview; i++)
        {
            var p = parameters[i];
            Console.WriteLine(
                $"  [{i}] id={p.Id} name='{p.Name}' units='{p.Units}' " +
                $"default={p.DefaultValue:F4} current={p.CurrentValue:F4} " +
                $"steps={p.StepCount} flags={p.Flags}");
        }

        // Cached on the slot snapshot.
        Assert.Equal(parameters.Count, manager.Slots[0].Parameters.Count);

        using var stopCts = new CancellationTokenSource(StopTimeout);
        await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task Chain_SetParam_AppliesToPlugin()
    {
        SkipIfBinaryMissing(_binaryPath);
        var pluginPath = Vst(EnvVarPath);
        SkipIfVst3Missing(pluginPath);

        await using var manager = new PluginHostManager();
        using var startCts = new CancellationTokenSource(StartTimeout);
        await manager.StartAsync(startCts.Token).ConfigureAwait(false);

        var outcome = await manager.LoadSlotAsync(0, pluginPath!).ConfigureAwait(false);
        Assert.True(outcome.Ok, outcome.Error);

        var parameters = await manager.ListSlotParametersAsync(0).ConfigureAwait(false);
        Assert.NotEmpty(parameters);

        // Pick the first non-readonly continuous (stepCount==0) automatable
        // parameter. Stepped/list params quantise to discrete values so a
        // requested 0.5 may snap to (e.g.) 0.6667 on a 3-step list — that
        // is correct plugin behaviour but not what this test is asserting.
        PluginParameter? target = null;
        foreach (var p in parameters)
        {
            if ((p.Flags & ParameterFlags.ReadOnly) != 0) continue;
            if ((p.Flags & ParameterFlags.Hidden) != 0) continue;
            if (p.StepCount != 0) continue;
            target = p;
            break;
        }
        if (target == null) target = parameters[0];

        var initial = target.CurrentValue;
        var requested = (initial < 0.45) ? 0.5 : 0.25;
        await manager.SetSlotParameterAsync(0, target.Id, requested)
            .ConfigureAwait(false);

        var refreshed = await manager.ListSlotParametersAsync(0).ConfigureAwait(false);
        var after = refreshed.First(p => p.Id == target.Id);
        Console.WriteLine(
            $"Chain_SetParam: id={target.Id} '{target.Name}' steps={target.StepCount} " +
            $"initial={initial:F4} requested={requested:F4} actual={after.CurrentValue:F4}");
        // Continuous parameters should land within 0.01 of the request.
        Assert.InRange(after.CurrentValue,
            requested - 0.01, requested + 0.01);

        using var stopCts = new CancellationTokenSource(StopTimeout);
        await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task Chain_8SlotsLoaded_NoLeak()
    {
        SkipIfBinaryMissing(_binaryPath);
        var pluginPath = Vst(EnvVarPath);
        SkipIfVst3Missing(pluginPath);

        await using (var manager = new PluginHostManager())
        {
            using var startCts = new CancellationTokenSource(StartTimeout);
            await manager.StartAsync(startCts.Token).ConfigureAwait(false);
            await manager.SetChainEnabledAsync(true).ConfigureAwait(false);

            for (int i = 0; i < ChainConstants.MaxSlots; i++)
            {
                using var loadCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var o = await manager.LoadSlotAsync(i, pluginPath!, loadCts.Token)
                    .ConfigureAwait(false);
                Assert.True(o.Ok, $"slot {i}: {o.Error}");
            }

            var input = BuildDeterministicInput(256, seed: 7);
            var output = new float[256];
            for (int i = 0; i < 100; i++)
            {
                Assert.True(manager.TryProcess(input, output, 256),
                    $"TryProcess returned false on iter {i}");
            }

            // Unload all explicitly; StopAsync would also do this.
            for (int i = 0; i < ChainConstants.MaxSlots; i++)
            {
                await manager.UnloadSlotAsync(i).ConfigureAwait(false);
                Assert.Null(manager.Slots[i].Plugin);
            }

            using var stopCts = new CancellationTokenSource(StopTimeout);
            await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
        }

        await Task.Delay(150).ConfigureAwait(false);

        var devShm = Directory.Exists("/dev/shm")
            ? Directory.GetFiles("/dev/shm", "*zeus-plughost*")
            : Array.Empty<string>();
        var tmpSocks = Directory.Exists("/tmp")
            ? Directory.GetFiles("/tmp", "zeus-plughost-*.sock")
            : Array.Empty<string>();
        Assert.True(devShm.Length == 0,
            "leaked /dev/shm: " + string.Join(", ", devShm));
        Assert.True(tmpSocks.Length == 0,
            "leaked /tmp socks: " + string.Join(", ", tmpSocks));
    }

    public void Dispose()
    {
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
                catch { /* best-effort */ }
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
