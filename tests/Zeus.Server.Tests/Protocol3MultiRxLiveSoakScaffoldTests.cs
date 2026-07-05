// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Multi-RX (>1 DDC stream) P3 acceptance scaffold. This is deliberately NOT
// a live test: it needs a real Saturn/OrionMkII-class radio and a real n9dsp
// sidecar process, neither of which is available in this unit-test project
// (the live Zeus stack is stopped for the duration of this test-authoring
// pass, and Zeus.Server.Tests has no handle to a physical radio). Running it
// for real is the team lead's job, against live hardware.
//
// What lives here is the acceptance CONTRACT — the assertions a live soak
// run must satisfy — expressed as a compiling, executable harness so the
// shape of "what does success look like" is pinned down in code, not just
// prose. The [Fact] below is Skipped (not merely commented out) so it shows
// up in `dotnet test` output as a visible, named placeholder instead of
// silently vanishing, and so CI/local runs never spend time or hardware on
// it by accident.
//
// Acceptance criteria this scaffold encodes (see LiveSoakResult below):
//   1. >= 2 DDC streams live simultaneously (RX1 + at least one secondary).
//   2. Interleaved sample pairs across those streams stay time-aligned
//      (no cross-channel sample drift beyond the negotiated frame period).
//   3. Per-channel meters (RxMeterFrame / RxMetersV2Frame) and display
//      frames stay sane (finite, non-sentinel while the channel is live)
//      for a sustained >60s soak — the duration long enough to surface the
//      slow leaks/resets that a few-second smoke test would miss.

using Zeus.Contracts;

namespace Zeus.Server.Tests;

public sealed class Protocol3MultiRxLiveSoakScaffoldTests
{
    /// <summary>
    /// Minimum simultaneous DDC streams the acceptance run must observe live
    /// (RX1 plus at least one secondary receiver).
    /// </summary>
    private const int MinLiveDdcStreams = 2;

    /// <summary>Minimum soak duration, in seconds, before a pass is credited.</summary>
    private const int MinSoakDurationSeconds = 60;

    /// <summary>
    /// n9dsp "no data" sentinel for RX meter dBFS — see
    /// Protocol3SidecarFrameForwarder.PublishRxMetersFromFrame and
    /// DspPipelineService.ApplyRxMeterCalibration, both of which treat
    /// values at/below this as "not real data."
    /// </summary>
    private const double RxMeterSentinelDbfs = -199.5;

    /// <summary>
    /// A single channel's observations gathered over the soak window. Populated
    /// by a live harness driving the real sidecar; every field defaults to a
    /// "not observed" shape so an incomplete/aborted run fails loudly instead
    /// of reading as a false pass.
    /// </summary>
    public sealed record ChannelSoakSample(
        int RxId,
        bool StreamLive,
        int SamplesObserved,
        double MaxInterChannelSampleDriftMs,
        double LastRxMeterDbfs,
        bool LastDisplayFrameFinite);

    public sealed record LiveSoakResult(
        TimeSpan Duration,
        IReadOnlyList<ChannelSoakSample> Channels);

    /// <summary>
    /// The acceptance predicate a live run's <see cref="LiveSoakResult"/> must
    /// satisfy. Pure and unit-testable on its own (see the two [Fact]s below)
    /// even though nothing in this project can produce a real
    /// <see cref="LiveSoakResult"/> from live hardware.
    /// </summary>
    internal static bool MeetsMultiRxAcceptanceCriteria(LiveSoakResult result, out string reason)
    {
        if (result.Duration < TimeSpan.FromSeconds(MinSoakDurationSeconds))
        {
            reason = $"soak duration {result.Duration.TotalSeconds:F0}s is below the {MinSoakDurationSeconds}s minimum";
            return false;
        }

        var liveChannels = result.Channels.Where(c => c.StreamLive).ToArray();
        if (liveChannels.Length < MinLiveDdcStreams)
        {
            reason = $"only {liveChannels.Length} live DDC stream(s) observed, need >= {MinLiveDdcStreams}";
            return false;
        }

        foreach (var channel in liveChannels)
        {
            if (channel.SamplesObserved <= 0)
            {
                reason = $"rx{channel.RxId} reported live but produced zero samples";
                return false;
            }
            if (!channel.LastDisplayFrameFinite)
            {
                reason = $"rx{channel.RxId} last display frame contained non-finite bins";
                return false;
            }
            if (channel.LastRxMeterDbfs <= RxMeterSentinelDbfs)
            {
                reason = $"rx{channel.RxId} meter is stuck at/below the n9dsp sentinel ({RxMeterSentinelDbfs} dBFS)";
                return false;
            }
        }

        // Interleave alignment: every live channel's worst observed drift
        // against its peers must stay within one negotiated frame period.
        // 1_536_000 Hz is the standard P3 multi-RX sample rate used
        // elsewhere in this suite (see RadioServiceUnifiedReceiverWriteTests);
        // a frame period at that rate is well under a millisecond, so 5ms is
        // a generous, clearly-broken-if-exceeded bound rather than a tight
        // hardware-timing assertion.
        const double maxAllowedDriftMs = 5.0;
        var worstDrift = liveChannels.Max(c => c.MaxInterChannelSampleDriftMs);
        if (worstDrift > maxAllowedDriftMs)
        {
            reason = $"worst inter-channel sample drift {worstDrift:F2}ms exceeds {maxAllowedDriftMs:F2}ms";
            return false;
        }

        reason = "ok";
        return true;
    }

    [Fact]
    public void AcceptanceCriteria_RejectsResultWithOnlyOneLiveDdcStream()
    {
        var result = new LiveSoakResult(
            Duration: TimeSpan.FromSeconds(90),
            Channels: new[]
            {
                new ChannelSoakSample(RxId: 0, StreamLive: true, SamplesObserved: 1000,
                    MaxInterChannelSampleDriftMs: 0.1, LastRxMeterDbfs: -73.0, LastDisplayFrameFinite: true),
                new ChannelSoakSample(RxId: 1, StreamLive: false, SamplesObserved: 0,
                    MaxInterChannelSampleDriftMs: 0.0, LastRxMeterDbfs: RxMeterSentinelDbfs, LastDisplayFrameFinite: false),
            });

        Assert.False(MeetsMultiRxAcceptanceCriteria(result, out var reason));
        Assert.Contains("live DDC stream", reason);
    }

    [Fact]
    public void AcceptanceCriteria_RejectsShortSoakEvenWithHealthyChannels()
    {
        var result = new LiveSoakResult(
            Duration: TimeSpan.FromSeconds(10),
            Channels: new[]
            {
                new ChannelSoakSample(0, true, 1000, 0.1, -73.0, true),
                new ChannelSoakSample(1, true, 1000, 0.1, -80.0, true),
            });

        Assert.False(MeetsMultiRxAcceptanceCriteria(result, out var reason));
        Assert.Contains("soak duration", reason);
    }

    [Fact]
    public void AcceptanceCriteria_RejectsSentinelStuckMeterOnALiveChannel()
    {
        var result = new LiveSoakResult(
            Duration: TimeSpan.FromSeconds(90),
            Channels: new[]
            {
                new ChannelSoakSample(0, true, 1000, 0.1, -73.0, true),
                new ChannelSoakSample(1, true, 1000, 0.1, RxMeterSentinelDbfs, true),
            });

        Assert.False(MeetsMultiRxAcceptanceCriteria(result, out var reason));
        Assert.Contains("sentinel", reason);
    }

    [Fact]
    public void AcceptanceCriteria_RejectsExcessiveInterChannelDrift()
    {
        var result = new LiveSoakResult(
            Duration: TimeSpan.FromSeconds(90),
            Channels: new[]
            {
                new ChannelSoakSample(0, true, 1000, 12.0, -73.0, true),
                new ChannelSoakSample(1, true, 1000, 0.1, -80.0, true),
            });

        Assert.False(MeetsMultiRxAcceptanceCriteria(result, out var reason));
        Assert.Contains("drift", reason);
    }

    [Fact]
    public void AcceptanceCriteria_AcceptsAHealthyTwoChannelSixtySecondSoak()
    {
        var result = new LiveSoakResult(
            Duration: TimeSpan.FromSeconds(65),
            Channels: new[]
            {
                new ChannelSoakSample(0, true, 65 * 1_536_000, 0.2, -73.0, true),
                new ChannelSoakSample(1, true, 65 * 1_536_000, 0.3, -81.5, true),
            });

        Assert.True(MeetsMultiRxAcceptanceCriteria(result, out var reason));
        Assert.Equal("ok", reason);
    }

    /// <summary>
    /// The live acceptance run itself. Skipped: it requires a physical
    /// Saturn/OrionMkII-class radio with >= 2 DDCs negotiated and a running
    /// n9dsp sidecar — neither exists in this unit-test project, and the
    /// live Zeus stack is intentionally stopped for this test-authoring pass.
    /// The team lead runs this against real hardware by replacing the
    /// NotImplementedException below with a harness that drives the actual
    /// P3 connect path (RadioService.MarkProtocol3Connected with
    /// maxReceivers >= 2, a live Protocol3SidecarBridge.ConnectAsync, and a
    /// >60s sample/meter/display capture loop) and feeds its
    /// <see cref="LiveSoakResult"/> through
    /// <see cref="MeetsMultiRxAcceptanceCriteria"/> above.
    /// </summary>
    [Fact(Skip = "Live-hardware acceptance run — requires a physical multi-DDC radio + running n9dsp sidecar. Not runnable in Zeus.Server.Tests; see class remarks.")]
    public void LiveSoak_TwoOrMoreDdcStreams_StaySaneForSixtySeconds()
    {
        throw new NotImplementedException(
            "Replace with a live harness: connect >= 2 DDCs via RadioService.MarkProtocol3Connected, " +
            "run Protocol3SidecarBridge.ConnectAsync against a real sidecar, capture >60s of samples/meters/" +
            "display frames per channel into a LiveSoakResult, and assert MeetsMultiRxAcceptanceCriteria(result, out _).");
    }
}
