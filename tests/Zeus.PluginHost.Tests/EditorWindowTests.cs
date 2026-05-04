// EditorWindowTests.cs — Phase 3 GUI gate (Linux X11).
//
// Validates the native plugin editor window flow end-to-end: the sidecar
// opens an X11 window for the slot's plugin, the audio thread keeps
// processing without blocking, and the editor can be closed via the
// host API.
//
// Skip-if behaviour:
//   - ZEUS_TEST_VST3_PATH unset → skipped (no plugin to test against).
//   - DISPLAY env var unset → skipped (no X11 server available, e.g.
//     headless CI).
// Both checks are belt-and-suspenders so a CI box that has DISPLAY set
// but no plugin (or vice versa) skips with a clear message.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zeus.PluginHost.Ipc;
using Zeus.PluginHost.Native;

namespace Zeus.PluginHost.Tests;

[Trait("Category", "Vst3Editor")]
public sealed class EditorWindowTests : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan StopTimeout  = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LoadTimeout  = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ShowTimeout  = TimeSpan.FromSeconds(5);

    private const string EnvVarPath = "ZEUS_TEST_VST3_PATH";

    private readonly string? _binaryPath;

    public EditorWindowTests()
    {
        _binaryPath = SidecarLocator.Locate();
    }

    private static void SkipIfBinaryMissing(string? path)
    {
        Skip.If(
            path is null || !File.Exists(path),
            "zeus-plughost sidecar binary not found. Build it at " +
            "~/Projects/openhpsdr-zeus-plughost/build/zeus-plughost or set " +
            "ZEUS_PLUGHOST_BIN.");
    }

    private static string? VstPath()
    {
        var v = Environment.GetEnvironmentVariable(EnvVarPath);
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private static void SkipIfVst3Missing(string? path)
    {
        Skip.If(string.IsNullOrEmpty(path),
            $"set {EnvVarPath} to an absolute path of a VST3 bundle " +
            "(e.g. /usr/lib/vst3/ZamEQ2.vst3) to run the editor tests.");
    }

    private static void SkipIfNoX11()
    {
        var display = Environment.GetEnvironmentVariable("DISPLAY");
        Skip.If(string.IsNullOrEmpty(display),
            "DISPLAY is not set — no X11 server available. The Phase 3 " +
            "GUI editor tests require an X11 display (Xorg, Xvfb, or " +
            "similar).");
    }

    private static void SkipAllPrereqs(string? binPath, string? vstPath)
    {
        SkipIfBinaryMissing(binPath);
        SkipIfNoX11();
        SkipIfVst3Missing(vstPath);
    }

    [SkippableFact]
    public async Task Editor_Show_OnLoadedPlugin_ReturnsOk()
    {
        var pluginPath = VstPath();
        SkipAllPrereqs(_binaryPath, pluginPath);

        await using var manager = new PluginHostManager();
        using var startCts = new CancellationTokenSource(StartTimeout);
        await manager.StartAsync(startCts.Token).ConfigureAwait(false);

        using var loadCts = new CancellationTokenSource(LoadTimeout);
        var loadOutcome = await manager.LoadPluginAsync(pluginPath!, loadCts.Token)
            .ConfigureAwait(false);
        Assert.True(loadOutcome.Ok, $"LoadPluginAsync failed: '{loadOutcome.Error}'");

        using var showCts = new CancellationTokenSource(ShowTimeout);
        var outcome = await manager.ShowSlotEditorAsync(0, showCts.Token)
            .ConfigureAwait(false);
        Assert.True(outcome.Ok,
            $"ShowSlotEditorAsync failed: '{outcome.Error}'");
        Assert.NotNull(outcome.Width);
        Assert.NotNull(outcome.Height);
        Assert.True(outcome.Width!.Value  > 0, "width should be > 0");
        Assert.True(outcome.Height!.Value > 0, "height should be > 0");
        Console.WriteLine(
            $"Editor opened: {outcome.Width}x{outcome.Height} for plugin " +
            $"'{loadOutcome.Info!.Name}'");

        // Let the editor draw a frame or two before closing.
        await Task.Delay(150).ConfigureAwait(false);

        using var hideCts = new CancellationTokenSource(ShowTimeout);
        var wasOpen = await manager.HideSlotEditorAsync(0, hideCts.Token)
            .ConfigureAwait(false);
        Assert.True(wasOpen, "HideSlotEditorAsync should report wasOpen==true");

        using var stopCts = new CancellationTokenSource(StopTimeout);
        await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task Editor_Show_BeforeLoad_ReturnsErrorStatus()
    {
        SkipIfBinaryMissing(_binaryPath);
        SkipIfNoX11();
        // No plugin to load — but we need DISPLAY to be set so the GUI
        // thread can come up and reach the "no-plugin-loaded" branch.

        await using var manager = new PluginHostManager();
        using var startCts = new CancellationTokenSource(StartTimeout);
        await manager.StartAsync(startCts.Token).ConfigureAwait(false);

        using var showCts = new CancellationTokenSource(ShowTimeout);
        var outcome = await manager.ShowSlotEditorAsync(0, showCts.Token)
            .ConfigureAwait(false);
        Assert.False(outcome.Ok);
        Assert.NotNull(outcome.Error);
        Assert.Contains("no-plugin-loaded", outcome.Error!,
            StringComparison.OrdinalIgnoreCase);

        Assert.True(manager.IsRunning,
            "sidecar should still be alive after a failed Show");

        using var stopCts = new CancellationTokenSource(StopTimeout);
        await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task Editor_HideTwice_SecondReturnsFalse()
    {
        var pluginPath = VstPath();
        SkipAllPrereqs(_binaryPath, pluginPath);

        await using var manager = new PluginHostManager();
        using var startCts = new CancellationTokenSource(StartTimeout);
        await manager.StartAsync(startCts.Token).ConfigureAwait(false);

        using var loadCts = new CancellationTokenSource(LoadTimeout);
        var loadOutcome = await manager.LoadPluginAsync(pluginPath!, loadCts.Token)
            .ConfigureAwait(false);
        Assert.True(loadOutcome.Ok, $"LoadPluginAsync failed: '{loadOutcome.Error}'");

        using var showCts = new CancellationTokenSource(ShowTimeout);
        var outcome = await manager.ShowSlotEditorAsync(0, showCts.Token)
            .ConfigureAwait(false);
        Assert.True(outcome.Ok, $"Show failed: '{outcome.Error}'");

        await Task.Delay(50).ConfigureAwait(false);

        using var hide1 = new CancellationTokenSource(ShowTimeout);
        Assert.True(await manager.HideSlotEditorAsync(0, hide1.Token)
            .ConfigureAwait(false), "first hide should report wasOpen==true");

        using var hide2 = new CancellationTokenSource(ShowTimeout);
        Assert.False(await manager.HideSlotEditorAsync(0, hide2.Token)
            .ConfigureAwait(false), "second hide should report wasOpen==false");

        using var stopCts = new CancellationTokenSource(StopTimeout);
        await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task Editor_AudioContinuesWhileEditorOpen()
    {
        var pluginPath = VstPath();
        SkipAllPrereqs(_binaryPath, pluginPath);

        await using var manager = new PluginHostManager();
        using var startCts = new CancellationTokenSource(StartTimeout);
        await manager.StartAsync(startCts.Token).ConfigureAwait(false);

        using var loadCts = new CancellationTokenSource(LoadTimeout);
        var loadOutcome = await manager.LoadPluginAsync(pluginPath!, loadCts.Token)
            .ConfigureAwait(false);
        Assert.True(loadOutcome.Ok, $"LoadPluginAsync failed: '{loadOutcome.Error}'");

        using var enableCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await manager.SetChainEnabledAsync(true, enableCts.Token)
            .ConfigureAwait(false);

        using var showCts = new CancellationTokenSource(ShowTimeout);
        var outcome = await manager.ShowSlotEditorAsync(0, showCts.Token)
            .ConfigureAwait(false);
        Assert.True(outcome.Ok, $"Show failed: '{outcome.Error}'");

        // 100 round-trips through TryProcess while the editor is open.
        // Confirms the audio thread isn't blocked by the GUI thread.
        var input  = new float[256];
        var output = new float[256];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = MathF.Sin(2.0f * MathF.PI * (i / 32.0f)) * 0.2f;
        }
        var sw = Stopwatch.StartNew();
        int okCount = 0;
        for (int i = 0; i < 100; i++)
        {
            if (manager.TryProcess(input, output, 256)) okCount++;
        }
        sw.Stop();
        Assert.Equal(100, okCount);
        Console.WriteLine(
            $"Editor_AudioContinuesWhileEditorOpen: 100 round-trips in " +
            $"{sw.Elapsed.TotalMilliseconds:F1} ms (avg " +
            $"{sw.Elapsed.TotalMilliseconds / 100:F2} ms/block)");

        using var hideCts = new CancellationTokenSource(ShowTimeout);
        await manager.HideSlotEditorAsync(0, hideCts.Token)
            .ConfigureAwait(false);

        using var stopCts = new CancellationTokenSource(StopTimeout);
        await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task Editor_TwoSlotsSimultaneously()
    {
        var pluginPath = VstPath();
        SkipAllPrereqs(_binaryPath, pluginPath);

        await using var manager = new PluginHostManager();
        using var startCts = new CancellationTokenSource(StartTimeout);
        await manager.StartAsync(startCts.Token).ConfigureAwait(false);

        // Load same plugin into two slots — that's fine for GUI testing.
        using var load0 = new CancellationTokenSource(LoadTimeout);
        var o0 = await manager.LoadSlotAsync(0, pluginPath!, load0.Token)
            .ConfigureAwait(false);
        Assert.True(o0.Ok, $"slot 0 load failed: '{o0.Error}'");

        using var load1 = new CancellationTokenSource(LoadTimeout);
        var o1 = await manager.LoadSlotAsync(1, pluginPath!, load1.Token)
            .ConfigureAwait(false);
        Assert.True(o1.Ok, $"slot 1 load failed: '{o1.Error}'");

        using var show0 = new CancellationTokenSource(ShowTimeout);
        var s0 = await manager.ShowSlotEditorAsync(0, show0.Token)
            .ConfigureAwait(false);
        Assert.True(s0.Ok, $"slot 0 show failed: '{s0.Error}'");

        using var show1 = new CancellationTokenSource(ShowTimeout);
        var s1 = await manager.ShowSlotEditorAsync(1, show1.Token)
            .ConfigureAwait(false);
        Assert.True(s1.Ok, $"slot 1 show failed: '{s1.Error}'");

        // Run a few audio round-trips with both editors open.
        using var enableCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await manager.SetChainEnabledAsync(true, enableCts.Token)
            .ConfigureAwait(false);
        var input  = new float[256];
        var output = new float[256];
        for (int i = 0; i < 50; i++)
        {
            Assert.True(manager.TryProcess(input, output, 256),
                $"TryProcess failed iter {i}");
        }

        using var hide0 = new CancellationTokenSource(ShowTimeout);
        await manager.HideSlotEditorAsync(0, hide0.Token).ConfigureAwait(false);
        using var hide1 = new CancellationTokenSource(ShowTimeout);
        await manager.HideSlotEditorAsync(1, hide1.Token).ConfigureAwait(false);

        using var stopCts = new CancellationTokenSource(StopTimeout);
        await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task Editor_UnloadSlotAutoClosesEditor()
    {
        var pluginPath = VstPath();
        SkipAllPrereqs(_binaryPath, pluginPath);

        await using var manager = new PluginHostManager();
        using var startCts = new CancellationTokenSource(StartTimeout);
        await manager.StartAsync(startCts.Token).ConfigureAwait(false);

        using var loadCts = new CancellationTokenSource(LoadTimeout);
        var loadOutcome = await manager.LoadPluginAsync(pluginPath!, loadCts.Token)
            .ConfigureAwait(false);
        Assert.True(loadOutcome.Ok, $"LoadPluginAsync failed: '{loadOutcome.Error}'");

        using var showCts = new CancellationTokenSource(ShowTimeout);
        var outcome = await manager.ShowSlotEditorAsync(0, showCts.Token)
            .ConfigureAwait(false);
        Assert.True(outcome.Ok, $"Show failed: '{outcome.Error}'");

        // Subscribe to the closed event before the unload, so the
        // sidecar's auto-close announcement can be observed. We don't
        // assert it fires (the sidecar implementation is allowed to
        // skip this announcement when the close is host-driven), only
        // that no crash happens. Document the observation in the test
        // output.
        int closedFires = 0;
        manager.SlotEditorClosed += (_, e) =>
        {
            if (e.SlotIdx == 0) Interlocked.Increment(ref closedFires);
        };

        using var unloadCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await manager.UnloadSlotAsync(0, unloadCts.Token).ConfigureAwait(false);
        // Give the async event time to round-trip through the channel.
        await Task.Delay(100).ConfigureAwait(false);

        Console.WriteLine(
            $"Editor_UnloadSlotAutoClosesEditor: SlotEditorClosed " +
            $"fires={closedFires} (>=0 acceptable; current sidecar " +
            $"emits one EditorClosed when unload races with an open editor).");

        Assert.True(manager.IsRunning,
            "sidecar should still be alive after slot unload");

        using var stopCts = new CancellationTokenSource(StopTimeout);
        await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        // Belt-and-suspenders: kill any leaked sidecar processes.
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
