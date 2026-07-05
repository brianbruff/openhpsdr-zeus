// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.Server;

// Small shutdown sequencer for desktop hosts that must always reach the final
// process-exit action even when a hosted service or plugin teardown wedges.
public sealed class BoundedShutdown
{
    private readonly IReadOnlyList<BoundedShutdownStep> _steps;
    private readonly Action _finalAction;
    private int _started;

    public BoundedShutdown(IEnumerable<BoundedShutdownStep> steps, Action finalAction)
    {
        ArgumentNullException.ThrowIfNull(steps);
        _steps = steps.ToArray();
        _finalAction = finalAction ?? throw new ArgumentNullException(nameof(finalAction));
    }

    public bool Run()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return false;

        try
        {
            foreach (var step in _steps)
                RunStep(step);
        }
        finally
        {
            try { _finalAction(); }
            catch { /* best effort: shutdown has no recovery path */ }
        }

        return true;
    }

    private static void RunStep(BoundedShutdownStep step)
    {
        try
        {
            var task = step.Start();
            if (task is null) return;

            if (step.Timeout is TimeSpan timeout)
            {
                task.Wait(timeout);
                return;
            }

            task.GetAwaiter().GetResult();
        }
        catch
        {
            // Shutdown must keep moving; the final exit action is authoritative.
        }
    }
}

public sealed class BoundedShutdownStep
{
    private readonly Func<Task> _start;

    private BoundedShutdownStep(string name, Func<Task> start, TimeSpan? timeout)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "shutdown step" : name;
        _start = start ?? throw new ArgumentNullException(nameof(start));
        Timeout = timeout;
    }

    public string Name { get; }
    public TimeSpan? Timeout { get; }

    internal Task Start() => _start();

    public static BoundedShutdownStep Run(string name, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return new BoundedShutdownStep(name, () =>
        {
            action();
            return Task.CompletedTask;
        }, timeout: null);
    }

    public static BoundedShutdownStep WaitFor(string name, Func<Task> start, TimeSpan timeout)
        => new(name, start, timeout);
}
