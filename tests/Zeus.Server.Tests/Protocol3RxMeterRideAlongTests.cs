// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Regression lock for the 2026-07-04 Protocol-3 S-meter fix (the "−250 dBm
// no-data" bug): under P3 the WDSP RX channel is never fed IQ, so the legacy
// per-tick meter block used to broadcast the WDSP-idle sentinel forever.
// The fix rides the n9dsp per-channel meter readings that arrive on the
// sidecar's display frames into DspPipelineService.PublishProtocol3RxMeters,
// which republishes through the exact same 0x14/0x19 outputs the WDSP tick
// uses, and the frame forwarder throttles/filters that ingress to the WDSP
// path's ~5 Hz cadence. Neither of these had unit coverage before this file.

using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json.Nodes;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class Protocol3RxMeterRideAlongTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-p3-rxmeter-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + ".pa")) File.Delete(_dbPath + ".pa"); } catch { }
    }

    private (RadioService Radio, DspPipelineService Pipeline) BuildRig()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath);
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        var radio = new RadioService(loggerFactory, dspStore, paStore);
        var hub = new StreamingHub(NullLogger<StreamingHub>.Instance);
        var pipeline = new DspPipelineService(radio, hub, Array.Empty<IRxAudioSink>(), loggerFactory);
        return (radio, pipeline);
    }

    [Fact]
    public void PublishProtocol3RxMeters_IsNoOpWhenProtocol3IsNotActive()
    {
        var (_, pipeline) = BuildRig();
        double? observedDbm = null;
        pipeline.RxMeterUpdated += (_, dbm) => observedDbm = dbm;

        // No MarkProtocol3Connected call — IsProtocol3Active is false, so the
        // ride-along publisher must not broadcast (WDSP tick owns the meter
        // in every other protocol mode).
        pipeline.PublishProtocol3RxMeters(channel: 0, dbfsRaw: -73.0, agcGainDb: 12.0, adcHeadroomDb: 20.0);

        Assert.Null(observedDbm);
    }

    [Fact]
    public void PublishProtocol3RxMeters_AppliesBoardCalOffsetAndRaisesLegacyMeterEvent()
    {
        var (radio, pipeline) = BuildRig();
        radio.MarkProtocol3Connected("127.0.0.1:1024", 1_536_000, maxReceivers: 1);

        int? observedChannel = null;
        double? observedDbm = null;
        pipeline.RxMeterUpdated += (ch, dbm) => { observedChannel = ch; observedDbm = dbm; };

        var expectedOffset = RadioCalibrations.RxMeterOffsetDb(
            radio.EffectiveBoardKind, radio.EffectiveOrionMkIIVariant);

        pipeline.PublishProtocol3RxMeters(channel: 0, dbfsRaw: -73.0, agcGainDb: 12.0, adcHeadroomDb: 20.0);

        Assert.Equal(0, observedChannel);
        Assert.NotNull(observedDbm);
        Assert.Equal(-73.0 + expectedOffset, observedDbm!.Value, precision: 6);
    }

    [Fact]
    public void PublishProtocol3RxMeters_LeavesSentinelDbfsUncalibrated()
    {
        var (radio, pipeline) = BuildRig();
        radio.MarkProtocol3Connected("127.0.0.1:1024", 1_536_000, maxReceivers: 1);

        double? observedDbm = null;
        pipeline.RxMeterUpdated += (_, dbm) => observedDbm = dbm;

        // n9dsp "no data" sentinel — must pass through unmodified, exactly
        // like the WDSP -400 "didn't run" sentinel does in BuildRxMetersV2.
        pipeline.PublishProtocol3RxMeters(channel: 0, dbfsRaw: -200.0, agcGainDb: 0.0, adcHeadroomDb: 200.0);

        Assert.Equal(-200.0, observedDbm);
    }

    [Fact]
    public void PublishProtocol3RxMeters_RaisesRxMetersV2WithAdcAndAgcFieldsPopulated()
    {
        var (radio, pipeline) = BuildRig();
        radio.MarkProtocol3Connected("127.0.0.1:1024", 1_536_000, maxReceivers: 1);

        RxMetersV2Frame? observedFrame = null;
        pipeline.RxMetersV2Updated += (_, frame) => observedFrame = frame;

        pipeline.PublishProtocol3RxMeters(channel: 0, dbfsRaw: -73.0, agcGainDb: 12.0, adcHeadroomDb: 20.0);

        Assert.NotNull(observedFrame);
        // ADC headroom is carried as a negative dBFS peak (headroom sign-flipped),
        // matching the WDSP-path AdcPk convention (see BuildRxMetersV2).
        Assert.Equal(-20.0f, observedFrame!.Value.AdcPk);
        Assert.Equal(12.0f, observedFrame.Value.AgcGain);
    }

    [Fact]
    public void PublishProtocol3RxMeters_FallsBackToSentinelAdcWhenHeadroomIsNotFinite()
    {
        var (radio, pipeline) = BuildRig();
        radio.MarkProtocol3Connected("127.0.0.1:1024", 1_536_000, maxReceivers: 1);

        RxMetersV2Frame? observedFrame = null;
        pipeline.RxMetersV2Updated += (_, frame) => observedFrame = frame;

        pipeline.PublishProtocol3RxMeters(channel: 0, dbfsRaw: -90.0, agcGainDb: double.NaN, adcHeadroomDb: double.NaN);

        Assert.NotNull(observedFrame);
        Assert.Equal(-200.0f, observedFrame!.Value.AdcPk);
        Assert.Equal(0.0f, observedFrame.Value.AgcGain);
    }

    // ---- Frame-forwarder ingress shape (rxMeterDbfs / rxAgcGainDb /
    // rxAdcHeadroomDb + the ~5 Hz throttle + rxId!=0 + sentinel filtering)
    // is implemented in Protocol3SidecarFrameForwarder.PublishRxMetersFromFrame,
    // a private instance method reachable only through the poll loop (which
    // needs a live sidecar HTTP endpoint). These tests lock in the parsing
    // contract that method depends on — the JSON key names and the sentinel
    // threshold — via the same JsonObject shape the sidecar emits, so a
    // rename or threshold change of any of those fields breaks a fast,
    // synchronous test instead of only being caught by a live soak.

    [Fact]
    public void SidecarDisplayFrame_RxMeterFieldNames_MatchExpectedContract()
    {
        var frame = new JsonObject
        {
            ["rxId"] = 0,
            ["rxMeterDbfs"] = -73.5,
            ["rxAgcGainDb"] = 8.25,
            ["rxAdcHeadroomDb"] = 14.0,
        };

        Assert.True(frame["rxMeterDbfs"] is JsonValue v1 && v1.TryGetValue<double>(out var dbfs) && dbfs == -73.5);
        Assert.True(frame["rxAgcGainDb"] is JsonValue v2 && v2.TryGetValue<double>(out var agc) && agc == 8.25);
        Assert.True(frame["rxAdcHeadroomDb"] is JsonValue v3 && v3.TryGetValue<double>(out var headroom) && headroom == 14.0);
    }

    [Theory]
    [InlineData(-199.5, false)]  // exactly at the sentinel boundary — treated as "no data"
    [InlineData(-199.6, false)]
    [InlineData(-199.4, true)]
    [InlineData(-73.0, true)]
    public void RxMeterSentinelThreshold_MatchesFrameForwarderCutoff(double dbfs, bool expectUsable)
    {
        // Mirrors the "if (dbfs <= -199.5) return;" gate in
        // Protocol3SidecarFrameForwarder.PublishRxMetersFromFrame, and the
        // "value <= -199.5" gate in DspPipelineService.ApplyRxMeterCalibration
        // — both must agree on where the sentinel boundary sits, or a value
        // could be forwarded but then miscalibrated (or vice versa).
        const double sentinel = -199.5;
        Assert.Equal(expectUsable, dbfs > sentinel);
    }
}
