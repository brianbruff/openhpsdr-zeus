using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Zeus.Contracts;
using Zeus.Plugins.Contracts.Extensions;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class Protocol3SidecarFrameForwarderTests
{
    [Fact]
    public void ResolveReverseDisplayBins_DefaultsToZeusLowToHighAxis()
    {
        var old = Environment.GetEnvironmentVariable("ZEUS_PROTOCOL3_REVERSE_DISPLAY_BINS");
        try
        {
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_REVERSE_DISPLAY_BINS", null);
            var configuration = new ConfigurationBuilder().Build();

            Assert.True(Protocol3SidecarFrameForwarder.ResolveReverseDisplayBins(configuration));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_REVERSE_DISPLAY_BINS", old);
        }
    }

    [Fact]
    public void ResolveReverseDisplayBins_CanBeDisabledForDiagnostics()
    {
        var old = Environment.GetEnvironmentVariable("ZEUS_PROTOCOL3_REVERSE_DISPLAY_BINS");
        try
        {
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_REVERSE_DISPLAY_BINS", null);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Zeus:Protocol3:Sidecar:ReverseDisplayBins"] = "false",
                })
                .Build();

            Assert.False(Protocol3SidecarFrameForwarder.ResolveReverseDisplayBins(configuration));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_REVERSE_DISPLAY_BINS", old);
        }
    }

    // Edge-guard frequency-absolute masking (zoom bug fix). The guard must mask
    // only the true DDC passband edges (centre ± sampleRate/2), never a fixed
    // slice of a zoomed view.
    private const float EdgeGuardFillDb = -100.0f;
    private const float DisplayFloorDb = -140.0f;

    private static (float[] pan, float[] wf) MakeGuardBuffers(int width)
    {
        var pan = new float[width];
        var wf = new float[width];
        Array.Fill(pan, EdgeGuardFillDb);
        Array.Fill(wf, EdgeGuardFillDb);
        return (pan, wf);
    }

    [Fact]
    public void ApplyDisplayEdgeGuard_FullSpan_MasksTrueBandEdges()
    {
        const int width = 4096;
        const long sampleRateHz = 192_000;
        var hzPerPixel = (float)sampleRateHz / width; // full DDC span in view
        var (pan, wf) = MakeGuardBuffers(width);

        Protocol3SidecarFrameForwarder.ApplyDisplayEdgeGuard(pan, wf, hzPerPixel, 6000.0, sampleRateHz);

        Assert.Equal(DisplayFloorDb, pan[0]);          // low edge masked
        Assert.Equal(DisplayFloorDb, pan[width - 1]);  // high edge masked
        Assert.Equal(EdgeGuardFillDb, pan[width / 2]); // centre untouched
    }

    [Fact]
    public void ApplyDisplayEdgeGuard_ZoomedIntoMidBand_MasksNothing()
    {
        const int width = 4096;
        const long sampleRateHz = 192_000;
        // 4x zoom: the visible window is ~48 kHz, deep inside the ±96 kHz
        // passband, so NO bin is within the 6 kHz guard of a true band edge.
        var hzPerPixel = (float)sampleRateHz / width / 4.0f;
        var (pan, wf) = MakeGuardBuffers(width);

        Protocol3SidecarFrameForwarder.ApplyDisplayEdgeGuard(pan, wf, hzPerPixel, 6000.0, sampleRateHz);

        Assert.Equal(EdgeGuardFillDb, pan[0]);          // edges NOT eaten
        Assert.Equal(EdgeGuardFillDb, pan[width - 1]);
        Assert.All(pan, v => Assert.Equal(EdgeGuardFillDb, v));
        Assert.All(wf, v => Assert.Equal(EdgeGuardFillDb, v));
    }

    [Fact]
    public void ApplyDisplayEdgeGuard_UnknownSampleRate_FallsBackToLegacyGuard()
    {
        const int width = 4096;
        var (pan, wf) = MakeGuardBuffers(width);

        // sampleRateHz = 0 → legacy fixed-fraction guard still masks the edges.
        Protocol3SidecarFrameForwarder.ApplyDisplayEdgeGuard(pan, wf, 46.875f, 6000.0, 0);

        Assert.Equal(DisplayFloorDb, pan[0]);
        Assert.Equal(DisplayFloorDb, pan[width - 1]);
        Assert.Equal(EdgeGuardFillDb, pan[width / 2]);
    }

    [Fact]
    public void ResolvePollCadence_DefaultsToProductionDisplayAndRealtimeAudio()
    {
        var oldDisplay = Environment.GetEnvironmentVariable("ZEUS_PROTOCOL3_FRAME_DISPLAY_POLL_MS");
        var oldDisplayMaxWidth = Environment.GetEnvironmentVariable("ZEUS_PROTOCOL3_FRAME_DISPLAY_MAX_WIDTH");
        var oldDisplayEdgeGuard = Environment.GetEnvironmentVariable("ZEUS_PROTOCOL3_FRAME_DISPLAY_EDGE_GUARD_HZ");
        var oldAudio = Environment.GetEnvironmentVariable("ZEUS_PROTOCOL3_FRAME_AUDIO_POLL_MS");
        try
        {
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_FRAME_DISPLAY_POLL_MS", null);
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_FRAME_DISPLAY_MAX_WIDTH", null);
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_FRAME_DISPLAY_EDGE_GUARD_HZ", null);
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_FRAME_AUDIO_POLL_MS", null);
            var configuration = new ConfigurationBuilder().Build();

            Assert.Equal(20, Protocol3SidecarFrameForwarder.ResolveDisplayPollMs(configuration));
            Assert.Equal(4096, Protocol3SidecarFrameForwarder.ResolveDisplayMaxWidth(configuration));
            Assert.Equal(10000.0, Protocol3SidecarFrameForwarder.ResolveDisplayEdgeGuardHz(configuration));
            Assert.Equal(10, Protocol3SidecarFrameForwarder.ResolveAudioPollMs(configuration));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_FRAME_DISPLAY_POLL_MS", oldDisplay);
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_FRAME_DISPLAY_MAX_WIDTH", oldDisplayMaxWidth);
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_FRAME_DISPLAY_EDGE_GUARD_HZ", oldDisplayEdgeGuard);
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_FRAME_AUDIO_POLL_MS", oldAudio);
        }
    }

    [Fact]
    public void ResolvePollCadence_AllowsExplicitHighRateEnvironmentOverrides()
    {
        var oldDisplay = Environment.GetEnvironmentVariable("ZEUS_PROTOCOL3_FRAME_DISPLAY_POLL_MS");
        var oldDisplayMaxWidth = Environment.GetEnvironmentVariable("ZEUS_PROTOCOL3_FRAME_DISPLAY_MAX_WIDTH");
        var oldDisplayEdgeGuard = Environment.GetEnvironmentVariable("ZEUS_PROTOCOL3_FRAME_DISPLAY_EDGE_GUARD_HZ");
        var oldAudio = Environment.GetEnvironmentVariable("ZEUS_PROTOCOL3_FRAME_AUDIO_POLL_MS");
        try
        {
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_FRAME_DISPLAY_POLL_MS", "8");
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_FRAME_DISPLAY_MAX_WIDTH", null);
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_FRAME_DISPLAY_EDGE_GUARD_HZ", null);
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_FRAME_AUDIO_POLL_MS", null);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Zeus:Protocol3:Sidecar:DisplayMaxWidth"] = "1024",
                    ["Zeus:Protocol3:Sidecar:DisplayEdgeGuardHz"] = "2400",
                    ["Zeus:Protocol3:Sidecar:AudioPollMs"] = "2",
                })
                .Build();

            Assert.Equal(8, Protocol3SidecarFrameForwarder.ResolveDisplayPollMs(configuration));
            Assert.Equal(1024, Protocol3SidecarFrameForwarder.ResolveDisplayMaxWidth(configuration));
            Assert.Equal(2400.0, Protocol3SidecarFrameForwarder.ResolveDisplayEdgeGuardHz(configuration));
            Assert.Equal(2, Protocol3SidecarFrameForwarder.ResolveAudioPollMs(configuration));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_FRAME_DISPLAY_POLL_MS", oldDisplay);
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_FRAME_DISPLAY_MAX_WIDTH", oldDisplayMaxWidth);
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_FRAME_DISPLAY_EDGE_GUARD_HZ", oldDisplayEdgeGuard);
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_FRAME_AUDIO_POLL_MS", oldAudio);
        }
    }

    [Fact]
    public void ResolvePollCadence_ClampsLowConfigDisplayCadenceToProductionDefault()
    {
        var oldDisplay = Environment.GetEnvironmentVariable("ZEUS_PROTOCOL3_FRAME_DISPLAY_POLL_MS");
        try
        {
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_FRAME_DISPLAY_POLL_MS", null);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Zeus:Protocol3:Sidecar:DisplayPollMs"] = "16",
                })
                .Build();

            Assert.Equal(20, Protocol3SidecarFrameForwarder.ResolveDisplayPollMs(configuration));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_FRAME_DISPLAY_POLL_MS", oldDisplay);
        }
    }

    [Fact]
    public void TryBuildDisplayFrame_KeepsSidecarLowToHighOrderByDefault()
    {
        var source = new JsonObject
        {
            ["ready"] = true,
            ["rxId"] = 0,
            ["width"] = 4,
            ["centerHz"] = 7_205_000,
            ["hzPerPixel"] = 25.0,
            ["lowToHigh"] = true,
            ["panDb"] = Values(-100.0, -90.0, -80.0, -70.0),
            ["wfDb"] = Values(-110.0, -100.0, -90.0, -80.0),
        };

        var ok = Protocol3SidecarFrameForwarder.TryBuildDisplayFrame(
            source,
            seq: 123,
            tsUnixMs: 456.0,
            reverseDisplayBins: false,
            out var frame);

        Assert.True(ok);
        Assert.Equal(123u, frame.Seq);
        Assert.Equal(0, frame.RxId);
        Assert.Equal(4, frame.Width);
        Assert.Equal(7_205_000, frame.CenterHz);
        Assert.Equal(25.0f, frame.HzPerPixel);
        Assert.Equal(DisplayBodyFlags.PanValid | DisplayBodyFlags.WfValid, frame.BodyFlags);
        Assert.Equal(new[] { -100.0f, -90.0f, -80.0f, -70.0f }, frame.PanDb.ToArray());
        Assert.Equal(new[] { -110.0f, -100.0f, -90.0f, -80.0f }, frame.WfDb.ToArray());
    }

    [Fact]
    public void TryBuildDisplayFrame_CanForceReverseForDiagnosticOverride()
    {
        var source = new JsonObject
        {
            ["ready"] = true,
            ["rxId"] = 0,
            ["width"] = 4,
            ["centerHz"] = 7_205_000,
            ["hzPerPixel"] = 25.0,
            ["lowToHigh"] = true,
            ["panDb"] = Values(-100.0, -90.0, -80.0, -70.0),
            ["wfDb"] = Values(-110.0, -100.0, -90.0, -80.0),
        };

        var ok = Protocol3SidecarFrameForwarder.TryBuildDisplayFrame(
            source,
            seq: 123,
            tsUnixMs: 456.0,
            reverseDisplayBins: true,
            out var frame);

        Assert.True(ok);
        Assert.Equal(new[] { -70.0f, -80.0f, -90.0f, -100.0f }, frame.PanDb.ToArray());
        Assert.Equal(new[] { -80.0f, -90.0f, -100.0f, -110.0f }, frame.WfDb.ToArray());
    }

    [Fact]
    public void TryBuildDisplayFrame_HonorsHostReverseHintWhenProjectionNeedsHostFlip()
    {
        var source = new JsonObject
        {
            ["ready"] = true,
            ["rxId"] = 0,
            ["width"] = 4,
            ["centerHz"] = 7_205_000,
            ["hzPerPixel"] = 25.0,
            ["lowToHigh"] = true,
            ["hostShouldReverseDisplayBins"] = true,
            ["panDb"] = Values(-100.0, -90.0, -80.0, -70.0),
            ["wfDb"] = Values(-110.0, -100.0, -90.0, -80.0),
        };

        var ok = Protocol3SidecarFrameForwarder.TryBuildDisplayFrame(
            source,
            seq: 123,
            tsUnixMs: 456.0,
            reverseDisplayBins: false,
            out var frame);

        Assert.True(ok);
        Assert.Equal(new[] { -70.0f, -80.0f, -90.0f, -100.0f }, frame.PanDb.ToArray());
        Assert.Equal(new[] { -80.0f, -90.0f, -100.0f, -110.0f }, frame.WfDb.ToArray());
    }

    [Fact]
    public void TryBuildDisplayFrame_AlsoReversesWhenSidecarMarksHighToLow()
    {
        var source = new JsonObject
        {
            ["ready"] = true,
            ["rxId"] = 1,
            ["width"] = 3,
            ["centerHz"] = 14_200_000,
            ["hzPerPixel"] = 10.0,
            ["lowToHigh"] = false,
            ["panDb"] = Values(-3.0, -2.0, -1.0),
            ["wfDb"] = Values(-6.0, -5.0, -4.0),
        };

        var ok = Protocol3SidecarFrameForwarder.TryBuildDisplayFrame(
            source,
            seq: 1,
            tsUnixMs: 2.0,
            reverseDisplayBins: false,
            out var frame);

        Assert.True(ok);
        Assert.Equal(1, frame.RxId);
        Assert.Equal(new[] { -1.0f, -2.0f, -3.0f }, frame.PanDb.ToArray());
        Assert.Equal(new[] { -4.0f, -5.0f, -6.0f }, frame.WfDb.ToArray());
    }

    [Fact]
    public void TryBuildDisplayFrame_DownsamplesWideSidecarFramesAndPreservesSpan()
    {
        var source = new JsonObject
        {
            ["ready"] = true,
            ["rxId"] = 0,
            ["width"] = 4,
            ["centerHz"] = 7_205_000,
            ["hzPerPixel"] = 25.0,
            ["lowToHigh"] = true,
            ["panDb"] = Values(-100.0, -90.0, -80.0, -70.0),
            ["wfDb"] = Values(-110.0, -100.0, -90.0, -80.0),
        };

        var ok = Protocol3SidecarFrameForwarder.TryBuildDisplayFrame(
            source,
            seq: 123,
            tsUnixMs: 456.0,
            reverseDisplayBins: false,
            maxDisplayWidth: 2,
            out var frame);

        Assert.True(ok);
        Assert.Equal(2, frame.Width);
        Assert.Equal(50.0f, frame.HzPerPixel);
        Assert.Equal(new[] { -95.0f, -75.0f }, frame.PanDb.ToArray());
        Assert.Equal(new[] { -105.0f, -85.0f }, frame.WfDb.ToArray());
    }

    [Fact]
    public void ApplyDisplayEdgeGuard_FloorsAndTapersUnreliableDdcEdges()
    {
        var pan = new[]
        {
            -50.0f, -60.0f, -70.0f, -80.0f, -90.0f,
            -92.0f, -94.0f, -96.0f, -98.0f, -100.0f,
            -100.0f, -98.0f, -96.0f, -94.0f, -92.0f,
            -90.0f, -80.0f, -70.0f, -60.0f, -50.0f
        };
        var wf = new[]
        {
            -55.0f, -65.0f, -75.0f, -85.0f, -95.0f,
            -97.0f, -99.0f, -101.0f, -103.0f, -105.0f,
            -105.0f, -103.0f, -101.0f, -99.0f, -97.0f,
            -95.0f, -85.0f, -75.0f, -65.0f, -55.0f
        };

        Protocol3SidecarFrameForwarder.ApplyDisplayEdgeGuard(
            pan,
            wf,
            hzPerPixel: 1000.0f,
            edgeGuardHz: 2000.0,
            sampleRateHz: 20_000);

        Assert.Equal(-140.0f, pan[0]);
        Assert.Equal(-140.0f, pan[1]);
        Assert.Equal(-140.0f, pan[^1]);
        Assert.Equal(-140.0f, pan[^2]);
        Assert.InRange(pan[2], -140.0f, -70.0f);
        Assert.InRange(pan[^3], -140.0f, -60.0f);
        Assert.Equal(-90.0f, pan[4]);
        Assert.Equal(-95.0f, wf[4]);
    }

    [Fact]
    public void ShouldRestartForIncompleteRx_RequiresSustainedIncompleteRxAndStaleAudio()
    {
        var diagnostics = HostedDiagnostics(
            rxProcessed: false,
            consecutiveIncompleteRxPolls: 149,
            rxReason: "RX poll produced 0 of 1 negotiated DDC channels.");

        Assert.False(Protocol3SidecarFrameForwarder.ShouldRestartForIncompleteRx(
            diagnostics,
            audioAgeMs: 10_000,
            nowUnixMs: 20_000,
            lastRestartUnixMs: 0,
            lastTxActivityUnixMs: 0,
            out _));

        diagnostics = HostedDiagnostics(
            rxProcessed: false,
            consecutiveIncompleteRxPolls: 150,
            rxReason: "RX poll produced 0 of 1 negotiated DDC channels.");

        Assert.False(Protocol3SidecarFrameForwarder.ShouldRestartForIncompleteRx(
            diagnostics,
            audioAgeMs: 500,
            nowUnixMs: 20_000,
            lastRestartUnixMs: 0,
            lastTxActivityUnixMs: 0,
            out _));

        Assert.True(Protocol3SidecarFrameForwarder.ShouldRestartForIncompleteRx(
            diagnostics,
            audioAgeMs: 2_500,
            nowUnixMs: 20_000,
            lastRestartUnixMs: 0,
            lastTxActivityUnixMs: 0,
            out var reason));
        Assert.Contains("150 incomplete RX polls", reason);
    }

    [Fact]
    public void ShouldRestartForIncompleteRx_ObservesRestartCooldown()
    {
        var diagnostics = HostedDiagnostics(
            rxProcessed: false,
            consecutiveIncompleteRxPolls: 500,
            rxReason: "RX poll produced 0 of 1 negotiated DDC channels.");

        Assert.False(Protocol3SidecarFrameForwarder.ShouldRestartForIncompleteRx(
            diagnostics,
            audioAgeMs: 10_000,
            nowUnixMs: 20_000,
            lastRestartUnixMs: 16_000,
            lastTxActivityUnixMs: 0,
            out _));

        Assert.True(Protocol3SidecarFrameForwarder.ShouldRestartForIncompleteRx(
            diagnostics,
            audioAgeMs: null,
            nowUnixMs: 20_000,
            lastRestartUnixMs: 14_000,
            lastTxActivityUnixMs: 0,
            out _));
    }

    [Fact]
    public void ShouldRestartForIncompleteRx_SuppressesRestartAfterTxActivity()
    {
        var diagnostics = HostedDiagnostics(
            rxProcessed: false,
            consecutiveIncompleteRxPolls: 500,
            rxReason: "RX poll produced 0 of 1 negotiated DDC channels.");

        Assert.False(Protocol3SidecarFrameForwarder.ShouldRestartForIncompleteRx(
            diagnostics,
            audioAgeMs: 10_000,
            nowUnixMs: 20_000,
            lastRestartUnixMs: 0,
            lastTxActivityUnixMs: 15_001,
            out _));

        Assert.True(Protocol3SidecarFrameForwarder.ShouldRestartForIncompleteRx(
            diagnostics,
            audioAgeMs: 10_000,
            nowUnixMs: 20_000,
            lastRestartUnixMs: 0,
            lastTxActivityUnixMs: 9_999,
            out _));
    }

    [Fact]
    public void TxActivityUnixMs_IgnoresStickyActiveFlagWithoutSenderTimestamp()
    {
        var diagnostics = new JsonObject
        {
            ["txIqIngestLoop"] = new JsonObject
            {
                ["active"] = true,
                ["lastSenderUtc"] = string.Empty,
            },
        };

        Assert.Equal(0, Protocol3SidecarFrameForwarder.TxActivityUnixMs(
            diagnostics,
            nowUnixMs: 20_000));
    }

    [Fact]
    public void TxActivityUnixMs_UsesSenderTimestampWhenPresent()
    {
        var diagnostics = new JsonObject
        {
            ["txIqIngestLoop"] = new JsonObject
            {
                ["active"] = true,
                ["lastSenderUtc"] = "2026-07-02T14:40:00.0000000Z",
            },
        };

        var expected = DateTimeOffset.Parse("2026-07-02T14:40:00.0000000Z")
            .ToUnixTimeMilliseconds();
        Assert.Equal(expected, Protocol3SidecarFrameForwarder.TxActivityUnixMs(
            diagnostics,
            nowUnixMs: expected + 1000));
    }

    [Fact]
    public void ParseAudioFramesPayload_MapsSidecarBlocksToAudioFrames()
    {
        var payload = BuildAudioPayload(
            rxId: 0,
            sequence: 42,
            timestampMs: 1_785_000_123,
            sampleRateHz: 48_000,
            samples: new[] { 0.125f, -0.25f, 0.5f });

        var frames = Protocol3SidecarFrameForwarder.ParseAudioFramesPayload(payload);

        var frame = Assert.Single(frames);
        Assert.Equal(42u, frame.Seq);
        Assert.Equal(1_785_000_123.0, frame.TsUnixMs);
        Assert.Equal(0, frame.RxId);
        Assert.Equal(1, frame.Channels);
        Assert.Equal(48_000u, frame.SampleRateHz);
        Assert.Equal(3, frame.SampleCount);
        Assert.Equal(new[] { 0.125f, -0.25f, 0.5f }, frame.Samples.ToArray());
    }

    [Fact]
    public void BuildProtocol3MutedAudioFrame_ZerosSamplesWithoutMutatingInput()
    {
        var inputSamples = new[] { 0.125f, -0.25f, 0.5f };
        var frame = new AudioFrame(
            Seq: 11,
            TsUnixMs: 2222,
            RxId: 0,
            Channels: 1,
            SampleRateHz: 48_000,
            SampleCount: checked((ushort)inputSamples.Length),
            Samples: inputSamples);

        var muted = Protocol3SidecarFrameForwarder.BuildProtocol3MutedAudioFrame(frame);

        Assert.Equal(frame.Seq, muted.Seq);
        Assert.Equal(frame.TsUnixMs, muted.TsUnixMs);
        Assert.Equal(frame.RxId, muted.RxId);
        Assert.Equal(frame.Channels, muted.Channels);
        Assert.Equal(frame.SampleRateHz, muted.SampleRateHz);
        Assert.Equal(frame.SampleCount, muted.SampleCount);
        Assert.Equal(new[] { 0f, 0f, 0f }, muted.Samples.ToArray());
        Assert.Equal(new[] { 0.125f, -0.25f, 0.5f }, frame.Samples.ToArray());
    }

    [Fact]
    public void ProcessProtocol3AudioModem_DecodesFreeDvPrimaryRxBeforePublish()
    {
        var modem = new RecordingAudioModem(active: true);
        double afGain = 1.0;
        var inputSamples = new[] { 0.125f, -0.25f, 0.5f };
        var frame = new AudioFrame(
            Seq: 7,
            TsUnixMs: 1234,
            RxId: 0,
            Channels: 1,
            SampleRateHz: 48_000,
            SampleCount: checked((ushort)inputSamples.Length),
            Samples: inputSamples);

        var processed = Protocol3SidecarFrameForwarder.ProcessProtocol3AudioModem(
            frame,
            modem,
            RxMode.FreeDv,
            rxAfGainDb: 0.0,
            ref afGain);

        Assert.Equal(new[] { (byte)RxMode.FreeDv }, modem.SyncModes);
        Assert.Equal(1, modem.ProcessRxCount);
        var samples = processed.Samples.ToArray();
        Assert.InRange(samples[0], 0.839f, 0.841f);
        Assert.InRange(samples[1], 0.749f, 0.751f);
        Assert.InRange(samples[2], 0.839f, 0.841f);
        Assert.Equal(new[] { 0.125f, -0.25f, 0.5f }, frame.Samples.ToArray());
    }

    [Fact]
    public void ProcessProtocol3AudioModem_SyncsButDoesNotProcessInactiveModem()
    {
        var modem = new RecordingAudioModem(active: false);
        double afGain = 1.0;
        var frame = new AudioFrame(
            Seq: 7,
            TsUnixMs: 1234,
            RxId: 0,
            Channels: 1,
            SampleRateHz: 48_000,
            SampleCount: 2,
            Samples: new[] { 0.25f, -0.5f });

        var processed = Protocol3SidecarFrameForwarder.ProcessProtocol3AudioModem(
            frame,
            modem,
            RxMode.USB,
            rxAfGainDb: 0.0,
            ref afGain);

        Assert.Equal(new[] { (byte)RxMode.USB }, modem.SyncModes);
        Assert.Equal(0, modem.ProcessRxCount);
        Assert.Equal(new[] { 0.25f, -0.5f }, processed.Samples.ToArray());
    }

    [Fact]
    public void ProcessProtocol3AudioModem_LeavesSecondaryRxFramesUntouched()
    {
        var modem = new RecordingAudioModem(active: true);
        double afGain = 1.0;
        var frame = new AudioFrame(
            Seq: 7,
            TsUnixMs: 1234,
            RxId: 1,
            Channels: 1,
            SampleRateHz: 48_000,
            SampleCount: 2,
            Samples: new[] { 0.25f, -0.5f });

        var processed = Protocol3SidecarFrameForwarder.ProcessProtocol3AudioModem(
            frame,
            modem,
            RxMode.FreeDv,
            rxAfGainDb: 0.0,
            ref afGain);

        Assert.Equal(new[] { (byte)RxMode.FreeDv }, modem.SyncModes);
        Assert.Equal(0, modem.ProcessRxCount);
        Assert.Equal(new[] { 0.25f, -0.5f }, processed.Samples.ToArray());
    }

    [Fact]
    public void ProcessProtocol3AudioModem_SanitizesAndLimitsDecodedSpeech()
    {
        var modem = new RecordingAudioModem(active: true);
        double afGain = 1.0;
        var frame = new AudioFrame(
            Seq: 7,
            TsUnixMs: 1234,
            RxId: 0,
            Channels: 1,
            SampleRateHz: 48_000,
            SampleCount: 3,
            Samples: new[] { 0.95f, float.NaN, -1.1f });

        var processed = Protocol3SidecarFrameForwarder.ProcessProtocol3AudioModem(
            frame,
            modem,
            RxMode.FreeDv,
            rxAfGainDb: 0.0,
            ref afGain);

        Assert.Equal(1, modem.ProcessRxCount);
        var samples = processed.Samples.ToArray();
        Assert.InRange(samples[0], 0.83f, 0.841f);
        Assert.InRange(samples[1], 0.83f, 0.841f);
        Assert.Equal(0f, samples[2]);
    }

    private static JsonArray Values(params double[] values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }
        return array;
    }

    private static JsonObject HostedDiagnostics(
        bool rxProcessed,
        long consecutiveIncompleteRxPolls,
        string rxReason) => new()
        {
            ["hostedRadioSession"] = new JsonObject
            {
                ["rxProcessed"] = rxProcessed,
                ["consecutiveIncompleteRxPolls"] = consecutiveIncompleteRxPolls,
                ["rxReason"] = rxReason,
            },
        };

    private static byte[] BuildAudioPayload(
        byte rxId,
        uint sequence,
        ulong timestampMs,
        uint sampleRateHz,
        float[] samples)
    {
        using var body = new MemoryStream();
        Span<byte> header = stackalloc byte[16];
        header[0] = (byte)'P';
        header[1] = (byte)'3';
        header[2] = (byte)'A';
        header[3] = (byte)'U';
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..6], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..8], 16);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], 1);
        body.Write(header);

        Span<byte> frameHeader = stackalloc byte[32];
        frameHeader[0] = rxId;
        frameHeader[1] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(frameHeader[4..8], sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(frameHeader[8..16], timestampMs);
        BinaryPrimitives.WriteUInt32LittleEndian(frameHeader[16..20], sampleRateHz);
        BinaryPrimitives.WriteUInt32LittleEndian(frameHeader[20..24], checked((uint)samples.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(frameHeader[24..28], checked((uint)(samples.Length * sizeof(float))));
        body.Write(frameHeader);
        body.Write(MemoryMarshal.AsBytes(samples.AsSpan()));
        return body.ToArray();
    }

    private sealed class RecordingAudioModem : IAudioModemPlugin
    {
        private readonly bool _active;
        private readonly List<byte> _syncModes = new();

        public RecordingAudioModem(bool active)
        {
            _active = active;
        }

        public IReadOnlyList<byte> SyncModes => _syncModes;
        public int ProcessRxCount { get; private set; }
        public bool Active => _active;
        public bool NativeAvailable => true;

        public void SyncMode(byte rxModeByte) => _syncModes.Add(rxModeByte);

        public void ProcessRx(Span<float> block48k)
        {
            ProcessRxCount++;
            for (int i = 0; i < block48k.Length; i++)
                block48k[i] += 1.0f;
        }

        public void ProcessTx(Span<float> block48k) { }
        public void FlushRx() { }
        public void FlushTx() { }
        public int FinishTx() => 0;
        public int DrainTx(Span<float> block48k) => 0;
    }
}
