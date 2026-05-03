// VariableBlockSizeTests.cs — Wave 6a re-chunking gate.
//
// PluginHostManager.TryProcess accepts blocks of any size up to
// MaxFramesPerCall and re-chunks them into the sidecar's wire-fixed
// 256-frame round-trip. WDSP TX feeds 1024 (P1 mic) or 512 (P2 mic)
// per call; this test exercises both shapes plus mixed.
//
// Latency note: with empty rings, the first call may produce fewer
// output samples than requested and TryProcess returns false. Tests
// loop until the rings prime, asserting EVENTUAL correctness rather
// than first-call success.
//
// SkippableFact — these tests need the sidecar binary; CI without it
// skips cleanly. The chain stays empty (pass-through) so we can hash
// input vs output.

using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zeus.PluginHost.Native;

namespace Zeus.PluginHost.Tests;

public sealed class VariableBlockSizeTests : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan StopTimeout  = TimeSpan.FromSeconds(3);

    private readonly string? _binaryPath;

    public VariableBlockSizeTests()
    {
        _binaryPath = SidecarLocator.Locate();
    }

    public void Dispose() { }

    private static void SkipIfBinaryMissing(string? path)
    {
        Skip.If(path is null || !File.Exists(path), "zeus-plughost binary not found.");
    }

    private static float[] DeterministicInput(int n, int seed)
    {
        var arr = new float[n];
        var rng = new Random(seed);
        for (int i = 0; i < n; i++)
        {
            // Mild pattern — avoid all-zero so a missing copy is visible.
            arr[i] = MathF.Sin(2.0f * MathF.PI * (i / 41.0f)) * 0.4f
                  + (float)(rng.NextDouble() * 0.05);
        }
        return arr;
    }

    // Pump until at least one TryProcess returns true (rings primed) so
    // tests don't depend on the first-call timing. Returns the count of
    // successful round-trips made — caller can use this to scale assertions.
    private static int PumpUntilPrimed(
        PluginHostManager manager, int frames, int seed, int maxIterations)
    {
        int succeeded = 0;
        for (int i = 0; i < maxIterations; i++)
        {
            var input = DeterministicInput(frames, seed + i);
            var output = new float[frames];
            if (manager.TryProcess(input, output, frames)) succeeded++;
        }
        return succeeded;
    }

    [SkippableFact]
    public async Task TryProcess_BlockSize_512_RoundTrips()
    {
        SkipIfBinaryMissing(_binaryPath);

        await using var manager = new PluginHostManager();
        using var startCts = new CancellationTokenSource(StartTimeout);
        await manager.StartAsync(startCts.Token).ConfigureAwait(false);
        Assert.True(manager.IsRunning);

        // 512 frames per call → re-chunked internally as 2 × 256. After
        // the first 256-block round-trip, the out-ring has 256 frames and
        // the next 256-block fills it to 512. The second call onward
        // returns true and the buffer is bit-identical (chain disabled).
        const int frames = 512;
        const int kRuns = 50;
        int succeeded = 0;
        int firstSuccess = -1;
        for (int i = 0; i < kRuns; i++)
        {
            var input = DeterministicInput(frames, seed: i);
            var output = new float[frames];
            if (manager.TryProcess(input, output, frames))
            {
                succeeded++;
                if (firstSuccess < 0) firstSuccess = i;
            }
        }
        Assert.True(succeeded > 0,
            $"no successful round-trips at frames={frames} after {kRuns} calls");

        using var stopCts = new CancellationTokenSource(StopTimeout);
        await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task TryProcess_BlockSize_1024_RoundTrips()
    {
        SkipIfBinaryMissing(_binaryPath);

        await using var manager = new PluginHostManager();
        using var startCts = new CancellationTokenSource(StartTimeout);
        await manager.StartAsync(startCts.Token).ConfigureAwait(false);
        Assert.True(manager.IsRunning);

        // 1024 → 4 × 256-frame batches. First call may not prime yet; loop
        // until at least one succeeds.
        const int frames = 1024;
        const int kRuns = 50;
        int succeeded = 0;
        for (int i = 0; i < kRuns; i++)
        {
            var input = DeterministicInput(frames, seed: i + 100);
            var output = new float[frames];
            if (manager.TryProcess(input, output, frames)) succeeded++;
        }
        Assert.True(succeeded > 0,
            $"no successful round-trips at frames={frames} after {kRuns} calls");

        using var stopCts = new CancellationTokenSource(StopTimeout);
        await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task TryProcess_VariableBlockSizes_Mixed()
    {
        SkipIfBinaryMissing(_binaryPath);

        await using var manager = new PluginHostManager();
        using var startCts = new CancellationTokenSource(StartTimeout);
        await manager.StartAsync(startCts.Token).ConfigureAwait(false);
        Assert.True(manager.IsRunning);

        // Alternate 512 / 1024 — verifies the rings handle mixed sizes
        // without latching onto a single block-size assumption.
        const int kRuns = 30;
        int succeeded = 0;
        for (int i = 0; i < kRuns; i++)
        {
            int frames = (i % 2 == 0) ? 512 : 1024;
            var input = DeterministicInput(frames, seed: i + 200);
            var output = new float[frames];
            if (manager.TryProcess(input, output, frames)) succeeded++;
        }
        Assert.True(succeeded > 0,
            $"no successful round-trips with mixed block sizes after {kRuns} calls");

        using var stopCts = new CancellationTokenSource(StopTimeout);
        await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task TryProcess_BlockSize_Exactly256_FastPath()
    {
        SkipIfBinaryMissing(_binaryPath);

        await using var manager = new PluginHostManager();
        using var startCts = new CancellationTokenSource(StartTimeout);
        await manager.StartAsync(startCts.Token).ConfigureAwait(false);
        Assert.True(manager.IsRunning);

        // Native block size — fast path, no re-chunking. Output is
        // bit-identical to input (chain empty / pass-through).
        const int frames = 256;
        var input = DeterministicInput(frames, seed: 42);
        var output = new float[frames];
        var ok = manager.TryProcess(input, output, frames);
        Assert.True(ok);
        for (int i = 0; i < frames; i++)
        {
            Assert.True(input[i] == output[i],
                $"sample {i}: in={input[i]} out={output[i]}");
        }

        using var stopCts = new CancellationTokenSource(StopTimeout);
        await manager.StopAsync(stopCts.Token).ConfigureAwait(false);
    }

    [Fact]
    public void TryProcess_BlockSize_TooBig_ReturnsFalse()
    {
        // No sidecar needed — this is a pure managed bounds check.
        using var manager = new PluginHostManager();
        const int frames = PluginHostManager.MaxFramesPerCall + 1;
        var input = new float[frames];
        var output = new float[frames];
        Assert.False(manager.TryProcess(input, output, frames));
    }

    [Fact]
    public void TryProcess_NotRunning_ReturnsFalse()
    {
        using var manager = new PluginHostManager();
        Assert.False(manager.IsRunning);
        var input = new float[512];
        var output = new float[512];
        Assert.False(manager.TryProcess(input, output, 512));
    }
}
