using System.Diagnostics;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class BoundedShutdownTests
{
    [Fact]
    public async Task Run_IsOnceUnderConcurrentCallers()
    {
        int steps = 0;
        int finals = 0;
        using var release = new ManualResetEventSlim();
        var shutdown = new BoundedShutdown(
            new[] { BoundedShutdownStep.Run("step", () => Interlocked.Increment(ref steps)) },
            () => Interlocked.Increment(ref finals));

        var callers = Enumerable.Range(0, 24)
            .Select(_ => Task.Run(() =>
            {
                release.Wait();
                return shutdown.Run();
            }))
            .ToArray();

        release.Set();
        var results = await Task.WhenAll(callers);

        Assert.Equal(1, results.Count(static r => r));
        Assert.Equal(1, steps);
        Assert.Equal(1, finals);
    }

    [Fact]
    public void Run_TimedOutStepDoesNotBlockLaterStepsOrFinalAction()
    {
        var never = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var shutdown = new BoundedShutdown(
            new[]
            {
                BoundedShutdownStep.WaitFor("never", () =>
                {
                    order.Add("never");
                    return never.Task;
                }, TimeSpan.FromMilliseconds(10)),
                BoundedShutdownStep.Run("later", () => order.Add("later")),
            },
            () => order.Add("final"));

        var sw = Stopwatch.StartNew();
        var first = shutdown.Run();
        sw.Stop();

        Assert.True(first);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(500));
        Assert.Equal(new[] { "never", "later", "final" }, order);
    }

    [Fact]
    public void Run_WaitsForCompletingTimedStepBeforeNextStep()
    {
        var delay = TimeSpan.FromMilliseconds(50);
        var order = new List<string>();
        var shutdown = new BoundedShutdown(
            new[]
            {
                BoundedShutdownStep.WaitFor("delayed", () => Task.Run(async () =>
                {
                    order.Add("delayed-start");
                    await Task.Delay(delay).ConfigureAwait(false);
                    order.Add("delayed-complete");
                }), TimeSpan.FromSeconds(1)),
                BoundedShutdownStep.Run("later", () => order.Add("later")),
            },
            () => order.Add("final"));

        var sw = Stopwatch.StartNew();
        var first = shutdown.Run();
        sw.Stop();

        Assert.True(first);
        Assert.True(sw.Elapsed >= delay, $"elapsed {sw.Elapsed} was shorter than {delay}");
        Assert.Equal(new[] { "delayed-start", "delayed-complete", "later", "final" }, order);
    }

    [Fact]
    public void Run_ThrowingStepDoesNotStopChain()
    {
        var order = new List<string>();
        var shutdown = new BoundedShutdown(
            new[]
            {
                BoundedShutdownStep.Run("throw", () =>
                {
                    order.Add("throw");
                    throw new InvalidOperationException("boom");
                }),
                BoundedShutdownStep.Run("later", () => order.Add("later")),
            },
            () => order.Add("final"));

        var first = shutdown.Run();

        Assert.True(first);
        Assert.Equal(new[] { "throw", "later", "final" }, order);
    }

    [Fact]
    public void Run_PreservesStepOrderingBeforeFinalAction()
    {
        var order = new List<string>();
        var shutdown = new BoundedShutdown(
            new[]
            {
                BoundedShutdownStep.Run("one", () => order.Add("one")),
                BoundedShutdownStep.WaitFor("two", () =>
                {
                    order.Add("two");
                    return Task.CompletedTask;
                }, TimeSpan.FromSeconds(1)),
                BoundedShutdownStep.Run("three", () => order.Add("three")),
            },
            () => order.Add("final"));

        var first = shutdown.Run();
        var second = shutdown.Run();

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(new[] { "one", "two", "three", "final" }, order);
    }
}
