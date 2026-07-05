// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Zeus.Contracts;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Server;

public sealed class Protocol3SidecarFrameForwarder : BackgroundService
{
    private const float DisplayFloorDb = -140.0f;
    private const int DefaultDisplayPollMs = 20;
    private const int DefaultDisplayMaxWidth = 4096;
    // Masks the DDC decimation-filter transition band at the true passband
    // edges (the coloured speckle at the extreme edges of a full-span view).
    // 10 kHz covers the G2's ~192 kHz DDC rolloff; the frequency-absolute guard
    // masks nothing once zoomed into mid-band. Tunable via
    // ZEUS_PROTOCOL3_FRAME_DISPLAY_EDGE_GUARD_HZ.
    private const double DefaultDisplayEdgeGuardHz = 10000.0;
    private const int DefaultAudioPollMs = 10;
    private const int MaxDisplayWidth = ushort.MaxValue;
    private const int MaxAudioFramesPerPoll = 64;
    private const int MaxRadioMicPacketsPerPoll = 128;
    private const int MaxAudioPayloadBytes = 2 * 1024 * 1024;
    private const int MaxRadioMicPayloadBytes = RadioMicReceiver.PacketBytes * MaxRadioMicPacketsPerPoll;
    private const bool DefaultReverseDisplayBins = true;
    private const int HealthPollMs = 1000;
    private const int IncompleteRxRestartPolls = 150;
    private const int StaleAudioRestartMs = 2500;
    private const int RestartCooldownMs = 5000;
    private const int TxActivityRestartGraceMs = 10000;

    private readonly RadioService _radio;
    private readonly Protocol3SidecarBridge _sidecar;
    private readonly StreamingHub _hub;
    private readonly IRxAudioSink[] _audioSinks;
    private readonly AudioModemPluginBridge? _audioModem;
    private readonly TxAudioIngest _txIngest;
    private readonly RadioMicReceiver _radioMicReceiver;
    private readonly IHttpClientFactory _httpFactory;
    private readonly HttpClient _http;
    private readonly ILogger<Protocol3SidecarFrameForwarder> _log;
    private readonly int _displayPollMs;
    private readonly int _displayMaxWidth;
    private readonly double _displayEdgeGuardHz;
    private readonly int _audioPollMs;
    private readonly bool _reverseDisplayBins;

    private long _displayFramesForwarded;
    private long _audioFramesForwarded;
    private long _radioMicPacketsForwarded;
    private long _displayPollFailures;
    private long _audioPollFailures;
    private long _radioMicPollFailures;
    private long _lastDisplayFrameUnixMs;
    private long _lastAudioFrameUnixMs;
    private long _lastRadioMicPacketUnixMs;
    private long _lastDisplayFailureLogMs;
    private long _lastAudioFailureLogMs;
    private long _lastRadioMicFailureLogMs;
    private long _lastHealthPollUnixMs;
    private long _lastRestartRequestUnixMs;
    private long _lastTxActivityUnixMs;
    private long _healthPollFailures;
    private long _restartRequests;
    private string? _lastRestartReason;
    private int _lastDisplayWidth;
    private int _lastDisplayRxId;
    private int _lastDisplayHzPerPixelBits;
    private int _lastAudioRxId;
    private int _lastAudioSampleRateHz;
    private int _lastAudioSampleCount;
    private int _running;
    private uint _displaySeq;
    private double _protocol3FreeDvAfGainLinear = 1.0;
    private readonly DspPipelineService? _pipeline;
    // n9dsp RX meter ride-along (S-meter fix): last publish time, to throttle
    // the 50 Hz display poll down to the ~5 Hz meter cadence the WDSP path uses.
    private long _lastRxMeterPublishMs;
    private const int RxMeterPublishIntervalMs = 200;
    // P3 TX FWD/REF power-meter ride-along (display-only, P2 parity): under
    // Protocol 3 the radio's forward/reverse power ADCs live in the sidecar
    // board-health telemetry, not the P1/P2 hi-priority stream TxMetersService
    // normally listens to — so without this the operator's TX power/SWR meter
    // sits at 0 during transmit. We poll the sidecar power ADCs while the TX-IQ
    // egress is armed (MOX or TUN) and feed them through the same OnTelemetryRaw
    // entry the P2 path uses. RX is untouched (the poll is gated off unless
    // transmitting, when RX audio is muted anyway).
    private readonly TxMetersService? _txMeters;
    private long _lastTxMeterPollMs;
    // ~4 Hz: fast enough for a live power/SWR needle, but the board-health block
    // rides the full ~40 KB diagnostics payload, so polling it faster than this
    // during TX steals sidecar-serialize + app-parse time from the panadapter
    // frame loop (visible as TX-time display lag). TxMetersService smooths and
    // rebroadcasts at 10 Hz regardless, so the needle still moves cleanly.
    private const int TxMeterPollMs = 250;

    public Protocol3SidecarFrameForwarder(
        RadioService radio,
        Protocol3SidecarBridge sidecar,
        StreamingHub hub,
        IEnumerable<IRxAudioSink> audioSinks,
        TxAudioIngest txIngest,
        IHttpClientFactory httpFactory,
        IConfiguration configuration,
        ILogger<Protocol3SidecarFrameForwarder> log,
        AudioModemPluginBridge? audioModem = null,
        DspPipelineService? pipeline = null,
        TxMetersService? txMeters = null)
    {
        _radio = radio;
        _sidecar = sidecar;
        _hub = hub;
        _pipeline = pipeline;
        _audioModem = audioModem;
        _txMeters = txMeters;
        _audioSinks = audioSinks.ToArray();
        _txIngest = txIngest;
        _radioMicReceiver = new RadioMicReceiver(
            block => txIngest.OnMicPcmBytesFromRadioMic(block),
            log);
        _httpFactory = httpFactory;
        _http = httpFactory.CreateClient(Protocol3SidecarBridge.HttpClientName);
        _log = log;
        _displayPollMs = ResolveDisplayPollMs(configuration);
        _displayMaxWidth = ResolveDisplayMaxWidth(configuration);
        _displayEdgeGuardHz = ResolveDisplayEdgeGuardHz(configuration);
        _audioPollMs = ResolveAudioPollMs(configuration);
        _reverseDisplayBins = ResolveReverseDisplayBins(configuration);
    }

    public object Status
    {
        get
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var displayMs = Interlocked.Read(ref _lastDisplayFrameUnixMs);
            var audioMs = Interlocked.Read(ref _lastAudioFrameUnixMs);
            var radioMicMs = Interlocked.Read(ref _lastRadioMicPacketUnixMs);
            return new
            {
                running = Volatile.Read(ref _running) != 0,
                display = new
                {
                    framesForwarded = Interlocked.Read(ref _displayFramesForwarded),
                    pollFailures = Interlocked.Read(ref _displayPollFailures),
                    lastFrameUnixMs = displayMs > 0 ? displayMs : (long?)null,
                    lastFrameAgeMs = displayMs > 0 ? Math.Max(0, nowMs - displayMs) : (long?)null,
                    lastRxId = Volatile.Read(ref _lastDisplayRxId),
                    lastWidth = Volatile.Read(ref _lastDisplayWidth),
                    maxWidth = _displayMaxWidth,
                    lastHzPerPixel = BitConverter.Int32BitsToSingle(Volatile.Read(ref _lastDisplayHzPerPixelBits)),
                    edgeGuardHz = _displayEdgeGuardHz,
                    reverseDisplayBins = _reverseDisplayBins,
                    pollMs = _displayPollMs,
                },
                audio = new
                {
                    framesForwarded = Interlocked.Read(ref _audioFramesForwarded),
                    pollFailures = Interlocked.Read(ref _audioPollFailures),
                    lastFrameUnixMs = audioMs > 0 ? audioMs : (long?)null,
                    lastFrameAgeMs = audioMs > 0 ? Math.Max(0, nowMs - audioMs) : (long?)null,
                    lastRxId = Volatile.Read(ref _lastAudioRxId),
                    lastSampleRateHz = Volatile.Read(ref _lastAudioSampleRateHz),
                    lastSampleCount = Volatile.Read(ref _lastAudioSampleCount),
                    sinkCount = _audioSinks.Length,
                    pollMs = _audioPollMs,
                },
                health = new
                {
                    pollFailures = Interlocked.Read(ref _healthPollFailures),
                    restartRequests = Interlocked.Read(ref _restartRequests),
                    lastHealthPollUnixMs = Interlocked.Read(ref _lastHealthPollUnixMs) > 0
                        ? Interlocked.Read(ref _lastHealthPollUnixMs)
                        : (long?)null,
                    lastRestartRequestUnixMs = Interlocked.Read(ref _lastRestartRequestUnixMs) > 0
                        ? Interlocked.Read(ref _lastRestartRequestUnixMs)
                        : (long?)null,
                    lastRestartReason = _lastRestartReason,
                    incompleteRxRestartPolls = IncompleteRxRestartPolls,
                    staleAudioRestartMs = StaleAudioRestartMs,
                    txActivityRestartGraceMs = TxActivityRestartGraceMs,
                    lastTxActivityUnixMs = Interlocked.Read(ref _lastTxActivityUnixMs) > 0
                        ? Interlocked.Read(ref _lastTxActivityUnixMs)
                        : (long?)null,
                },
                radioMic = new
                {
                    packetsForwarded = Interlocked.Read(ref _radioMicPacketsForwarded),
                    pollFailures = Interlocked.Read(ref _radioMicPollFailures),
                    lastPacketUnixMs = radioMicMs > 0 ? radioMicMs : (long?)null,
                    lastPacketAgeMs = radioMicMs > 0 ? Math.Max(0, nowMs - radioMicMs) : (long?)null,
                    receiverPacketsAccepted = _radioMicReceiver.TotalPacketsAccepted,
                    receiverPacketsDropped = _radioMicReceiver.TotalPacketsDropped,
                    receiverBlocksForwarded = _radioMicReceiver.TotalBlocksForwarded,
                    maxPacketsPerPoll = MaxRadioMicPacketsPerPoll,
                    pollMs = _audioPollMs,
                },
                // Measurement-only TX-turnaround latency (MOX→first-IQ-at-egress
                // and PTT-release→egress-drain) sourced from the WDSP TX path.
                txTurnaround = _pipeline?.TxTurnaroundStatus,
            };
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Volatile.Write(ref _running, 1);
        try
        {
            await Task.WhenAll(
                    RunDisplayLoopAsync(stoppingToken),
                    RunAudioLoopAsync(stoppingToken),
                    RunRadioMicLoopAsync(stoppingToken))
                .ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task RunDisplayLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_radio.IsProtocol3Active && _hub.DisplayStreamRequested && _sidecar.DiagnosticsUrl is { } diagnosticsUrl)
                {
                    await PollDisplayAsync(diagnosticsUrl, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _displayPollFailures);
                LogThrottled(ref _lastDisplayFailureLogMs, ex, "p3.sidecar.display.forward.failed");
            }

            await DelayQuietAsync(_displayPollMs, ct).ConfigureAwait(false);
        }
    }

    private async Task RunAudioLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_radio.IsProtocol3Active && _sidecar.DiagnosticsUrl is { } diagnosticsUrl)
                {
                    await PollAudioAsync(diagnosticsUrl, ct).ConfigureAwait(false);
                    await PollHealthAsync(diagnosticsUrl, ct).ConfigureAwait(false);
                    await PollTxMetersAsync(diagnosticsUrl, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _audioPollFailures);
                LogThrottled(ref _lastAudioFailureLogMs, ex, "p3.sidecar.audio.forward.failed");
            }

            await DelayQuietAsync(_audioPollMs, ct).ConfigureAwait(false);
        }
    }

    private async Task RunRadioMicLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_radio.IsProtocol3Active &&
                    IsRadioMicSourceActive() &&
                    _sidecar.DiagnosticsUrl is { } diagnosticsUrl)
                {
                    await PollRadioMicAsync(diagnosticsUrl, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _radioMicPollFailures);
                LogThrottled(ref _lastRadioMicFailureLogMs, ex, "p3.sidecar.radio-mic.forward.failed");
            }

            await DelayQuietAsync(_audioPollMs, ct).ConfigureAwait(false);
        }
    }

    private async Task PollDisplayAsync(Uri diagnosticsUrl, CancellationToken ct)
    {
        // The waterfall/panadapter keeps running during TX (P2 parity — you can
        // watch your own signal). The earlier "hold RX display during TX" tweak
        // was reverted: the smear it targeted was actually the wrong-frequency
        // TX bug (DUC 21 kHz off), fixed at its root, so holding the display
        // only froze the waterfall during transmit.
        var state = _radio.Snapshot();
        var zoom = Math.Clamp(state.ZoomLevel, 1, 64);
        var url = Endpoint(
            diagnosticsUrl,
            "/api/diagnostics/v2/p3-display-frames",
            $"zoom={zoom.ToString(CultureInfo.InvariantCulture)}&center=0&maxFrames=1&dropStale=true");
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var root = await JsonNode.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false) as JsonObject;
        if (root?["frames"] is not JsonArray frames) return;

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var node in frames)
        {
            if (node is not JsonObject frameObject) continue;
            if (!TryBuildDisplayFrame(
                    frameObject,
                    ++_displaySeq,
                    nowMs,
                    _reverseDisplayBins,
                    _displayMaxWidth,
                    out var frame))
            {
                continue;
            }

            if (_displayEdgeGuardHz > 0.0)
            {
                var panDb = frame.PanDb.ToArray();
                var wfDb = frame.WfDb.ToArray();
                ApplyDisplayEdgeGuard(panDb, wfDb, frame.HzPerPixel, _displayEdgeGuardHz, frame.SampleRateHz);
                frame = frame with { PanDb = panDb, WfDb = wfDb };
            }
            _hub.Broadcast(frame);
            Interlocked.Increment(ref _displayFramesForwarded);
            Interlocked.Exchange(ref _lastDisplayFrameUnixMs, nowMs);
            Volatile.Write(ref _lastDisplayRxId, frame.RxId);
            Volatile.Write(ref _lastDisplayWidth, frame.Width);
            Volatile.Write(ref _lastDisplayHzPerPixelBits, BitConverter.SingleToInt32Bits(frame.HzPerPixel));

            PublishRxMetersFromFrame(frameObject, frame.RxId, nowMs);
        }
    }

    // n9dsp RX meter ride-along (S-meter fix): the sidecar attaches per-channel
    // meter readings to each display frame; forward them into the pipeline's
    // shared meter publisher so the S-meter, Meters Panel, CAT and Auto-AGC all
    // read real n9dsp values under P3 instead of the unfed WDSP channel's
    // "no data" floor. Throttled to the WDSP path's ~5 Hz cadence.
    private void PublishRxMetersFromFrame(JsonObject frameObject, int rxId, long nowMs)
    {
        if (_pipeline is null) return;
        if (rxId != 0) return;   // primary RX drives the S-meter, as on P2
        if (nowMs - Interlocked.Read(ref _lastRxMeterPublishMs) < RxMeterPublishIntervalMs) return;
        if (frameObject["rxMeterDbfs"] is not JsonValue dbfsNode ||
            !dbfsNode.TryGetValue<double>(out var dbfs))
        {
            return;
        }
        if (dbfs <= -199.5) return;   // sidecar "no data" sentinel

        double agcGainDb = frameObject["rxAgcGainDb"] is JsonValue agcNode &&
            agcNode.TryGetValue<double>(out var agc) ? agc : 0.0;
        double adcHeadroomDb = frameObject["rxAdcHeadroomDb"] is JsonValue headroomNode &&
            headroomNode.TryGetValue<double>(out var headroom) ? headroom : 200.0;

        Interlocked.Exchange(ref _lastRxMeterPublishMs, nowMs);
        _pipeline.PublishProtocol3RxMeters(rxId, dbfs, agcGainDb, adcHeadroomDb);
    }

    private async Task PollAudioAsync(Uri diagnosticsUrl, CancellationToken ct)
    {
        var url = Endpoint(
            diagnosticsUrl,
            "/api/diagnostics/v2/p3-audio-frames",
            $"maxFrames={MaxAudioFramesPerPoll}&dropStale=false");
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length > MaxAudioPayloadBytes)
            throw new InvalidDataException($"Protocol 3 audio payload too large: {bytes.Length} bytes.");

        var frames = ParseAudioFramesPayload(bytes);
        var forwardedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var state = _radio.Snapshot();
        // Mute RX audio while transmitting (P2 parity). With TX now on-frequency,
        // the RX DDC demodulates the operator's own transmit and would play it
        // back ("hearing myself during TX") — the n9dsp RX audio must be gated on
        // the TX-active state exactly as WDSP RXA is muted on key-down under P2.
        //
        // ALSO mute while the TX monitor (preview) is on, even with MOX off. Under
        // P2 the DSP Tick publishes the monitor audio INSTEAD of RX (single source
        // into every sink — see WdspDspEngine.SetTxMonitorEnabled: "the operator
        // still wants to NOT hear the band when monitor is on"). Under P3 the RX
        // audio arrives here from the sidecar poll, DECOUPLED from the WDSP monitor
        // Tick, so without this gate BOTH stream into NativeAudioSink at ~48 kHz
        // each — ~96 kHz into a ring the device drains at 48 kHz. The ring pins at
        // the latency ceiling and the elastic-buffer Skip() trims it every cycle,
        // one discontinuity per trim ("preview super choppy"). Muting RX here
        // restores the P2 single-source invariant: the monitor tick is the only
        // producer while previewing, so the ring stays rate-matched and smooth.
        var txMonitorOn = _pipeline?.CurrentEngine?.IsTxMonitorOn ?? false;
        var muteForTx = _sidecar.TxIqStreamingArmed || txMonitorOn;
        foreach (var frame in frames)
        {
            if (!muteForTx)
            {
                var publishFrame = ProcessProtocol3AudioModem(
                    frame,
                    _audioModem?.Current,
                    state.Mode,
                    state.RxAfGainDb,
                    ref _protocol3FreeDvAfGainLinear);
                _pipeline?.PublishProtocol3RxAudioDiagnostics(in publishFrame);
                foreach (var sink in _audioSinks)
                {
                    sink.Publish(publishFrame);
                }
            }
            else if (!txMonitorOn)
            {
                // Keep playback clocked with silence during MOX/TUN so the
                // native/browser audio rings do not under-run and rebuffer on
                // the un-key edge. TX monitor preview remains the sole producer
                // when it is enabled, so it does not get a parallel silence feed.
                var publishFrame = BuildProtocol3MutedAudioFrame(frame);
                _pipeline?.PublishProtocol3RxAudioDiagnostics(in publishFrame);
                foreach (var sink in _audioSinks)
                {
                    sink.Publish(publishFrame);
                }
            }

            Interlocked.Increment(ref _audioFramesForwarded);
            Interlocked.Exchange(ref _lastAudioFrameUnixMs, forwardedUnixMs);
            Volatile.Write(ref _lastAudioRxId, frame.RxId);
            Volatile.Write(ref _lastAudioSampleRateHz, checked((int)frame.SampleRateHz));
            Volatile.Write(ref _lastAudioSampleCount, frame.SampleCount);
        }
    }

    internal static AudioFrame BuildProtocol3MutedAudioFrame(AudioFrame frame)
    {
        var count = frame.Samples.Length;
        return count == 0
            ? frame
            : frame with { Samples = new float[count] };
    }

    internal static AudioFrame ProcessProtocol3AudioModem(
        AudioFrame frame,
        IAudioModemPlugin? modem,
        RxMode mode,
        double rxAfGainDb,
        ref double freeDvAfGainLinear)
    {
        if (modem is null) return frame;

        modem.SyncMode((byte)mode);
        if (!modem.Active) return frame;

        // Match the P2/Wdsp path: FreeDV is an RX0 mono 48 kHz post-demod insert.
        // Other frame shapes are passed through rather than corrupting audio.
        if (frame.RxId != 0 ||
            frame.Channels != 1 ||
            frame.SampleRateHz != DspPipelineService.AudioOutputRateHz ||
            frame.SampleCount == 0)
        {
            return frame;
        }

        var samples = frame.Samples.ToArray();
        var block = samples.AsSpan(0, frame.SampleCount);
        DspPipelineService.SanitizeAudioBuffer(block);
        modem.ProcessRx(block);
        ApplyProtocol3FreeDvAfGain(block, rxAfGainDb, ref freeDvAfGainLinear);
        DspPipelineService.LimitRxAudioBuffer(block);
        return frame with { Samples = samples };
    }

    private static void ApplyProtocol3FreeDvAfGain(
        Span<float> block,
        double targetDb,
        ref double freeDvAfGainLinear)
    {
        double startGain = freeDvAfGainLinear;
        double startDb = 20.0 * Math.Log10(Math.Max(startGain, 1e-9));
        double endDb = StepTowardCappedDb(startDb, targetDb, maxStep: 2.0);
        double endGain = Math.Pow(10.0, endDb / 20.0);
        int n = block.Length;
        if (n == 0)
        {
            freeDvAfGainLinear = endGain;
            return;
        }

        for (int i = 0; i < n; i++)
        {
            double t = (i + 1) / (double)n;
            double gain = startGain + (endGain - startGain) * t;
            block[i] = (float)(block[i] * gain);
        }
        freeDvAfGainLinear = endGain;
    }

    private static double StepTowardCappedDb(double current, double target, double maxStep)
    {
        double delta = target - current;
        return Math.Abs(delta) <= maxStep ? target : current + Math.Sign(delta) * maxStep;
    }

    private async Task PollRadioMicAsync(Uri diagnosticsUrl, CancellationToken ct)
    {
        var url = Endpoint(
            diagnosticsUrl,
            "/api/diagnostics/v2/p3-radio-mic-packets",
            $"maxPackets={MaxRadioMicPacketsPerPoll}&dropStale=true");
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length > MaxRadioMicPayloadBytes)
            throw new InvalidDataException($"Protocol 3 radio-mic payload too large: {bytes.Length} bytes.");

        var packets = ForwardRadioMicPacketPayload(bytes);
        if (packets > 0)
        {
            Interlocked.Add(ref _radioMicPacketsForwarded, packets);
            Interlocked.Exchange(ref _lastRadioMicPacketUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }

    // Feed the sidecar's forward/reverse power ADCs into TxMetersService so the
    // operator's TX power/SWR meter reads during Protocol 3 transmit (P2 parity).
    // Only runs while the TX-IQ egress is armed — TxIqStreamingArmed covers both
    // keyed MOX and TUN, both of which drive the egress — and self-throttles to
    // ~7 Hz. The power raws live in the full /api/diagnostics/v2 payload (the same
    // one PollHealthAsync reads at 1 Hz), but the meter wants a livelier cadence,
    // so this pulls it faster while transmitting. When not transmitting it is a
    // single volatile read and return, so RX is untouched; during TX the RX audio
    // this loop shares is muted anyway, so the extra fetch costs nothing audible.
    // Display-only: TxMetersService gates its SWR auto-trip off under P3 because
    // this raw scale is not yet validated against the G2 watts calibration.
    private async Task PollTxMetersAsync(Uri diagnosticsUrl, CancellationToken ct)
    {
        if (_txMeters is null || !_sidecar.TxIqStreamingArmed) return;

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var last = Interlocked.Read(ref _lastTxMeterPollMs);
        if (last > 0 && nowMs - last < TxMeterPollMs) return;
        if (Interlocked.CompareExchange(ref _lastTxMeterPollMs, nowMs, last) != last) return;

        try
        {
            using var response = await _http.GetAsync(diagnosticsUrl, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            if (await JsonNode.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false) is not JsonObject root)
                return;
            if (root["p3"] is not JsonObject p3) return;
            if (!TryReadRawAdc(p3, "forwardPowerRaw", out var fwd)) return;
            TryReadRawAdc(p3, "reversePowerRaw", out var rev);
            _txMeters.OnTelemetryRaw(fwd, rev);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Non-critical: a missed sample just leaves the needle briefly stale.
            // Never let a meter hiccup disturb the audio loop it rides on.
            LogThrottled(ref _lastAudioFailureLogMs, ex, "p3.sidecar.tx-meters.poll.failed");
        }
    }

    // Parse a non-negative integer board-health ADC field into a ushort, clamping
    // into range. Returns false when the field is absent or non-numeric (older
    // sidecar without the board-health block) so the caller leaves the meter
    // untouched rather than feeding it a spurious zero.
    private static bool TryReadRawAdc(JsonObject obj, string name, out ushort value)
    {
        value = 0;
        if (obj[name] is not JsonValue node) return false;
        if (node.TryGetValue<long>(out var l))
        {
            value = (ushort)Math.Clamp(l, 0L, ushort.MaxValue);
            return true;
        }
        if (node.TryGetValue<double>(out var d) && double.IsFinite(d))
        {
            value = (ushort)Math.Clamp((long)Math.Round(d), 0L, ushort.MaxValue);
            return true;
        }
        return false;
    }

    private async Task PollHealthAsync(Uri diagnosticsUrl, CancellationToken ct)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var lastPollMs = Interlocked.Read(ref _lastHealthPollUnixMs);
        if (lastPollMs > 0 && nowMs - lastPollMs < HealthPollMs) return;
        if (Interlocked.CompareExchange(ref _lastHealthPollUnixMs, nowMs, lastPollMs) != lastPollMs)
            return;

        try
        {
            using var response = await _http.GetAsync(diagnosticsUrl, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var root = await JsonNode.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false) as JsonObject;
            if (root is null) return;

            var audioMs = Interlocked.Read(ref _lastAudioFrameUnixMs);
            var audioAgeMs = audioMs > 0 ? Math.Max(0, nowMs - audioMs) : (long?)null;
            var lastRestartMs = Interlocked.Read(ref _lastRestartRequestUnixMs);
            var txActivityMs = Math.Max(
                TxActivityUnixMs(root, nowMs),
                _sidecar.LastTxActivityUnixMs);
            if (txActivityMs > 0)
                Interlocked.Exchange(ref _lastTxActivityUnixMs, txActivityMs);
            var lastTxActivityMs = Interlocked.Read(ref _lastTxActivityUnixMs);
            if (!ShouldRestartForIncompleteRx(
                    root,
                    audioAgeMs,
                    nowMs,
                    lastRestartMs,
                    lastTxActivityMs,
                    out var reason))
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _lastRestartRequestUnixMs, nowMs, lastRestartMs) != lastRestartMs)
                return;

            Interlocked.Increment(ref _restartRequests);
            _lastRestartReason = reason;
            await _sidecar.RequestRestartAsync(reason, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _healthPollFailures);
            LogThrottled(ref _lastAudioFailureLogMs, ex, "p3.sidecar.health.poll.failed");
        }
    }

    internal static bool ShouldRestartForIncompleteRx(
        JsonObject diagnostics,
        long? audioAgeMs,
        long nowUnixMs,
        long lastRestartUnixMs,
        long lastTxActivityUnixMs,
        out string reason)
    {
        reason = string.Empty;
        if (lastRestartUnixMs > 0 && nowUnixMs - lastRestartUnixMs < RestartCooldownMs)
            return false;
        if (lastTxActivityUnixMs > 0 && nowUnixMs - lastTxActivityUnixMs < TxActivityRestartGraceMs)
            return false;

        if (diagnostics["hostedRadioSession"] is not JsonObject hosted)
            return false;

        var incomplete = JsonLong(hosted, "consecutiveIncompleteRxPolls", 0);
        var rxProcessed = JsonBool(hosted, "rxProcessed", fallback: true);
        if (rxProcessed || incomplete < IncompleteRxRestartPolls)
            return false;

        if (audioAgeMs is { } age && age < StaleAudioRestartMs)
            return false;

        var rxReason = JsonString(hosted, "rxReason", "incomplete RX poll");
        reason =
            $"Sidecar RX stalled: {incomplete} incomplete RX polls, " +
            $"audio age {(audioAgeMs?.ToString(CultureInfo.InvariantCulture) ?? "none")} ms, {rxReason}";
        return true;
    }

    internal static long TxActivityUnixMs(JsonObject diagnostics, long nowUnixMs)
    {
        if (diagnostics["txIqIngestLoop"] is JsonObject txLoop)
        {
            if (TryParseUtcUnixMs(JsonString(txLoop, "lastSenderUtc", string.Empty), out var lastSenderMs))
                return lastSenderMs;
        }

        if (diagnostics["p3"] is JsonObject p3 &&
            TryParseUtcUnixMs(JsonString(p3, "txIqIngressLastUtc", string.Empty), out var ingressMs))
        {
            return ingressMs;
        }

        return 0;
    }

    internal static bool TryBuildDisplayFrame(
        JsonObject source,
        uint seq,
        double tsUnixMs,
        bool reverseDisplayBins,
        out DisplayFrame frame) =>
        TryBuildDisplayFrame(
            source,
            seq,
            tsUnixMs,
            reverseDisplayBins,
            MaxDisplayWidth,
            out frame);

    internal static bool TryBuildDisplayFrame(
        JsonObject source,
        uint seq,
        double tsUnixMs,
        bool reverseDisplayBins,
        int maxDisplayWidth,
        out DisplayFrame frame)
    {
        frame = default;
        if (!JsonBool(source, "ready", fallback: true)) return false;
        var width = JsonInt(source, "width", 0);
        if (width <= 0 || width > MaxDisplayWidth) return false;

        var panDb = source["panDb"] as JsonArray;
        var wfDb = source["wfDb"] as JsonArray;
        if (panDb is null || wfDb is null || panDb.Count != width || wfDb.Count != width) return false;

        var centerHz = JsonLong(source, "centerHz", JsonLong(source, "centerFrequencyHz", 0));
        var hzPerPixel = (float)JsonDouble(source, "hzPerPixel", 0.0);
        if (centerHz <= 0 || !float.IsFinite(hzPerPixel) || hzPerPixel <= 0.0f) return false;

        var sourceWidth = width;
        var effectiveMaxWidth = Math.Clamp(maxDisplayWidth, 1, MaxDisplayWidth);
        if (width > effectiveMaxWidth)
        {
            var pan = JsonFloatArray(panDb, width, effectiveMaxWidth);
            var wf = JsonFloatArray(wfDb, width, effectiveMaxWidth);
            width = effectiveMaxWidth;
            hzPerPixel *= (float)((double)sourceWidth / width);
            if (!float.IsFinite(hzPerPixel) || hzPerPixel <= 0.0f) return false;

            frame = BuildDisplayFrameFromArrays(source, seq, tsUnixMs, reverseDisplayBins, width, centerHz, hzPerPixel, pan, wf);
            return frame.BodyFlags != DisplayBodyFlags.None;
        }
        else
        {
            var pan = JsonFloatArray(panDb, width);
            var wf = JsonFloatArray(wfDb, width);
            frame = BuildDisplayFrameFromArrays(source, seq, tsUnixMs, reverseDisplayBins, width, centerHz, hzPerPixel, pan, wf);
            return frame.BodyFlags != DisplayBodyFlags.None;
        }
    }

    private static DisplayFrame BuildDisplayFrameFromArrays(
        JsonObject source,
        uint seq,
        double tsUnixMs,
        bool reverseDisplayBins,
        int width,
        long centerHz,
        float hzPerPixel,
        float[] pan,
        float[] wf)
    {
        var lowToHigh = JsonBool(source, "lowToHigh", fallback: true);
        var sidecarReverseDisplayBins = JsonBool(source, "reverseDisplayBins", fallback: false);
        var hostShouldReverseDisplayBins = JsonBool(source, "hostShouldReverseDisplayBins", fallback: false);
        if (reverseDisplayBins || sidecarReverseDisplayBins || hostShouldReverseDisplayBins || !lowToHigh)
        {
            Array.Reverse(pan);
            Array.Reverse(wf);
        }

        var flags = DisplayBodyFlags.None;
        if (pan.Length == width) flags |= DisplayBodyFlags.PanValid;
        if (wf.Length == width) flags |= DisplayBodyFlags.WfValid;
        return new DisplayFrame(
            Seq: seq,
            TsUnixMs: tsUnixMs,
            RxId: checked((byte)Math.Clamp(JsonInt(source, "rxId", 0), 0, byte.MaxValue)),
            BodyFlags: flags,
            Width: checked((ushort)width),
            CenterHz: centerHz,
            HzPerPixel: hzPerPixel,
            PanDb: pan,
            WfDb: wf,
            // DDC passband width, so the edge-guard masks the true band edges
            // rather than a fixed fraction of a zoomed view (0 = unknown → legacy).
            SampleRateHz: JsonLong(source, "sampleRateHz", 0));
    }

    internal int ForwardRadioMicPacketPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0) return 0;
        if (payload.Length % RadioMicReceiver.PacketBytes != 0)
            throw new InvalidDataException("Protocol 3 radio-mic payload is not aligned to 132-byte packets.");

        var packets = payload.Length / RadioMicReceiver.PacketBytes;
        for (var offset = 0; offset < payload.Length; offset += RadioMicReceiver.PacketBytes)
        {
            _radioMicReceiver.Accept(payload.Slice(offset, RadioMicReceiver.PacketBytes));
        }

        return packets;
    }

    internal static bool ResolveReverseDisplayBins(IConfiguration configuration) =>
        BoolSetting(
            configuration,
            "ReverseDisplayBins",
            "ZEUS_PROTOCOL3_REVERSE_DISPLAY_BINS",
            fallback: DefaultReverseDisplayBins);

    internal static int ResolveDisplayPollMs(IConfiguration configuration)
    {
        var envValue = Environment.GetEnvironmentVariable("ZEUS_PROTOCOL3_FRAME_DISPLAY_POLL_MS");
        if (!string.IsNullOrWhiteSpace(envValue))
            return IntValue(envValue, DefaultDisplayPollMs, 8, 500);

        var configured = configuration["Zeus:Protocol3:Sidecar:DisplayPollMs"];
        return IntValue(configured, DefaultDisplayPollMs, DefaultDisplayPollMs, 500);
    }

    internal static int ResolveDisplayMaxWidth(IConfiguration configuration) =>
        IntSetting(
            configuration,
            "DisplayMaxWidth",
            "ZEUS_PROTOCOL3_FRAME_DISPLAY_MAX_WIDTH",
            DefaultDisplayMaxWidth,
            256,
            MaxDisplayWidth);

    internal static double ResolveDisplayEdgeGuardHz(IConfiguration configuration) =>
        DoubleSetting(
            configuration,
            "DisplayEdgeGuardHz",
            "ZEUS_PROTOCOL3_FRAME_DISPLAY_EDGE_GUARD_HZ",
            DefaultDisplayEdgeGuardHz,
            0.0,
            48000.0);

    internal static int ResolveAudioPollMs(IConfiguration configuration) =>
        IntSetting(
            configuration,
            "AudioPollMs",
            "ZEUS_PROTOCOL3_FRAME_AUDIO_POLL_MS",
            DefaultAudioPollMs,
            2,
            250);

    private bool IsRadioMicSourceActive()
    {
        var source = _radio.Snapshot().TxAudioSource;
        _txIngest.SetActiveSource(source);
        return source != TxAudioSource.Host;
    }

    internal static IReadOnlyList<AudioFrame> ParseAudioFramesPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 16) return Array.Empty<AudioFrame>();
        if (payload[0] != (byte)'P' ||
            payload[1] != (byte)'3' ||
            payload[2] != (byte)'A' ||
            payload[3] != (byte)'U')
        {
            throw new InvalidDataException("Protocol 3 audio payload has invalid magic.");
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..6]);
        var headerBytes = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..8]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..12]);
        if (version != 1) throw new InvalidDataException($"Unsupported Protocol 3 audio payload version {version}.");
        if (headerBytes < 16 || headerBytes > payload.Length)
            throw new InvalidDataException("Protocol 3 audio payload has invalid header length.");
        if (count > 256) throw new InvalidDataException("Protocol 3 audio payload frame count is unreasonable.");

        var frames = new List<AudioFrame>(checked((int)count));
        var offset = (int)headerBytes;
        for (var index = 0; index < count; index++)
        {
            if (offset + 32 > payload.Length)
                throw new InvalidDataException("Protocol 3 audio frame header is truncated.");

            var header = payload.Slice(offset, 32);
            var rxId = header[0];
            var channels = header[1];
            var seq = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
            var tsMs = BinaryPrimitives.ReadUInt64LittleEndian(header[8..16]);
            var sampleRateHz = BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]);
            var sampleCount = BinaryPrimitives.ReadUInt32LittleEndian(header[20..24]);
            var sampleBytes = BinaryPrimitives.ReadUInt32LittleEndian(header[24..28]);
            offset += 32;

            if (channels == 0) throw new InvalidDataException("Protocol 3 audio frame has zero channels.");
            if (sampleCount > ushort.MaxValue) throw new InvalidDataException("Protocol 3 audio frame has too many samples.");
            var expectedBytes = checked(sampleCount * channels * sizeof(float));
            if (sampleBytes != expectedBytes)
                throw new InvalidDataException("Protocol 3 audio frame sample byte count does not match header.");
            if (offset + sampleBytes > payload.Length)
                throw new InvalidDataException("Protocol 3 audio frame samples are truncated.");

            var samples = new float[checked((int)(sampleCount * channels))];
            payload.Slice(offset, checked((int)sampleBytes)).CopyTo(MemoryMarshal.AsBytes(samples.AsSpan()));
            offset += checked((int)sampleBytes);

            frames.Add(new AudioFrame(
                Seq: seq,
                TsUnixMs: tsMs,
                RxId: rxId,
                Channels: channels,
                SampleRateHz: sampleRateHz,
                SampleCount: checked((ushort)sampleCount),
                Samples: samples));
        }

        return frames;
    }

    private static float[] JsonFloatArray(JsonArray array, int width)
    {
        var values = new float[width];
        for (var index = 0; index < width; index++)
        {
            values[index] = JsonArrayFloat(array, index);
        }
        return values;
    }

    private static float[] JsonFloatArray(JsonArray array, int sourceWidth, int targetWidth)
    {
        if (targetWidth <= 0) throw new ArgumentOutOfRangeException(nameof(targetWidth));
        if (sourceWidth <= targetWidth) return JsonFloatArray(array, sourceWidth);

        var values = new float[targetWidth];
        var scale = (double)sourceWidth / targetWidth;
        for (var target = 0; target < targetWidth; target++)
        {
            var start = (int)Math.Floor(target * scale);
            var end = (int)Math.Floor((target + 1) * scale);
            if (end <= start) end = start + 1;
            start = Math.Clamp(start, 0, sourceWidth - 1);
            end = Math.Clamp(end, start + 1, sourceWidth);

            double sum = 0.0;
            var count = 0;
            for (var sourceIndex = start; sourceIndex < end; sourceIndex++)
            {
                var value = JsonArrayFloat(array, sourceIndex);
                if (!float.IsFinite(value)) continue;
                sum += value;
                count++;
            }

            values[target] = count > 0 ? (float)(sum / count) : DisplayFloorDb;
        }

        return values;
    }

    private static float JsonArrayFloat(JsonArray array, int index)
    {
        var value = JsonArrayDouble(array, index, DisplayFloorDb);
        var sample = (float)value;
        return float.IsFinite(sample) ? sample : DisplayFloorDb;
    }

    internal static float[] DownsampleBins(ReadOnlySpan<float> source, int targetWidth)
    {
        if (targetWidth <= 0) throw new ArgumentOutOfRangeException(nameof(targetWidth));
        if (source.Length <= targetWidth) return source.ToArray();

        var values = new float[targetWidth];
        var scale = (double)source.Length / targetWidth;
        for (var target = 0; target < targetWidth; target++)
        {
            var start = (int)Math.Floor(target * scale);
            var end = (int)Math.Floor((target + 1) * scale);
            if (end <= start) end = start + 1;
            start = Math.Clamp(start, 0, source.Length - 1);
            end = Math.Clamp(end, start + 1, source.Length);

            double sum = 0.0;
            var count = 0;
            for (var sourceIndex = start; sourceIndex < end; sourceIndex++)
            {
                var value = source[sourceIndex];
                if (!float.IsFinite(value)) continue;
                sum += value;
                count++;
            }

            values[target] = count > 0 ? (float)(sum / count) : DisplayFloorDb;
        }

        return values;
    }

    internal static void ApplyDisplayEdgeGuard(
        float[] panDb,
        float[] wfDb,
        float hzPerPixel,
        double edgeGuardHz,
        long sampleRateHz)
    {
        if (panDb.Length == 0 || wfDb.Length == 0) return;
        if (!float.IsFinite(hzPerPixel) || hzPerPixel <= 0.0f) return;
        if (!double.IsFinite(edgeGuardHz) || edgeGuardHz <= 0.0) return;

        var width = Math.Min(panDb.Length, wfDb.Length);
        if (width < 4) return;

        int floorBins;
        if (sampleRateHz > 0)
        {
            // Mask only the TRUE DDC passband edges, not a fixed fraction of the
            // (possibly zoomed) view. The passband spans ±sampleRateHz/2 around
            // the display centre; the guard reaches this far into each display
            // edge. When zoomed into mid-band the visible window sits well inside
            // the passband, the reach goes negative, and NOTHING is masked —
            // fixing the "black edges eat real spectrum when zoomed" bug where a
            // flat width/10 slice was always blanked.
            var passbandHalfSpanHz = sampleRateHz / 2.0;
            var displayHalfSpanHz = (width / 2.0) * hzPerPixel;
            var overreachHz = edgeGuardHz - (passbandHalfSpanHz - displayHalfSpanHz);
            if (overreachHz <= 0.0) return;
            floorBins = (int)Math.Ceiling(overreachHz / hzPerPixel);
        }
        else
        {
            // Unknown passband geometry (e.g. deserialized frame): legacy
            // fixed-fraction guard, unchanged for backward compatibility.
            floorBins = (int)Math.Ceiling(edgeGuardHz / hzPerPixel);
        }

        floorBins = Math.Clamp(floorBins, 1, Math.Max(1, width / 10));
        var fadeBins = Math.Min(floorBins, Math.Max(0, (width / 2) - floorBins));
        ApplyDisplayEdgeGuard(panDb, floorBins, fadeBins);
        ApplyDisplayEdgeGuard(wfDb, floorBins, fadeBins);
    }

    private static void ApplyDisplayEdgeGuard(float[] values, int floorBins, int fadeBins)
    {
        var width = values.Length;
        var clampedFloorBins = Math.Min(floorBins, width / 2);
        for (var offset = 0; offset < clampedFloorBins; offset++)
        {
            values[offset] = DisplayFloorDb;
            values[width - 1 - offset] = DisplayFloorDb;
        }

        for (var offset = 0; offset < fadeBins; offset++)
        {
            var left = clampedFloorBins + offset;
            var right = width - 1 - left;
            if (left > right) break;

            var t = (offset + 1.0) / (fadeBins + 1.0);
            var gain = t * t * (3.0 - (2.0 * t));
            values[left] = EdgeGuardBlend(values[left], gain);
            if (right != left) {
                values[right] = EdgeGuardBlend(values[right], gain);
            }
        }
    }

    private static float EdgeGuardBlend(float value, double gain)
    {
        if (!float.IsFinite(value)) return DisplayFloorDb;
        return (float)(DisplayFloorDb + ((value - DisplayFloorDb) * gain));
    }

    private static Uri Endpoint(Uri diagnosticsUrl, string path, string query)
    {
        return new UriBuilder(diagnosticsUrl)
        {
            Path = path,
            Query = query,
            Fragment = string.Empty,
        }.Uri;
    }

    private void LogThrottled(ref long lastLogMs, Exception ex, string message)
    {
        var now = Environment.TickCount64;
        var prior = Interlocked.Read(ref lastLogMs);
        if (now - prior < 1_000) return;
        Interlocked.Exchange(ref lastLogMs, now);
        _log.LogWarning(ex, message);
    }

    private static async Task DelayQuietAsync(int ms, CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMilliseconds(ms), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private static int IntSetting(
        IConfiguration configuration,
        string key,
        string env,
        int fallback,
        int min,
        int max)
    {
        var value = Environment.GetEnvironmentVariable(env);
        if (string.IsNullOrWhiteSpace(value)) value = configuration[$"Zeus:Protocol3:Sidecar:{key}"];
        return IntValue(value, fallback, min, max);
    }

    private static int IntValue(string? value, int fallback, int min, int max) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, min, max)
            : fallback;

    private static double DoubleSetting(
        IConfiguration configuration,
        string key,
        string env,
        double fallback,
        double min,
        double max)
    {
        var value = Environment.GetEnvironmentVariable(env);
        if (string.IsNullOrWhiteSpace(value)) value = configuration[$"Zeus:Protocol3:Sidecar:{key}"];
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
            double.IsFinite(parsed)
            ? Math.Clamp(parsed, min, max)
            : fallback;
    }

    private static bool BoolSetting(IConfiguration configuration, string key, string env, bool fallback)
    {
        var value = Environment.GetEnvironmentVariable(env);
        if (string.IsNullOrWhiteSpace(value)) value = configuration[$"Zeus:Protocol3:Sidecar:{key}"];
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool JsonBool(JsonObject obj, string property, bool fallback)
    {
        try { return obj[property]?.GetValue<bool>() ?? fallback; }
        catch { return fallback; }
    }

    private static int JsonInt(JsonObject obj, string property, int fallback)
    {
        try
        {
            var node = obj[property];
            if (node is null) return fallback;
            if (node is JsonValue value && value.TryGetValue<int>(out var i)) return i;
            if (node is JsonValue longValue &&
                longValue.TryGetValue<long>(out var l) &&
                l >= int.MinValue &&
                l <= int.MaxValue)
                return (int)l;
        }
        catch { }
        return fallback;
    }

    private static long JsonLong(JsonObject obj, string property, long fallback)
    {
        try
        {
            var node = obj[property];
            if (node is null) return fallback;
            if (node is JsonValue intValue && intValue.TryGetValue<int>(out var i)) return i;
            if (node is JsonValue value && value.TryGetValue<long>(out var l)) return l;
            if (node is JsonValue uintValue && uintValue.TryGetValue<uint>(out var u)) return u;
            if (node is JsonValue ulongValue && ulongValue.TryGetValue<ulong>(out var ul))
                return checked((long)Math.Min(ul, (ulong)long.MaxValue));
            if (node is JsonValue doubleValue &&
                doubleValue.TryGetValue<double>(out var d) &&
                double.IsFinite(d))
                return (long)Math.Round(d);
        }
        catch { }
        return fallback;
    }

    private static double JsonDouble(JsonObject obj, string property, double fallback)
    {
        try
        {
            var value = obj[property]?.GetValue<double>() ?? fallback;
            return double.IsFinite(value) ? value : fallback;
        }
        catch { return fallback; }
    }

    private static string JsonString(JsonObject obj, string property, string fallback)
    {
        try
        {
            var value = obj[property]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
        catch { return fallback; }
    }

    private static bool TryParseUtcUnixMs(string value, out long unixMs)
    {
        unixMs = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dto))
        {
            return false;
        }

        unixMs = dto.ToUnixTimeMilliseconds();
        return true;
    }

    private static double JsonArrayDouble(JsonArray array, int index, double fallback)
    {
        try
        {
            var value = array[index]?.GetValue<double>() ?? fallback;
            return double.IsFinite(value) ? value : fallback;
        }
        catch { return fallback; }
    }
}
