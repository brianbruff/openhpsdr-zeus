// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Diagnostics;

namespace Zeus.Server;

/// <summary>
/// Measurement-only TX-turnaround latency observer. Captures, per over, the
/// two edges that bracket the WDSP TX egress path and reports rolling stats:
///
///   1. <b>MOX-assert → first-TX-IQ-at-egress</b> — from the MOX/keydown edge
///      (<see cref="OnMoxEdge"/> rising) to the first block that reaches
///      <c>DspPipelineService.ForwardTxIqToP2</c> (<see cref="OnTxIqEgress"/>).
///   2. <b>PTT-release → egress-drain</b> — from the unkey edge to the last
///      TX-IQ block that reached egress before the stream went quiet.
///
/// This is pure instrumentation: it never gates, delays, or mutates the TX IQ
/// path, and it touches nothing on the RX path. The hot path
/// (<see cref="OnTxIqEgress"/>) is allocation-free and lock-free — at most one
/// interlocked flag flip plus one volatile timestamp write per call. The ring
/// buffers behind the percentile stats are only mutated on the (rare) per-over
/// edges, under a dedicated lightweight lock, so they never touch the
/// sample-rate hot path.
/// </summary>
internal sealed class TxTurnaroundTelemetry
{
    private const int RingCapacity = 64;

    private readonly ILogger _log;
    private readonly object _ringSync = new();
    private readonly LatencyRing _moxToFirstIq = new(RingCapacity);
    private readonly LatencyRing _releaseToDrain = new(RingCapacity);

    // Monotonic Stopwatch ticks. 0 = unset.
    private long _moxAssertTicks;
    private long _releaseTicks;
    private long _lastEgressTicks;
    // 1 while an over is keyed and still awaiting its first egress block.
    private int _firstIqPending;
    // 1 after an unkey edge until the drain latency has been resolved.
    private int _drainPending;
    private long _overSeq;

    // Last resolved values (ms), for cheap read-out without touching the rings.
    private long _lastMoxToFirstIqMicros = -1;
    private long _lastReleaseToDrainMicros = -1;

    public TxTurnaroundTelemetry(ILogger log)
    {
        _log = log;
    }

    /// <summary>
    /// Record a MOX edge. Rising edge (keydown) stamps the MOX-assert instant
    /// and arms the first-IQ measurement; falling edge (unkey) stamps the
    /// release instant and arms the drain measurement.
    /// </summary>
    public void OnMoxEdge(bool on)
    {
        if (on)
        {
            // Resolve any still-pending drain from the previous over before we
            // reuse the shared egress timestamp for the new over.
            ResolvePendingDrain();
            Interlocked.Increment(ref _overSeq);
            Volatile.Write(ref _lastEgressTicks, 0);
            Interlocked.Exchange(ref _moxAssertTicks, Stopwatch.GetTimestamp());
            Volatile.Write(ref _firstIqPending, 1);
        }
        else
        {
            Volatile.Write(ref _firstIqPending, 0);
            Interlocked.Exchange(ref _releaseTicks, Stopwatch.GetTimestamp());
            Volatile.Write(ref _drainPending, 1);
        }
    }

    /// <summary>
    /// Hot-path hook: one TX-IQ block just reached egress
    /// (<c>ForwardTxIqToP2</c>). Allocation-free and lock-free. On the first
    /// block of an over it resolves the MOX→first-IQ latency; every block
    /// refreshes the last-egress timestamp used to resolve the drain latency.
    /// </summary>
    public void OnTxIqEgress()
    {
        var now = Stopwatch.GetTimestamp();
        Volatile.Write(ref _lastEgressTicks, now);

        if (Volatile.Read(ref _firstIqPending) == 0) return;
        // Only the thread that flips the flag records the sample.
        if (Interlocked.Exchange(ref _firstIqPending, 0) == 0) return;

        var moxTicks = Interlocked.Read(ref _moxAssertTicks);
        if (moxTicks <= 0) return;
        var micros = TicksToMicros(now - moxTicks);
        Volatile.Write(ref _lastMoxToFirstIqMicros, micros);
        lock (_ringSync) _moxToFirstIq.Add(micros);
    }

    /// <summary>
    /// Resolve the pending PTT-release → egress-drain latency once the TX-IQ
    /// stream has gone quiet after an unkey. Called lazily (on the next
    /// keydown and on every diagnostics snapshot) so no timer is needed.
    /// </summary>
    public void ResolvePendingDrain()
    {
        if (Volatile.Read(ref _drainPending) == 0) return;
        if (Interlocked.Exchange(ref _drainPending, 0) == 0) return;

        var releaseTicks = Interlocked.Read(ref _releaseTicks);
        var lastEgress = Volatile.Read(ref _lastEgressTicks);
        // If the final egress block landed at/after the unkey edge, drain =
        // last-egress − release; if egress had already stopped before unkey,
        // the stream was already drained (0 ms).
        var drainTicks = lastEgress > releaseTicks ? lastEgress - releaseTicks : 0;
        var micros = TicksToMicros(drainTicks);
        Volatile.Write(ref _lastReleaseToDrainMicros, micros);
        lock (_ringSync) _releaseToDrain.Add(micros);

        // One structured line per completed over so the timing baseline can be
        // captured from the log stream as well as the diagnostics surface.
        _log.LogInformation(
            "p3.tx.turnaround over={Over} moxToFirstIqMs={MoxToFirstIqMs:F3} releaseToDrainMs={ReleaseToDrainMs:F3}",
            Interlocked.Read(ref _overSeq),
            Volatile.Read(ref _lastMoxToFirstIqMicros) / 1000.0,
            micros / 1000.0);
    }

    /// <summary>
    /// Snapshot of both latencies as last value plus p50/p95 (all in
    /// milliseconds). Shape is stable for the diagnostics/Status surface.
    /// </summary>
    public object Snapshot()
    {
        ResolvePendingDrain();

        double lastFirstIqMs, p50FirstIqMs, p95FirstIqMs;
        double lastDrainMs, p50DrainMs, p95DrainMs;
        int firstIqCount, drainCount;
        lock (_ringSync)
        {
            firstIqCount = _moxToFirstIq.Count;
            p50FirstIqMs = _moxToFirstIq.Percentile(0.50) / 1000.0;
            p95FirstIqMs = _moxToFirstIq.Percentile(0.95) / 1000.0;
            drainCount = _releaseToDrain.Count;
            p50DrainMs = _releaseToDrain.Percentile(0.50) / 1000.0;
            p95DrainMs = _releaseToDrain.Percentile(0.95) / 1000.0;
        }
        var lastFirstIqMicros = Volatile.Read(ref _lastMoxToFirstIqMicros);
        var lastDrainMicros = Volatile.Read(ref _lastReleaseToDrainMicros);
        lastFirstIqMs = lastFirstIqMicros >= 0 ? lastFirstIqMicros / 1000.0 : double.NaN;
        lastDrainMs = lastDrainMicros >= 0 ? lastDrainMicros / 1000.0 : double.NaN;

        return new
        {
            overs = Interlocked.Read(ref _overSeq),
            moxToFirstIq = new
            {
                lastMs = double.IsNaN(lastFirstIqMs) ? (double?)null : lastFirstIqMs,
                p50Ms = firstIqCount > 0 ? p50FirstIqMs : (double?)null,
                p95Ms = firstIqCount > 0 ? p95FirstIqMs : (double?)null,
                samples = firstIqCount,
            },
            releaseToDrain = new
            {
                lastMs = double.IsNaN(lastDrainMs) ? (double?)null : lastDrainMs,
                p50Ms = drainCount > 0 ? p50DrainMs : (double?)null,
                p95Ms = drainCount > 0 ? p95DrainMs : (double?)null,
                samples = drainCount,
            },
        };
    }

    private static long TicksToMicros(long ticks) =>
        ticks <= 0 ? 0 : (long)(ticks * 1_000_000.0 / Stopwatch.Frequency);

    /// <summary>
    /// Fixed-capacity latency ring (microseconds). Percentiles are computed
    /// over a small stack-allocated copy so reads never allocate on the heap.
    /// Guarded externally by the owner's ring lock; not thread-safe on its own.
    /// </summary>
    private sealed class LatencyRing
    {
        private readonly long[] _values;
        private int _count;
        private int _next;

        public LatencyRing(int capacity)
        {
            _values = new long[capacity];
        }

        public int Count => _count;

        public void Add(long micros)
        {
            _values[_next] = micros;
            _next = (_next + 1) % _values.Length;
            if (_count < _values.Length) _count++;
        }

        public double Percentile(double q)
        {
            if (_count == 0) return 0;
            Span<long> scratch = stackalloc long[_count];
            for (var i = 0; i < _count; i++) scratch[i] = _values[i];
            scratch.Sort();
            var rank = (int)Math.Ceiling(q * _count) - 1;
            if (rank < 0) rank = 0;
            if (rank >= _count) rank = _count - 1;
            return scratch[rank];
        }
    }
}
