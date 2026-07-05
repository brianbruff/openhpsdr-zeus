// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Zeus.Contracts;

namespace Zeus.Server;

public sealed record Protocol3SidecarSnapshot(
    Uri DiagnosticsUrl,
    string Status,
    string DspEngine,
    int RxExpectedStreams,
    int RxActiveStreams,
    int DspActiveChannels);

internal sealed record Protocol3SidecarDisplayFrame(
    byte RxId,
    DisplayBodyFlags BodyFlags,
    long CenterHz,
    float HzPerPixel,
    float[] PanDb,
    float[] WfDb,
    string Source);

public sealed class Protocol3SidecarBridge : IDisposable
{
    public const string HttpClientName = "Protocol3Sidecar";
    private const string HostedSessionArmValue = "ENABLE_SIDECAR_P3_HOSTED_SESSION";
    private const int DefaultAnalyzerFftLength = 65_536;
    private const int DefaultReceiveMs = 0;
    private const int DefaultControlTimeoutMs = 250;
    private const int TxIqFramesPerPacket = 240;
    private const int TxIqBytesPerFrame = 6;
    private const int TxIqPayloadBytes = TxIqFramesPerPacket * TxIqBytesPerFrame;
    private const int TxIqS24Max = 0x7f_ffff;
    private const int TxIqS24Min = -0x80_0000;
    private const int TxIqSendQueueCapacity = 2048;
    // TX-IQ egress DAC rate: the radio's DUC consumes interleaved IQ at this
    // sample rate (ducCompatibleTxRateHz), so each 240-frame packet is 1.25 ms
    // of transmit audio. RunTxIqSenderAsync meters packets out at this cadence
    // instead of flushing each 20 ms mic burst all at once — the burst-then-idle
    // pattern overran then starved the radio's DUC FIFO, which is what produced
    // the "TX not steady" sequence-gap/drop stutter on voice.
    private const int TxIqDacRateHz = 192_000;
    // How far behind the DAC schedule the pacer may fall before it re-anchors
    // and drains at full speed (a genuine backlog after a burst). Bounds the
    // in-flight depth well under TxIqSendQueueCapacity so the pacer smooths the
    // steady stream yet never accumulates unbounded debt or under-delivers.
    private const int TxIqPacerMaxLeadPackets = 64;
    private const int TxControlKeepalivePeriodMs = 100;
    private const string SidecarPidFileName = "protocol3-sidecar.pid";

    private readonly object _sync = new();
    private readonly object _txIqSync = new();
    private readonly SemaphoreSlim _restartSync = new(1, 1);
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<Protocol3SidecarBridge> _log;
    private Process? _process;
    private ProcessOutputTail? _processOutput;
    private bool _ownsProcess;
    private Uri? _diagnosticsUrl;
    private Protocol3SidecarSnapshot? _lastSnapshot;
    private IPEndPoint? _txIqIngestEndpoint;
    private Channel<byte[]>? _txIqSendQueue;
    private CancellationTokenSource? _txIqSendCts;
    private Task? _txIqSendTask;
    private readonly object _txControlKeepaliveSync = new();
    private CancellationTokenSource? _txControlKeepaliveCts;
    private Task? _txControlKeepaliveTask;
    private string? _txControlKeepalivePayload;
    private readonly byte[] _txIqPacket = new byte[TxIqPayloadBytes];
    private int _txIqPacketBytes;
    private int _txIqQueueDepth;
    private long _txIqPacketsSent;
    private long _txIqPacketsDropped;
    private long _txIqPacketsQueued;
    private long _txIqQueueDropped;
    private long _txControlPosts;
    private long _lastTxActivityUnixMs;
    private long _controlPostSuccesses;
    private long _controlPostMissingUrl;
    private int _txIqStreamingArmed;

    /// <summary>
    /// True while the host TX-IQ path is armed (key-down through unkey). The
    /// sidecar frame forwarder holds RX display frames while this is set so
    /// the WDSP TX display owns the panadapter/waterfall during transmit —
    /// P2-parity behaviour, and it prevents the two frame sources from
    /// alternating widths (planner-reset smear) while keyed.
    /// </summary>
    internal bool TxIqStreamingArmed => Volatile.Read(ref _txIqStreamingArmed) != 0;
    private string? _lastControlPostUrl;
    private int _lastControlPostStatus;
    private string? _lastControlPostError;
    private string? _lastControlPostBody;
    private string? _lastTxControlPayload;
    private Protocol3SidecarConnection? _lastConnection;
    private StateDto? _lastReceiverState;
    private int _lastReceiverRxStreams;
    private int _lastReceiverSampleRateHz;
    private long _restartAttempts;
    private long _restartSuccesses;
    private long _restartFailures;
    private long _lastRestartUnixMs;
    private string? _lastRestartReason;
    private string? _lastRestartError;

    private sealed record Protocol3SidecarConnection(
        IPEndPoint RadioEndpoint,
        int P3Port,
        int SampleRateHz,
        int RxStreams,
        StateDto InitialState);

    internal readonly record struct SidecarPidRecord(
        int ProcessId,
        long StartUnixMs,
        string DiagnosticsUrl);

    public Uri? DiagnosticsUrl
    {
        get
        {
            lock (_sync) return _diagnosticsUrl;
        }
    }

    public Protocol3SidecarBridge(
        IHttpClientFactory httpFactory,
        IConfiguration configuration,
        ILogger<Protocol3SidecarBridge> log)
    {
        _httpFactory = httpFactory;
        _configuration = configuration;
        _log = log;
    }

    public object Status
    {
        get
        {
            IPEndPoint? txIqEndpoint;
            int txIqPartialBytes;
            int txIqQueueDepth;
            lock (_txIqSync)
            {
                txIqEndpoint = _txIqIngestEndpoint;
                txIqPartialBytes = _txIqPacketBytes;
                txIqQueueDepth = _txIqQueueDepth;
            }

            lock (_sync)
            {
                var lastRestartUnixMs = Interlocked.Read(ref _lastRestartUnixMs);
                return new
                {
                    configured = ExistingDiagnosticsUrl() is not null ||
                        !string.IsNullOrWhiteSpace(Setting("Project", "ZEUS_PROTOCOL3_SIDECAR_PROJECT")),
                    running = _process is { HasExited: false } || (_diagnosticsUrl is not null && !_ownsProcess),
                    diagnosticsUrl = _diagnosticsUrl?.ToString(),
                    last = _lastSnapshot,
                    sidecarOutput = _processOutput?.Snapshot(),
                    txIqIngestEndpoint = txIqEndpoint?.ToString(),
                    txIqPacketsSent = Interlocked.Read(ref _txIqPacketsSent),
                    txIqPacketsDropped = Interlocked.Read(ref _txIqPacketsDropped),
                    txIqPacketsQueued = Interlocked.Read(ref _txIqPacketsQueued),
                    txIqQueueDropped = Interlocked.Read(ref _txIqQueueDropped),
                    txIqQueueDepth,
                    txIqPartialBytes,
                    txIqStreamingArmed = Volatile.Read(ref _txIqStreamingArmed) != 0,
                    txControlPosts = Interlocked.Read(ref _txControlPosts),
                    lastTxActivityUnixMs = LastTxActivityUnixMs is > 0 ? LastTxActivityUnixMs : (long?)null,
                    controlPostSuccesses = Interlocked.Read(ref _controlPostSuccesses),
                    controlPostMissingUrl = Interlocked.Read(ref _controlPostMissingUrl),
                    lastControlPostUrl = _lastControlPostUrl,
                    lastControlPostStatus = _lastControlPostStatus,
                    lastControlPostError = _lastControlPostError,
                    lastControlPostBody = _lastControlPostBody,
                    lastTxControlPayload = _lastTxControlPayload,
                    restartAttempts = Interlocked.Read(ref _restartAttempts),
                    restartSuccesses = Interlocked.Read(ref _restartSuccesses),
                    restartFailures = Interlocked.Read(ref _restartFailures),
                    lastRestartUnixMs = lastRestartUnixMs > 0 ? lastRestartUnixMs : (long?)null,
                    lastRestartReason = _lastRestartReason,
                    lastRestartError = _lastRestartError,
                };
            }
        }
    }

    public async Task<Protocol3SidecarSnapshot> ConnectAsync(
        IPEndPoint radioEndpoint,
        int p3Port,
        int sampleRateHz,
        int rxStreams,
        StateDto initialState,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(radioEndpoint);
        if (p3Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(p3Port));
        if (sampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        if (rxStreams is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(rxStreams));

        RememberConnection(radioEndpoint, p3Port, sampleRateHz, rxStreams, initialState);

        var existingUrl = ExistingDiagnosticsUrl();
        if (existingUrl is not null)
        {
            var existing = await WaitForReadyAsync(existingUrl, rxStreams, TimeSpan.FromSeconds(10), ct)
                .ConfigureAwait(false);
            Remember(existing, ownsProcess: false, process: null, txIqIngestPort: ConfiguredTxIqIngestPort());
            return existing;
        }

        var project = FullPathSetting("Project", "ZEUS_PROTOCOL3_SIDECAR_PROJECT");
        var n9dspLibrary = FullPathSetting("N9DspLibrary", "ZEUS_PROTOCOL3_N9DSP_LIBRARY");
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(n9dspLibrary))
        {
            throw new InvalidOperationException(
                "Protocol 3 sidecar is not configured. Set ZEUS_PROTOCOL3_SIDECAR_PROJECT to the private " +
                "Zeus.Protocol3.Probe.csproj and ZEUS_PROTOCOL3_N9DSP_LIBRARY to the private n9dsp native library.");
        }
        if (!File.Exists(project))
            throw new InvalidOperationException($"Protocol 3 sidecar project was not found: {project}");
        if (!File.Exists(n9dspLibrary))
            throw new InvalidOperationException($"N9DSP native library was not found: {n9dspLibrary}");

        await DisconnectAsync(ct).ConfigureAwait(false);
        StopPreviousOwnedSidecar();

        var listenUrl = ListenUrl();
        var diagnosticsUrl = DiagnosticsUrlForListen(listenUrl);
        var txIqIngestPort = TxIqIngestPort();
        var psi = BuildStartInfo(project, n9dspLibrary, listenUrl, radioEndpoint.Address, p3Port, sampleRateHz, rxStreams, initialState, txIqIngestPort);
        var process = Process.Start(psi) ??
            throw new InvalidOperationException("Protocol 3 sidecar process did not start.");
        WriteOwnedSidecarPidFile(process, diagnosticsUrl);
        var output = ProcessOutputTail.Attach(process);
        Remember(null, true, process, diagnosticsUrl, output, txIqIngestPort);
        _log.LogInformation(
            "p3.sidecar.start pid={Pid} radio={Radio} p3Port={Port} diagnostics={Diagnostics} txIqIngest={TxIqIngestPort}",
            process.Id,
            radioEndpoint.Address,
            p3Port,
            diagnosticsUrl,
            txIqIngestPort);

        try
        {
            var snapshot = await WaitForReadyAsync(diagnosticsUrl, rxStreams, TimeSpan.FromSeconds(15), ct)
                .ConfigureAwait(false);
            Remember(snapshot, true, process, diagnosticsUrl, output, txIqIngestPort);
            return snapshot;
        }
        catch
        {
            await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        Process? process;
        bool kill;
        lock (_sync)
        {
            process = _process;
            kill = _ownsProcess;
            _process = null;
            _processOutput = null;
            _ownsProcess = false;
            _diagnosticsUrl = null;
            _lastSnapshot = null;
        }
        StopTxControlKeepalive();
        ClearTxIqIngest();

        if (process is null || !kill) return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "p3.sidecar.stop.error");
        }
        finally
        {
            DeleteOwnedSidecarPidFile(process);
            process.Dispose();
        }
    }

    internal async Task<Protocol3SidecarDisplayFrame?> FetchWidebandDisplayFrameAsync(
        int targetWidth,
        CancellationToken ct)
    {
        if (targetWidth <= 0) throw new ArgumentOutOfRangeException(nameof(targetWidth));

        Uri? diagnosticsUrl;
        lock (_sync)
        {
            diagnosticsUrl = _diagnosticsUrl ?? _lastSnapshot?.DiagnosticsUrl;
        }
        if (diagnosticsUrl is null) return null;

        var displayUrl = DisplayFramesUrlForDiagnostics(diagnosticsUrl);
        var http = _httpFactory.CreateClient(HttpClientName);
        using var response = await http.GetAsync(displayUrl, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Protocol 3 sidecar display frames returned HTTP {(int)response.StatusCode}.");

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var root = await JsonNode.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false) as JsonObject
            ?? throw new InvalidOperationException("Protocol 3 sidecar display frames root is not an object.");
        return TryParseWidebandDisplayFrame(root, targetWidth, out var frame) ? frame : null;
    }

    public void Dispose()
    {
        try { DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch { }
    }

    internal async Task<bool> RequestRestartAsync(string reason, CancellationToken ct)
    {
        if (!await _restartSync.WaitAsync(0, ct).ConfigureAwait(false))
            return false;

        try
        {
            Protocol3SidecarConnection? connection;
            StateDto? receiverState;
            bool ownsProcess;
            lock (_sync)
            {
                connection = _lastConnection;
                receiverState = _lastReceiverState;
                ownsProcess = _ownsProcess;
            }

            if (connection is null || !ownsProcess)
            {
                _lastRestartError = "restart skipped; sidecar is not owned by this Zeus process.";
                return false;
            }

            var state = receiverState ?? connection.InitialState;
            Interlocked.Increment(ref _restartAttempts);
            Interlocked.Exchange(ref _lastRestartUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _lastRestartReason = reason;
            _lastRestartError = null;
            _log.LogWarning("p3.sidecar.restart.request reason={Reason}", reason);

            await DisconnectAsync(ct).ConfigureAwait(false);
            await ConnectAsync(
                    connection.RadioEndpoint,
                    connection.P3Port,
                    connection.SampleRateHz,
                    connection.RxStreams,
                    state,
                    ct)
                .ConfigureAwait(false);

            Interlocked.Increment(ref _restartSuccesses);
            _log.LogInformation("p3.sidecar.restart.complete reason={Reason}", reason);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _restartFailures);
            _lastRestartError = ex.Message;
            _log.LogWarning(ex, "p3.sidecar.restart.failed reason={Reason}", reason);
            return false;
        }
        finally
        {
            _restartSync.Release();
        }
    }

    internal long LastTxActivityUnixMs => Interlocked.Read(ref _lastTxActivityUnixMs);

    internal void ForwardTxIq(ReadOnlySpan<float> iqInterleaved)
    {
        if (iqInterleaved.Length < 2) return;
        if (Volatile.Read(ref _txIqStreamingArmed) == 0) return;

        lock (_txIqSync)
        {
            var queue = _txIqSendQueue;
            if (_txIqIngestEndpoint is null || queue is null) return;

            var frames = iqInterleaved.Length / 2;
            for (var frame = 0; frame < frames; frame++)
            {
                WriteS24BigEndian(_txIqPacket, _txIqPacketBytes, FloatToS24(iqInterleaved[frame * 2]));
                _txIqPacketBytes += 3;
                WriteS24BigEndian(_txIqPacket, _txIqPacketBytes, FloatToS24(iqInterleaved[frame * 2 + 1]));
                _txIqPacketBytes += 3;

                if (_txIqPacketBytes == TxIqPayloadBytes)
                    EnqueueTxIqPacketLocked(queue);
            }
        }
    }

    internal static Protocol3SidecarSnapshot ValidateDiagnosticsSnapshot(
        JsonObject diagnostics,
        Uri diagnosticsUrl,
        int expectedRxStreams)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(diagnosticsUrl);
        ValidateDiagnosticsUrl(diagnosticsUrl);

        var protocol = RequiredString(diagnostics, "protocol");
        if (!string.Equals(protocol, "p3", StringComparison.Ordinal))
            throw new InvalidOperationException($"Protocol 3 sidecar diagnostics reported protocol '{protocol}', expected 'p3'.");

        var p3 = RequiredObject(diagnostics, "p3");
        var status = RequiredString(p3, "status");
        if (!string.Equals(status, "ok", StringComparison.Ordinal) &&
            !string.Equals(status, "degraded", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Protocol 3 sidecar diagnostics status is '{status}', expected 'ok' or 'degraded'.");

        var rxActive = RequiredInt(p3, "rxActiveStreams");
        var rxExpected = p3.ContainsKey("rxExpectedStreams")
            ? RequiredInt(p3, "rxExpectedStreams")
            : rxActive;
        if (rxExpected < expectedRxStreams || rxActive < expectedRxStreams)
            throw new InvalidOperationException(
                $"Protocol 3 sidecar reported {rxActive}/{rxExpected} RX streams; Zeus requires {expectedRxStreams}.");

        var dsp = RequiredObject(p3, "dsp");
        var engine = RequiredString(dsp, "engine");
        if (!string.Equals(engine, "n9dsp", StringComparison.Ordinal))
            throw new InvalidOperationException($"Protocol 3 sidecar DSP engine is '{engine}', expected 'n9dsp'.");

        var dspChannels = dsp.ContainsKey("activeChannels")
            ? RequiredInt(dsp, "activeChannels")
            : rxActive;
        if (dspChannels < expectedRxStreams)
            throw new InvalidOperationException(
                $"Protocol 3 sidecar N9DSP reports {dspChannels} active DSP channels; Zeus requires {expectedRxStreams}.");

        return new Protocol3SidecarSnapshot(
            diagnosticsUrl,
            status,
            engine,
            rxExpected,
            rxActive,
            dspChannels);
    }

    internal static bool TryParseWidebandDisplayFrame(
        JsonObject displayFrames,
        int targetWidth,
        out Protocol3SidecarDisplayFrame? frame)
    {
        ArgumentNullException.ThrowIfNull(displayFrames);
        if (targetWidth <= 0) throw new ArgumentOutOfRangeException(nameof(targetWidth));

        frame = null;
        if (displayFrames["frames"] is not JsonArray frames) return false;

        foreach (var node in frames)
        {
            if (node is not JsonObject candidate) continue;
            if (!StringEquals(candidate, "contract", "Zeus.Contracts.DisplayFrame.v1")) continue;
            if (!BoolValue(candidate, "ready", fallback: true)) continue;
            if (candidate["panDb"] is not JsonArray pan || candidate["wfDb"] is not JsonArray wf)
                continue;

            var declaredWidth = IntValue(candidate, "width", Math.Min(pan.Count, wf.Count));
            var sourceWidth = Math.Min(declaredWidth, Math.Min(pan.Count, wf.Count));
            if (sourceWidth <= 0) continue;

            var hzPerPixel = DoubleValue(candidate, "hzPerPixel", double.NaN);
            var spanHz = DoubleValue(candidate, "spanHz",
                double.IsFinite(hzPerPixel) && hzPerPixel > 0 ? hzPerPixel * sourceWidth : double.NaN);
            var centerHz = LongValue(candidate, "centerHz",
                (long)Math.Round(DoubleValue(candidate, "centerFrequencyHz", WidebandSpectrumAnalyzer.DisplayCenterHz)));
            if (!CoversWidebandDisplay(candidate, centerHz, spanHz, sourceWidth, hzPerPixel))
                continue;

            var reverse = BoolValue(candidate, "hostShouldReverseDisplayBins", fallback: false) ||
                !BoolValue(candidate, "lowToHigh", fallback: true);
            var outputPan = ResampleDbArray(pan, sourceWidth, targetWidth, reverse);
            var outputWf = ResampleDbArray(wf, sourceWidth, targetWidth, reverse);

            var outputSpanHz = double.IsFinite(spanHz) && spanHz > 0
                ? spanHz
                : hzPerPixel * sourceWidth;
            var outputHzPerPixel = (float)(outputSpanHz / targetWidth);
            if (!float.IsFinite(outputHzPerPixel) || outputHzPerPixel <= 0)
                outputHzPerPixel = WidebandSpectrumAnalyzer.HzPerPixel;

            var rxId = (byte)Math.Clamp(IntValue(candidate, "rxId", 0), 0, byte.MaxValue);
            frame = new Protocol3SidecarDisplayFrame(
                rxId,
                ParseDisplayBodyFlags(StringValue(candidate, "bodyFlags", "PanValid|WfValid")),
                centerHz,
                outputHzPerPixel,
                outputPan,
                outputWf,
                StringValue(candidate, "source", "p3-wideband"));
            return true;
        }

        return false;
    }

    private async Task<Protocol3SidecarSnapshot> WaitForReadyAsync(
        Uri diagnosticsUrl,
        int rxStreams,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var snapshot = await FetchSnapshotAsync(diagnosticsUrl, rxStreams, ct).ConfigureAwait(false);
                _log.LogInformation(
                    "p3.sidecar.ready diagnostics={Diagnostics} rx={RxActive}/{RxExpected} dsp={DspChannels}",
                    diagnosticsUrl,
                    snapshot.RxActiveStreams,
                    snapshot.RxExpectedStreams,
                    snapshot.DspActiveChannels);
                return snapshot;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                if (HasOwnedProcessExited(out var exitCode, out var output))
                {
                    var details = string.IsNullOrWhiteSpace(output)
                        ? string.Empty
                        : $" Last sidecar output:{Environment.NewLine}{output}";
                    throw new InvalidOperationException(
                        $"Protocol 3 sidecar exited before diagnostics became ready (exit code {exitCode}).{details}",
                        ex);
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(400), ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            lastError is null
                ? "Protocol 3 sidecar did not report ready diagnostics in time."
                : $"Protocol 3 sidecar did not report ready diagnostics in time: {lastError.Message}",
            lastError);
    }

    private async Task<Protocol3SidecarSnapshot> FetchSnapshotAsync(
        Uri diagnosticsUrl,
        int rxStreams,
        CancellationToken ct)
    {
        ValidateDiagnosticsUrl(diagnosticsUrl);
        var http = _httpFactory.CreateClient(HttpClientName);
        using var response = await http.GetAsync(diagnosticsUrl, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Protocol 3 sidecar diagnostics returned HTTP {(int)response.StatusCode}.");

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var root = await JsonNode.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false) as JsonObject
            ?? throw new InvalidOperationException("Protocol 3 sidecar diagnostics root is not an object.");
        return ValidateDiagnosticsSnapshot(root, diagnosticsUrl, rxStreams);
    }

    internal async Task PushReceiverSettingsAsync(
        StateDto state,
        int rxStreams,
        int sampleRateHz,
        CancellationToken ct)
    {
        RememberReceiverState(state, rxStreams, sampleRateHz);
        Uri? diagnosticsUrl;
        lock (_sync)
        {
            diagnosticsUrl = _diagnosticsUrl;
        }
        if (diagnosticsUrl is null) return;

        var payload = BuildReceiverSettingsJson(
            state,
            rxStreams,
            sampleRateHz,
            invertRxTuningOffset: InvertRxTuningOffset());
        var url = Endpoint(diagnosticsUrl, "/api/control/receivers");
        var http = _httpFactory.CreateClient(HttpClientName);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(url, content, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return;

        var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (detail.Length > 512) detail = detail[..512];
        throw new InvalidOperationException(
            $"Protocol 3 sidecar receiver control returned HTTP {(int)response.StatusCode}: {detail}");
    }

    internal async Task PushTxControlAsync(
        bool mox,
        bool txEnable,
        bool tune,
        int driveLevel,
        CancellationToken ct)
    {
        Interlocked.Increment(ref _txControlPosts);
        var active = mox || txEnable || tune;
        if (active)
            MarkTxActivity();
        if (!active)
        {
            DisarmTxIqStreaming();
            StopTxControlKeepalive();
            ResetTxIqSendQueue();
        }

        var payload = new JsonObject
        {
            ["mox"] = mox,
            ["txEnable"] = txEnable,
            ["tune"] = tune,
            ["driveLevel"] = Math.Clamp(driveLevel, 0, 255),
            ["timeoutMs"] = DefaultControlTimeoutMs,
        }.ToJsonString();
        _lastTxControlPayload = payload;
        await PostControlAsync("/api/control/tx/control", payload, "TX control", ct)
            .ConfigureAwait(false);
        if (active)
        {
            Volatile.Write(ref _txIqStreamingArmed, 1);
            StartTxControlKeepalive(payload);
        }
    }

    /// <summary>
    /// The three Saturn Alex register images the P3 RF_PATH command carries
    /// (offsets 12/16/20) — composed via
    /// Protocol2Client.ComposeSaturnAlexRegisterImages so P2 and P3 share one
    /// band→relay source of truth. Without these words the radio's BPF/LPF/
    /// antenna relays never move off their boot defaults (the "no relay clicks
    /// on band change" bug).
    /// </summary>
    internal readonly record struct AlexRegisterImages(
        ushort TxFilterRegister,
        uint RxRegister,
        ushort TxAntennaRegister);

    internal Task PushRfPathAsync(bool paEnabled, AlexRegisterImages? alex, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["paEnabled"] = paEnabled,
            ["timeoutMs"] = DefaultControlTimeoutMs,
        };
        if (alex is { } a)
        {
            payload["alexTxFilterRegister"] = a.TxFilterRegister;
            payload["alexRxRegister"] = a.RxRegister;
            payload["alexTxAntennaRegister"] = a.TxAntennaRegister;
            // Alex 0 present; manual filter select on (Zeus is the filter
            // authority, exactly as on P2); T/R relay operation enabled.
            payload["alexEnabledBits"] = 1;
            payload["alexManualFilterSelect"] = true;
            payload["alexTrRelayEnabled"] = true;
        }
        return PostControlAsync("/api/control/rf-path", payload.ToJsonString(), "RF path", ct);
    }

    internal Task PushAudioControlAsync(AudioFrontEndPush push, CancellationToken ct)
    {
        var micControl = push.MicControlByte;
        var source = push.Source.ToString();
        var payload = new JsonObject
        {
            ["micControlByte"] = micControl,
            ["micPttEnabled"] = (micControl & 0x04) == 0,
            ["micRingEnabled"] = (micControl & 0x08) != 0,
            ["lineInGain"] = push.LineInGain,
            ["source"] = source,
            ["timeoutMs"] = DefaultControlTimeoutMs,
        }.ToJsonString();
        return PostControlAsync("/api/control/audio", payload, "audio control", ct);
    }

    internal async Task PrepareHostTxIqAsync(
        CancellationToken ct,
        int settleMilliseconds = DefaultControlTimeoutMs,
        int commandTimeoutMilliseconds = DefaultControlTimeoutMs)
    {
        DisarmTxIqStreaming();
        settleMilliseconds = Math.Clamp(settleMilliseconds, 100, 10000);
        commandTimeoutMilliseconds = Math.Clamp(commandTimeoutMilliseconds, 100, 10000);
        Uri? diagnosticsUrl;
        lock (_sync)
        {
            diagnosticsUrl = _diagnosticsUrl;
        }
        if (diagnosticsUrl is null) return;

        var url = Endpoint(
            diagnosticsUrl,
            "/api/control/tx/prepare-host-iq",
            $"timeoutMs={commandTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)}&settleMs={settleMilliseconds.ToString(CultureInfo.InvariantCulture)}");
        var http = _httpFactory.CreateClient(HttpClientName);
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(url, content, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (detail.Length > 512) detail = detail[..512];
        throw new InvalidOperationException(
            $"Protocol 3 sidecar host TX IQ prepare returned HTTP {(int)response.StatusCode}: {detail}");
    }

    private async Task PostControlAsync(
        string path,
        string payload,
        string label,
        CancellationToken ct)
    {
        Uri? diagnosticsUrl;
        lock (_sync)
        {
            diagnosticsUrl = _diagnosticsUrl;
        }
        if (diagnosticsUrl is null)
        {
            Interlocked.Increment(ref _controlPostMissingUrl);
            _lastControlPostError = $"{label}: diagnostics URL is not set.";
            return;
        }

        var url = Endpoint(diagnosticsUrl, path);
        _lastControlPostUrl = url.ToString();
        var http = _httpFactory.CreateClient(HttpClientName);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(url, content, ct).ConfigureAwait(false);
        _lastControlPostStatus = (int)response.StatusCode;
        var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        _lastControlPostBody = detail.Length > 512 ? detail[..512] : detail;
        if (response.IsSuccessStatusCode)
        {
            Interlocked.Increment(ref _controlPostSuccesses);
            _lastControlPostError = null;
            return;
        }

        if (detail.Length > 512) detail = detail[..512];
        _lastControlPostError = $"{label}: HTTP {(int)response.StatusCode}: {detail}";
        throw new InvalidOperationException(
            $"Protocol 3 sidecar {label} returned HTTP {(int)response.StatusCode}: {detail}");
    }

    internal ProcessStartInfo BuildStartInfo(
        string project,
        string n9dspLibrary,
        Uri listenUrl,
        IPAddress radioIp,
        int p3Port,
        int sampleRateHz,
        int rxStreams,
        StateDto initialState,
        int txIqIngestPort = 0)
    {
        var dotnet = Setting("DotnetPath", "ZEUS_PROTOCOL3_SIDECAR_DOTNET");
        if (string.IsNullOrWhiteSpace(dotnet)) dotnet = "dotnet";

        var psi = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(project) ?? AppContext.BaseDirectory,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(project);
        if (BoolSetting("NoBuild", "ZEUS_PROTOCOL3_SIDECAR_NO_BUILD"))
            psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("--hosted-diagnostics-server");
        psi.ArgumentList.Add("--allow-degraded-start");
        psi.ArgumentList.Add("--listen");
        psi.ArgumentList.Add(listenUrl.ToString());
        psi.ArgumentList.Add("--host");
        psi.ArgumentList.Add(radioIp.ToString());
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(p3Port.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--receive-ms");
        psi.ArgumentList.Add(IntSetting("ReceiveMs", "ZEUS_PROTOCOL3_RECEIVE_MS", DefaultReceiveMs).ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--poll-ms");
        psi.ArgumentList.Add(IntSetting("PollMs", "ZEUS_PROTOCOL3_DISPLAY_POLL_MS", fallback: 20).ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--diagnostics-publish-ms");
        psi.ArgumentList.Add(IntSetting("DiagnosticsPublishMs", "ZEUS_PROTOCOL3_DIAGNOSTICS_PUBLISH_MS", fallback: 50).ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--hosted-wait-ms");
        psi.ArgumentList.Add("7000");
        psi.ArgumentList.Add("--serve-ms");
        psi.ArgumentList.Add("0");
        if (txIqIngestPort > 0)
        {
            psi.ArgumentList.Add("--tx-iq-ingest-port");
            psi.ArgumentList.Add(txIqIngestPort.ToString(CultureInfo.InvariantCulture));
        }
        psi.ArgumentList.Add("--rx-count");
        psi.ArgumentList.Add(rxStreams.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--rx-rate");
        psi.ArgumentList.Add(sampleRateHz.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--analyzer-fft-length");
        psi.ArgumentList.Add(IntSetting("AnalyzerFftLength", "ZEUS_PROTOCOL3_ANALYZER_FFT_LENGTH", DefaultAnalyzerFftLength).ToString(CultureInfo.InvariantCulture));
        var analyzerProcessEvery = IntSetting("AnalyzerProcessEvery", "ZEUS_PROTOCOL3_ANALYZER_PROCESS_EVERY", 0);
        if (analyzerProcessEvery > 0)
        {
            psi.ArgumentList.Add("--analyzer-process-every");
            psi.ArgumentList.Add(analyzerProcessEvery.ToString(CultureInfo.InvariantCulture));
        }
        // P3 display calibration (dB offset applied to every analyzer bin in
        // n9dsp). Default -27 dB: measured 2026-07-04 — the raw dBFS bins sat
        // ~25-30 dB above the P2/WDSP render for the same antenna, pinning the
        // waterfall past the fixed color window's dbMax (blown-out white,
        // "missing" weak signals). Override via ZEUS_PROTOCOL3_ANALYZER_CALIBRATION_DB.
        var analyzerCalibrationDb = DoubleSetting(
            "AnalyzerCalibrationDb", "ZEUS_PROTOCOL3_ANALYZER_CALIBRATION_DB", -27.0);
        if (analyzerCalibrationDb != 0.0)
        {
            psi.ArgumentList.Add("--analyzer-calibration-db");
            psi.ArgumentList.Add(analyzerCalibrationDb.ToString("R", CultureInfo.InvariantCulture));
        }
        if (BoolSetting("RxIqConjugate", "ZEUS_PROTOCOL3_RX_IQ_CONJUGATE", fallback: false))
            psi.ArgumentList.Add("--rx-iq-conjugate");
        else
            psi.ArgumentList.Add("--rx-iq-normal");
        psi.ArgumentList.Add("--rx-settings-json");
        psi.ArgumentList.Add(BuildReceiverSettingsJson(
            initialState,
            rxStreams,
            sampleRateHz,
            invertRxTuningOffset: InvertRxTuningOffset()));
        psi.ArgumentList.Add("--n9dsp-library");
        psi.ArgumentList.Add(n9dspLibrary);
        psi.Environment["N9DSP_NATIVE_LIBRARY"] = n9dspLibrary;
        psi.Environment["P3_ZEUS_HOSTED_RADIO_SESSION"] = HostedSessionArmValue;
        return psi;
    }

    internal static string BuildReceiverSettingsJson(
        StateDto state,
        int rxStreams,
        int sampleRateHz,
        bool invertRxTuningOffset = false)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (rxStreams is < 1 or > WireContract.MaxReceivers)
            throw new ArgumentOutOfRangeException(nameof(rxStreams));
        if (sampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));

        var receivers = state.Receivers ?? Array.Empty<ReceiverDto>();

        // TX DUC (upconverter) NCO frequency — the RF frequency transmit lands
        // on. Mirrors the P2 DUC tuning (Protocol2Client.SetTxDucFrequency uses
        // CwOffset.EffectiveLoHz on the TX VFO). Without this the P3 transmit DUC
        // stays on a stale frequency (observed 21 kHz off the VFO). Carried on
        // every receiver entry; the sidecar emits it once via NCO_COMMAND DUC0.
        var (txEngineMode, _, _) = Protocol3EngineFilterFor(ReceiverFor(state, receivers, 0));
        var txDucFrequencyHz = CwOffset.EffectiveLoHz(txEngineMode, RadioService.TxFrequencyHz(state));

        var array = new JsonArray();
        for (var index = 0; index < rxStreams; index++)
        {
            var rx = ReceiverFor(state, receivers, index);
            var enabled = rx.Enabled && index < state.MaxReceivers;
            var (engineMode, passbandLowHz, passbandHighHz) = Protocol3EngineFilterFor(rx);
            var effectiveLoHz = CwOffset.EffectiveLoHz(engineMode, rx.VfoHz);
            var centerHz = state.RadioLoHz > 0 ? state.RadioLoHz : effectiveLoHz;
            var tuningOffsetHz = ClampInt64(effectiveLoHz - centerHz);
            if (invertRxTuningOffset) tuningOffsetHz = -tuningOffsetHz;
            var muted = rx.Muted ||
                (index == 0 && state.Rx1Muted) ||
                (index == 1 && state.Rx2Muted);
            var nr = state.Nr;

            array.Add(new JsonObject
            {
                ["receiverIndex"] = index,
                ["enabled"] = enabled,
                ["dspChannelIndex"] = index,
                ["ddcIndex"] = index,
                ["adcSource"] = rx.AdcSource,
                ["centerFrequencyHz"] = centerHz,
                ["tuningOffsetHz"] = tuningOffsetHz,
                ["txDucFrequencyHz"] = txDucFrequencyHz,
                ["mode"] = (int)engineMode,
                ["passbandLowHz"] = passbandLowHz,
                ["passbandHighHz"] = passbandHighHz,
                ["afGainDb"] = enabled && !muted ? rx.AfGainDb : -120.0,
                ["sampleRateHz"] = sampleRateHz,
                ["enableRxAgc"] = state.Agc?.Mode == AgcMode.Fixed ? 0 : 1,
                ["rxAgcProfile"] = RxAgcProfile(state.Agc?.Mode ?? AgcMode.Med),
                ["agcAttackMs"] = 2.0,
                ["agcReleaseMs"] = state.Agc?.DecayMs ?? 120,
                ["agcHangMs"] = state.Agc?.HangMs ?? 0,
                ["agcHangThresholdRms"] = HangThresholdRms(state.Agc?.HangThreshold),
                ["agcMaxGainDb"] = state.AgcTopDb,
                ["agcSlope"] = state.Agc?.Slope ?? 0,
                ["agcHoldThresholdRms"] = 0.0,
                ["enableRxNoiseBlanker"] = nr?.NbMode == NbMode.Nb1 ? 1 : 0,
                ["rxNoiseBlankerThresholdDb"] = nr?.NbThreshold ?? 20.0,
                ["rxNoiseBlankerTailUs"] = 80.0,
                ["rxNoiseBlankerRecoveryUs"] = 120.0,
                ["enableRxSpectralBlanker"] = EnableRxSpectralBlanker(nr),
                ["rxSpectralBlankerThresholdDb"] = nr?.NbThreshold ?? 20.0,
                ["rxSpectralBlankerTailMs"] = 1.0,
                ["rxSpectralBlankerRecoveryMs"] = 3.0,
                ["enableRxNotch"] = nr?.AnfEnabled == true ? 1 : 0,
                ["enableRxSquelch"] = state.Squelch?.Enabled == true ? 1 : 0,
                ["rxSquelchThresholdDb"] = SquelchThresholdDb(state.Squelch),
                ["rxSquelchHysteresisDb"] = 3.0,
                ["rxSquelchAttackMs"] = 5.0,
                ["rxSquelchReleaseMs"] = 50.0,
                ["enableRxAnr"] = EnableRxAnr(nr),
                ["rxAnrStrength"] = AnrStrength(nr),
                ["rxAnrAdaptationRate"] = 0.05,
                ["enableRxEmnr"] = EnableRxEmnr(nr),
                ["rxEmnrStrength"] = EmnrStrength(nr),
                ["rxEmnrNoiseFloor"] = EmnrNoiseFloor(nr),
                ["rxEmnrAttackMs"] = 4.0,
                ["rxEmnrReleaseMs"] = 120.0,
                ["enableRxMultiNotch"] = nr?.NbpNotchesEnabled == true ? 2 : 0,
            });
        }

        return array.ToJsonString();
    }

    private static (RxMode mode, int lowHz, int highHz) Protocol3EngineFilterFor(ReceiverDto rx)
    {
        var engineMode = RadioService.EffectiveEngineMode(rx.Mode, rx.VfoHz);
        if (rx.Mode != RxMode.FreeDv)
            return (engineMode, rx.FilterLowHz, rx.FilterHighHz);

        int loAbs = Math.Min(Math.Abs(rx.FilterLowHz), Math.Abs(rx.FilterHighHz));
        int hiAbs = Math.Max(Math.Abs(rx.FilterLowHz), Math.Abs(rx.FilterHighHz));
        var (lowHz, highHz) = RadioService.SignedFilterForMode(engineMode, loAbs, hiAbs);
        return (engineMode, lowHz, highHz);
    }

    private static ReceiverDto ReceiverFor(StateDto state, IReadOnlyList<ReceiverDto> receivers, int index)
    {
        if (index == 0)
        {
            return new ReceiverDto(
                0,
                Enabled: true,
                AdcSource: 0,
                VfoHz: state.VfoHz,
                Mode: state.Mode,
                FilterLowHz: state.FilterLowHz,
                FilterHighHz: state.FilterHighHz,
                FilterPresetName: state.FilterPresetName,
                AfGainDb: state.RxAfGainDb,
                SampleRateHz: state.SampleRate,
                Muted: state.Rx1Muted);
        }

        var existing = receivers.FirstOrDefault(receiver => receiver.Index == index);
        if (existing is not null)
        {
            return index == 1
                ? existing with { Enabled = state.Rx2Enabled, Muted = state.Rx2Muted, SampleRateHz = state.SampleRate }
                : existing;
        }

        return new ReceiverDto(
            index,
            Enabled: false,
            AdcSource: 0,
            VfoHz: state.VfoHz,
            Mode: state.Mode,
            FilterLowHz: state.FilterLowHz,
            FilterHighHz: state.FilterHighHz,
            FilterPresetName: state.FilterPresetName,
            AfGainDb: -120.0,
            SampleRateHz: state.SampleRate,
            Muted: true);
    }

    private static int ClampInt64(long value) =>
        value < int.MinValue ? int.MinValue :
        value > int.MaxValue ? int.MaxValue :
        (int)value;

    private static int RxAgcProfile(AgcMode mode) => mode switch
    {
        AgcMode.Fast => 1,
        AgcMode.Med => 2,
        AgcMode.Slow => 3,
        AgcMode.Long => 4,
        AgcMode.Fixed => 0,
        _ => 2,
    };

    private static double HangThresholdRms(int? percent) =>
        percent is null ? 0.05 : Math.Clamp(percent.Value, 0, 100) / 100.0;

    private static double SquelchThresholdDb(SquelchConfig? squelch) =>
        squelch is null ? -140.0 : -140.0 + Math.Clamp(squelch.Level, 0, 100);

    private static int EnableRxSpectralBlanker(NrConfig? nr) =>
        nr?.SnbEnabled == true || nr?.NbMode == NbMode.Nb2 ? 1 : 0;

    private static int EnableRxAnr(NrConfig? nr) =>
        nr?.NrMode is NrMode.Anr or NrMode.Rnnr ? 1 : 0;

    private static double AnrStrength(NrConfig? nr) =>
        nr?.NrMode == NrMode.Rnnr ? 0.55 : 0.85;

    private static int EnableRxEmnr(NrConfig? nr) =>
        nr?.NrMode is NrMode.Emnr or NrMode.Sbnr or NrMode.Rnnr ? 1 : 0;

    private static double EmnrStrength(NrConfig? nr) =>
        nr?.NrMode == NrMode.Rnnr ? 0.45 :
        nr?.NrMode == NrMode.Sbnr && nr.Nr4ReductionAmount is double reduction ? Math.Clamp(reduction / 20.0, 0.0, 1.0) :
        nr?.EmnrPost2Factor is double factor ? Math.Clamp(factor / 20.0, 0.0, 1.0) : 0.75;

    private static double EmnrNoiseFloor(NrConfig? nr) =>
        nr?.EmnrPost2Nlevel is double level ? Math.Clamp(level / 800.0, 0.0, 0.25) : 0.015;

    private Uri ListenUrl()
    {
        var configured = Setting("ListenUrl", "ZEUS_PROTOCOL3_SIDECAR_LISTEN");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri))
                throw new InvalidOperationException($"Invalid Protocol 3 sidecar listen URL: {configured}");
            ValidateLoopbackHttpBase(uri, "Protocol 3 sidecar listen URL");
            return uri;
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new Uri($"http://127.0.0.1:{port}/");
    }

    private int TxIqIngestPort()
    {
        var configured = ConfiguredTxIqIngestPort();
        if (configured > 0) return configured;

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }

    private int ConfiguredTxIqIngestPort()
    {
        var port = IntSetting("TxIqIngestPort", "ZEUS_PROTOCOL3_TX_IQ_INGEST_PORT", 0);
        return port is > 0 and <= 65535 ? port : 0;
    }

    private Uri? ExistingDiagnosticsUrl()
    {
        var raw = Setting("DiagnosticsUrl", "ZEUS_PROTOCOL3_SIDECAR_DIAGNOSTICS_URL");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"Invalid Protocol 3 sidecar diagnostics URL: {raw}");
        ValidateDiagnosticsUrl(uri);
        return uri;
    }

    private void StopPreviousOwnedSidecar()
    {
        var path = SidecarPidFilePath();
        try
        {
            if (!File.Exists(path)) return;
            var text = File.ReadAllText(path, Encoding.UTF8);
            if (!TryParseSidecarPidRecord(text, out var record))
            {
                TryDeleteFile(path);
                return;
            }

            using var process = Process.GetProcessById(record.ProcessId);
            if (!ProcessStartMatches(process, record.StartUnixMs))
            {
                _log.LogDebug(
                    "p3.sidecar.orphan.skip pid={Pid} diagnostics={Diagnostics} reason=start-time-mismatch",
                    record.ProcessId,
                    record.DiagnosticsUrl);
                return;
            }

            if (!process.HasExited)
            {
                _log.LogWarning(
                    "p3.sidecar.orphan.stop pid={Pid} diagnostics={Diagnostics}",
                    record.ProcessId,
                    record.DiagnosticsUrl);
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(5000))
                {
                    _log.LogWarning("p3.sidecar.orphan.stop.timeout pid={Pid}", record.ProcessId);
                    return;
                }
            }

            TryDeleteFile(path);
        }
        catch (ArgumentException)
        {
            TryDeleteFile(path);
        }
        catch (InvalidOperationException)
        {
            TryDeleteFile(path);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "p3.sidecar.orphan.stop.error");
        }
    }

    private void WriteOwnedSidecarPidFile(Process process, Uri diagnosticsUrl)
    {
        if (!TryGetProcessStartUnixMs(process, out var startUnixMs)) return;
        var path = SidecarPidFilePath();
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var text = string.Join(
                Environment.NewLine,
                $"pid={process.Id.ToString(CultureInfo.InvariantCulture)}",
                $"startUnixMs={startUnixMs.ToString(CultureInfo.InvariantCulture)}",
                $"diagnosticsUrl={diagnosticsUrl}",
                string.Empty);
            File.WriteAllText(path, text, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "p3.sidecar.pid.write.failed");
        }
    }

    private void DeleteOwnedSidecarPidFile(Process process)
    {
        var path = SidecarPidFilePath();
        try
        {
            if (!File.Exists(path)) return;
            if (!TryGetProcessStartUnixMs(process, out var startUnixMs)) return;
            var text = File.ReadAllText(path, Encoding.UTF8);
            if (!TryParseSidecarPidRecord(text, out var record)) return;
            if (record.ProcessId == process.Id && StartUnixMsMatches(startUnixMs, record.StartUnixMs))
                TryDeleteFile(path);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "p3.sidecar.pid.delete.failed");
        }
    }

    internal static bool TryParseSidecarPidRecord(string? text, out SidecarPidRecord record)
    {
        record = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        int? pid = null;
        long? startUnixMs = null;
        string? diagnosticsUrl = null;
        foreach (var rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;

            if (string.Equals(parts[0], "pid", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPid))
            {
                pid = parsedPid;
            }
            else if (string.Equals(parts[0], "startUnixMs", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedStartUnixMs))
            {
                startUnixMs = parsedStartUnixMs;
            }
            else if (string.Equals(parts[0], "diagnosticsUrl", StringComparison.OrdinalIgnoreCase))
            {
                diagnosticsUrl = parts[1];
            }
        }

        if (pid is not > 0 || startUnixMs is not > 0 || string.IsNullOrWhiteSpace(diagnosticsUrl))
            return false;
        if (!Uri.TryCreate(diagnosticsUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsLoopback ||
            !string.Equals(uri.AbsolutePath, "/api/diagnostics/v2", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        record = new SidecarPidRecord(pid.Value, startUnixMs.Value, uri.ToString());
        return true;
    }

    private static string SidecarPidFilePath() =>
        Path.Combine(LocalZeusDataDirectory(), SidecarPidFileName);

    private static string LocalZeusDataDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? AppContext.BaseDirectory
            : Path.Combine(localAppData, "Zeus");
    }

    private static bool ProcessStartMatches(Process process, long expectedStartUnixMs) =>
        TryGetProcessStartUnixMs(process, out var actualStartUnixMs) &&
        StartUnixMsMatches(actualStartUnixMs, expectedStartUnixMs);

    internal static bool StartUnixMsMatches(long actualStartUnixMs, long expectedStartUnixMs) =>
        actualStartUnixMs > 0 &&
        expectedStartUnixMs > 0 &&
        Math.Abs(actualStartUnixMs - expectedStartUnixMs) <= 1000;

    private static bool TryGetProcessStartUnixMs(Process process, out long startUnixMs)
    {
        startUnixMs = 0;
        try
        {
            startUnixMs = new DateTimeOffset(process.StartTime).ToUnixTimeMilliseconds();
            return startUnixMs > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch { }
    }

    private static Uri DiagnosticsUrlForListen(Uri listenUrl)
    {
        ValidateLoopbackHttpBase(listenUrl, "Protocol 3 sidecar listen URL");
        return new UriBuilder(listenUrl)
        {
            Path = "/api/diagnostics/v2",
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri;
    }

    private static Uri Endpoint(Uri diagnosticsUrl, string path, string? query = null)
    {
        ValidateDiagnosticsUrl(diagnosticsUrl);
        return new UriBuilder(diagnosticsUrl)
        {
            Path = path,
            Query = query ?? string.Empty,
            Fragment = string.Empty,
        }.Uri;
    }

    private static Uri DisplayFramesUrlForDiagnostics(Uri diagnosticsUrl) =>
        Endpoint(diagnosticsUrl, "/api/diagnostics/v2/p3-display-frames", "zoom=1&center=0");

    private static void ValidateDiagnosticsUrl(Uri uri)
    {
        if (!uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsLoopback ||
            !string.Equals(uri.AbsolutePath, "/api/diagnostics/v2", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Protocol 3 sidecar diagnostics URL must be loopback http://.../api/diagnostics/v2.");
        }
    }

    private static void ValidateLoopbackHttpBase(Uri uri, string label)
    {
        if (!uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsLoopback ||
            uri.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException($"{label} must be loopback HTTP.");
        }
    }

    private string? Setting(string key, string env)
    {
        var value = Environment.GetEnvironmentVariable(env);
        if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        value = _configuration[$"Zeus:Protocol3:Sidecar:{key}"];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private string? FullPathSetting(string key, string env)
    {
        var value = Setting(key, env);
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
    }

    private int IntSetting(string key, string env, int fallback)
    {
        var value = Setting(key, env);
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private double DoubleSetting(string key, string env, double fallback)
    {
        var value = Setting(key, env);
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private bool BoolSetting(string key, string env, bool fallback = false)
    {
        var value = Setting(key, env);
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private bool InvertRxTuningOffset() =>
        BoolSetting("InvertRxTuningOffset", "ZEUS_PROTOCOL3_INVERT_RX_TUNING_OFFSET", fallback: true);

    private void RememberConnection(
        IPEndPoint radioEndpoint,
        int p3Port,
        int sampleRateHz,
        int rxStreams,
        StateDto initialState)
    {
        lock (_sync)
        {
            _lastConnection = new Protocol3SidecarConnection(
                radioEndpoint,
                p3Port,
                sampleRateHz,
                rxStreams,
                initialState);
            _lastReceiverState = initialState;
            _lastReceiverRxStreams = rxStreams;
            _lastReceiverSampleRateHz = sampleRateHz;
        }
    }

    private void RememberReceiverState(StateDto state, int rxStreams, int sampleRateHz)
    {
        lock (_sync)
        {
            _lastReceiverState = state;
            _lastReceiverRxStreams = rxStreams;
            _lastReceiverSampleRateHz = sampleRateHz;
        }
    }

    private void Remember(
        Protocol3SidecarSnapshot? snapshot,
        bool ownsProcess,
        Process? process,
        Uri? diagnosticsUrl = null,
        ProcessOutputTail? output = null,
        int txIqIngestPort = 0)
    {
        lock (_sync)
        {
            _lastSnapshot = snapshot;
            _ownsProcess = ownsProcess;
            _process = process;
            _processOutput = output;
            _diagnosticsUrl = diagnosticsUrl ?? snapshot?.DiagnosticsUrl;
        }
        ConfigureTxIqIngest(txIqIngestPort);
    }

    private void ClearTxIqIngest()
    {
        DisarmTxIqStreaming();
        CancellationTokenSource? cts;
        Task? task;
        lock (_txIqSync)
        {
            _txIqIngestEndpoint = null;
            _txIqSendQueue?.Writer.TryComplete();
            _txIqSendQueue = null;
            cts = _txIqSendCts;
            task = _txIqSendTask;
            _txIqSendCts = null;
            _txIqSendTask = null;
            _txIqPacketBytes = 0;
            _txIqQueueDepth = 0;
        }
        CancelAndDisposeTxIqSender(cts, task);
    }

    private void ConfigureTxIqIngest(int txIqIngestPort)
    {
        DisarmTxIqStreaming();
        CancellationTokenSource? oldCts;
        Task? oldTask;
        lock (_txIqSync)
        {
            _txIqSendQueue?.Writer.TryComplete();
            oldCts = _txIqSendCts;
            oldTask = _txIqSendTask;
            _txIqSendQueue = null;
            _txIqSendCts = null;
            _txIqSendTask = null;
            _txIqPacketBytes = 0;
            _txIqQueueDepth = 0;

            if (txIqIngestPort <= 0)
            {
                _txIqIngestEndpoint = null;
                CancelAndDisposeTxIqSender(oldCts, oldTask);
                return;
            }

            var endpoint = new IPEndPoint(IPAddress.Loopback, txIqIngestPort);
            var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(TxIqSendQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
            var cts = new CancellationTokenSource();

            _txIqIngestEndpoint = endpoint;
            _txIqSendQueue = queue;
            _txIqSendCts = cts;
            _txIqSendTask = Task.Run(
                () => RunTxIqSenderAsync(queue.Reader, endpoint, cts.Token),
                CancellationToken.None);
        }
        CancelAndDisposeTxIqSender(oldCts, oldTask);
    }

    private void ResetTxIqSendQueue()
    {
        DisarmTxIqStreaming();
        int port;
        lock (_txIqSync)
        {
            port = _txIqIngestEndpoint?.Port ?? 0;
        }
        if (port > 0) ConfigureTxIqIngest(port);
    }

    private void DisarmTxIqStreaming()
    {
        Volatile.Write(ref _txIqStreamingArmed, 0);
    }

    private static void CancelAndDisposeTxIqSender(CancellationTokenSource? cts, Task? task)
    {
        if (cts is null) return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { return; }

        if (task is null || task.IsCompleted)
        {
            try { cts.Dispose(); }
            catch (ObjectDisposedException) { }
            return;
        }

        _ = task.ContinueWith(
            _ =>
            {
                try { cts.Dispose(); }
                catch (ObjectDisposedException) { }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void StartTxControlKeepalive(string payload)
    {
        CancellationTokenSource? oldCts = null;
        Task? oldTask = null;
        lock (_txControlKeepaliveSync)
        {
            _txControlKeepalivePayload = payload;
            if (_txControlKeepaliveTask is { IsCompleted: false })
                return;

            oldCts = _txControlKeepaliveCts;
            oldTask = _txControlKeepaliveTask;
            var cts = new CancellationTokenSource();
            _txControlKeepaliveCts = cts;
            _txControlKeepaliveTask = Task.Run(
                () => RunTxControlKeepaliveAsync(cts.Token),
                CancellationToken.None);
        }
        CancelAndDisposeTxControlKeepalive(oldCts, oldTask);
    }

    private void StopTxControlKeepalive()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_txControlKeepaliveSync)
        {
            _txControlKeepalivePayload = null;
            cts = _txControlKeepaliveCts;
            task = _txControlKeepaliveTask;
            _txControlKeepaliveCts = null;
            _txControlKeepaliveTask = null;
        }
        CancelAndDisposeTxControlKeepalive(cts, task);
    }

    private static void CancelAndDisposeTxControlKeepalive(CancellationTokenSource? cts, Task? task)
    {
        if (cts is null) return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { return; }

        if (task is null || task.IsCompleted)
        {
            try { cts.Dispose(); }
            catch (ObjectDisposedException) { }
            return;
        }

        _ = task.ContinueWith(
            _ =>
            {
                try { cts.Dispose(); }
                catch (ObjectDisposedException) { }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunTxControlKeepaliveAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TxControlKeepalivePeriodMs, ct).ConfigureAwait(false);
                string? payload;
                lock (_txControlKeepaliveSync)
                {
                    payload = _txControlKeepalivePayload;
                }
                if (string.IsNullOrWhiteSpace(payload))
                    continue;

                Interlocked.Increment(ref _txControlPosts);
                try
                {
                    await PostControlAsync("/api/control/tx/control", payload, "TX control keepalive", ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "p3.sidecar.tx-control.keepalive.failed");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void EnqueueTxIqPacketLocked(Channel<byte[]> queue)
    {
        var packet = new byte[TxIqPayloadBytes];
        Buffer.BlockCopy(_txIqPacket, 0, packet, 0, TxIqPayloadBytes);
        if (queue.Writer.TryWrite(packet))
        {
            _txIqQueueDepth++;
            Interlocked.Increment(ref _txIqPacketsQueued);
        }
        else
        {
            Interlocked.Increment(ref _txIqQueueDropped);
        }
        _txIqPacketBytes = 0;
    }

    private async Task RunTxIqSenderAsync(
        ChannelReader<byte[]> reader,
        IPEndPoint endpoint,
        CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);

        // Pace packets out at the radio's DUC DAC rate so the transmit stream is
        // steady instead of bursty. The mic path hands us ~16 packets at once
        // every 20 ms (one WDSP TX block); flushing that burst back-to-back
        // overfills the radio's ~4096-word DUC FIFO and then starves it for the
        // rest of the 20 ms — the source of the sequence-gap/drop "TX not steady"
        // stutter. Metering each 1.25 ms packet against a Stopwatch deadline
        // keeps the FIFO evenly filled (this is exactly how TxTuneDriver stays
        // rock-steady). timeBeginPeriod(1) is set process-wide, so Task.Delay
        // resolves near 1 ms — fine to spread a 16-packet burst across ~20 ms.
        //
        // Self-regulating and safe against under-delivery: we only *wait* when
        // ahead of the DAC clock; when a real backlog builds (production briefly
        // outruns the DAC, or Task.Delay overshoots) `now` passes the deadline
        // and packets go out immediately at full speed to catch up. The deadline
        // is clamped so it can never accumulate more than TxIqPacerMaxLeadPackets
        // of debt, bounding in-flight depth well under the channel capacity.
        long ticksPerPacket = Stopwatch.Frequency * TxIqFramesPerPacket / TxIqDacRateHz;
        long maxDebtTicks = ticksPerPacket * TxIqPacerMaxLeadPackets;
        long deadline = 0;

        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var packet))
                {
                    ct.ThrowIfCancellationRequested();

                    long now = Stopwatch.GetTimestamp();
                    if (deadline == 0) deadline = now;        // anchor the clock on the first packet
                    long aheadTicks = deadline - now;
                    if (aheadTicks > 0)
                    {
                        int waitMs = (int)(aheadTicks * 1000 / Stopwatch.Frequency);
                        if (waitMs >= 1)
                        {
                            try { await Task.Delay(waitMs, ct).ConfigureAwait(false); }
                            catch (OperationCanceledException) { return; }
                        }
                    }

                    SendTxIqPacket(udp, endpoint, packet);
                    deadline += ticksPerPacket;
                    // Don't let the schedule fall unboundedly behind real time on a
                    // sustained backlog — cap the debt so catch-up stays bounded.
                    long minDeadline = Stopwatch.GetTimestamp() - maxDebtTicks;
                    if (deadline < minDeadline) deadline = minDeadline;

                    lock (_txIqSync)
                    {
                        if (_txIqQueueDepth > 0) _txIqQueueDepth--;
                    }
                }

                // Queue drained (gap between transmissions / mic bursts): reset the
                // clock so the next packet re-anchors fresh rather than counting the
                // idle gap as "behind schedule" and dumping a catch-up burst.
                deadline = 0;
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
    }

    private void SendTxIqPacket(UdpClient udp, IPEndPoint endpoint, byte[] packet)
    {
        try
        {
            udp.Send(packet, packet.Length, endpoint);
            MarkTxActivity();
            Interlocked.Increment(ref _txIqPacketsSent);
        }
        catch (SocketException ex)
        {
            Interlocked.Increment(ref _txIqPacketsDropped);
            _log.LogDebug(ex, "p3.sidecar.tx_iq.send_failed endpoint={Endpoint}", endpoint);
        }
        catch (ObjectDisposedException ex)
        {
            Interlocked.Increment(ref _txIqPacketsDropped);
            _log.LogDebug(ex, "p3.sidecar.tx_iq.send_disposed endpoint={Endpoint}", endpoint);
        }
    }

    private void MarkTxActivity() =>
        Interlocked.Exchange(ref _lastTxActivityUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private static int FloatToS24(float sample)
    {
        if (!float.IsFinite(sample)) return 0;
        var scaled = (int)MathF.Round(Math.Clamp(sample, -1f, 1f) * TxIqS24Max);
        return Math.Clamp(scaled, TxIqS24Min, TxIqS24Max);
    }

    private static void WriteS24BigEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)((value >> 16) & 0xff);
        buffer[offset + 1] = (byte)((value >> 8) & 0xff);
        buffer[offset + 2] = (byte)(value & 0xff);
    }

    private bool HasOwnedProcessExited(out int? exitCode, out string output)
    {
        lock (_sync)
        {
            if (_ownsProcess && _process is { HasExited: true } process)
            {
                exitCode = process.ExitCode;
                output = _processOutput?.Snapshot() ?? string.Empty;
                return true;
            }
        }
        exitCode = null;
        output = string.Empty;
        return false;
    }

    private static JsonObject RequiredObject(JsonObject parent, string property) =>
        parent[property] as JsonObject ??
        throw new InvalidOperationException($"Protocol 3 sidecar diagnostics missing object '{property}'.");

    private static string RequiredString(JsonObject parent, string property) =>
        parent[property]?.GetValue<string>() ??
        throw new InvalidOperationException($"Protocol 3 sidecar diagnostics missing string '{property}'.");

    private static int RequiredInt(JsonObject parent, string property)
    {
        var node = parent[property] ??
            throw new InvalidOperationException($"Protocol 3 sidecar diagnostics missing number '{property}'.");
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var i)) return i;
            if (value.TryGetValue<long>(out var l) && l <= int.MaxValue && l >= int.MinValue) return (int)l;
            if (value.TryGetValue<uint>(out var u) && u <= int.MaxValue) return (int)u;
            if (value.TryGetValue<ulong>(out var ul) && ul <= int.MaxValue) return (int)ul;
        }
        throw new InvalidOperationException($"Protocol 3 sidecar diagnostics property '{property}' is not an int.");
    }

    private static bool CoversWidebandDisplay(
        JsonObject frame,
        long centerHz,
        double spanHz,
        int sourceWidth,
        double hzPerPixel)
    {
        if (!double.IsFinite(spanHz) || spanHz <= 0)
            spanHz = double.IsFinite(hzPerPixel) && hzPerPixel > 0
                ? hzPerPixel * sourceWidth
                : 0.0;
        if (spanHz < 59_000_000.0) return false;

        var rfStart = DoubleValue(frame, "rfStartFrequencyHz", centerHz - (spanHz / 2.0));
        var rfEnd = DoubleValue(frame, "rfEndFrequencyHz", centerHz + (spanHz / 2.0));
        if (rfStart > rfEnd) (rfStart, rfEnd) = (rfEnd, rfStart);

        return (rfStart <= 500_000.0 && rfEnd >= 59_500_000.0) ||
            (Math.Abs(centerHz - WidebandSpectrumAnalyzer.DisplayCenterHz) <= 1_000_000L &&
             spanHz >= 59_000_000.0);
    }

    private static float[] ResampleDbArray(JsonArray source, int sourceWidth, int targetWidth, bool reverse)
    {
        var output = new float[targetWidth];
        for (var dst = 0; dst < targetWidth; dst++)
        {
            var start = (int)((long)dst * sourceWidth / targetWidth);
            var end = (int)((long)(dst + 1) * sourceWidth / targetWidth);
            if (end <= start) end = start + 1;

            var value = float.NegativeInfinity;
            for (var src = start; src < end && src < sourceWidth; src++)
            {
                var sourceIndex = reverse ? sourceWidth - 1 - src : src;
                var sample = FloatValue(source[sourceIndex], fallback: -200f);
                if (float.IsFinite(sample) && sample > value) value = sample;
            }
            output[dst] = float.IsFinite(value) ? value : -200f;
        }
        return output;
    }

    private static DisplayBodyFlags ParseDisplayBodyFlags(string value)
    {
        var flags = DisplayBodyFlags.None;
        if (value.Contains("PanValid", StringComparison.OrdinalIgnoreCase))
            flags |= DisplayBodyFlags.PanValid;
        if (value.Contains("WfValid", StringComparison.OrdinalIgnoreCase))
            flags |= DisplayBodyFlags.WfValid;
        return flags == DisplayBodyFlags.None
            ? DisplayBodyFlags.PanValid | DisplayBodyFlags.WfValid
            : flags;
    }

    private static bool StringEquals(JsonObject parent, string property, string expected) =>
        string.Equals(StringValue(parent, property, string.Empty), expected, StringComparison.Ordinal);

    private static string StringValue(JsonObject parent, string property, string fallback)
    {
        try { return parent[property]?.GetValue<string>() ?? fallback; }
        catch { return fallback; }
    }

    private static bool BoolValue(JsonObject parent, string property, bool fallback)
    {
        try
        {
            if (parent[property] is JsonValue value && value.TryGetValue<bool>(out var b)) return b;
        }
        catch { }
        return fallback;
    }

    private static int IntValue(JsonObject parent, string property, int fallback)
    {
        try
        {
            if (parent[property] is JsonValue value)
            {
                if (value.TryGetValue<int>(out var i)) return i;
                if (value.TryGetValue<long>(out var l) && l is >= int.MinValue and <= int.MaxValue) return (int)l;
                if (value.TryGetValue<double>(out var d) && double.IsFinite(d)) return (int)Math.Round(d);
            }
        }
        catch { }
        return fallback;
    }

    private static long LongValue(JsonObject parent, string property, long fallback)
    {
        try
        {
            if (parent[property] is JsonValue value)
            {
                if (value.TryGetValue<long>(out var l)) return l;
                if (value.TryGetValue<int>(out var i)) return i;
                if (value.TryGetValue<double>(out var d) && double.IsFinite(d)) return (long)Math.Round(d);
            }
        }
        catch { }
        return fallback;
    }

    private static double DoubleValue(JsonObject parent, string property, double fallback)
    {
        try
        {
            if (parent[property] is JsonValue value)
            {
                if (value.TryGetValue<double>(out var d)) return d;
                if (value.TryGetValue<float>(out var f)) return f;
                if (value.TryGetValue<long>(out var l)) return l;
                if (value.TryGetValue<int>(out var i)) return i;
            }
        }
        catch { }
        return fallback;
    }

    private static float FloatValue(JsonNode? node, float fallback)
    {
        try
        {
            if (node is JsonValue value)
            {
                if (value.TryGetValue<float>(out var f)) return f;
                if (value.TryGetValue<double>(out var d) && double.IsFinite(d)) return (float)d;
                if (value.TryGetValue<int>(out var i)) return i;
                if (value.TryGetValue<long>(out var l)) return l;
            }
        }
        catch { }
        return fallback;
    }

    private sealed class ProcessOutputTail
    {
        private readonly object _sync = new();
        private readonly Queue<string> _lines = new();
        private readonly int _maxLines;

        private ProcessOutputTail(int maxLines)
        {
            _maxLines = maxLines;
        }

        public static ProcessOutputTail Attach(Process process)
        {
            var tail = new ProcessOutputTail(80);
            process.OutputDataReceived += (_, e) => tail.Append("stdout", e.Data);
            process.ErrorDataReceived += (_, e) => tail.Append("stderr", e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return tail;
        }

        public string Snapshot()
        {
            lock (_sync)
                return string.Join(Environment.NewLine, _lines);
        }

        private void Append(string stream, string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (_sync)
            {
                _lines.Enqueue($"{stream}: {line}");
                while (_lines.Count > _maxLines) _lines.Dequeue();
            }
        }
    }
}
