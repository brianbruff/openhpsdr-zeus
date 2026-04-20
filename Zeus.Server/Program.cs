using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Nereus.Contracts;
using Nereus.Dsp;
using Nereus.Protocol1;
using Nereus.Protocol1.Discovery;
using Nereus.Server;

var builder = WebApplication.CreateBuilder(args);

// Emit enums as strings on the wire ("USB", not 1) per doc 04 §3. The
// converter also accepts ordinal integers on read, so older clients that
// POST numeric mode values keep working.
builder.Services.Configure<JsonOptions>(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// 5000 is claimed by macOS ControlCenter (AirPlay receiver) by default,
// which replies 403 before Kestrel ever sees the request. 6060 is a
// stable free port across macOS/Linux/Windows for local dev and avoids
// conflicting with the user's Log4YM project (which also binds :5050).
// Bind on all interfaces so the SPA + API are reachable from other hosts
// on the LAN (doc 01 §Deployment: local single-user, same LAN as radio).
builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(6060));

// DspPipelineService owns engine selection directly: Synthetic while idle,
// WDSP while a Protocol1Client is attached. No IDspEngine DI registration —
// swapping requires lifecycle control the container can't express.
builder.Services.AddSingleton<IRadioDiscovery, RadioDiscoveryService>();
// TxIqRing is shared: TxAudioIngest writes modulated IQ into it, Protocol1Client
// (constructed inside RadioService) reads from it for the EP2 payload.
builder.Services.AddSingleton<Nereus.Protocol1.TxIqRing>();
builder.Services.AddSingleton<Nereus.Protocol1.ITxIqSource>(sp =>
    sp.GetRequiredService<Nereus.Protocol1.TxIqRing>());
builder.Services.AddSingleton<RadioService>();
builder.Services.AddSingleton<StreamingHub>();
builder.Services.AddSingleton<DspPipelineService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DspPipelineService>());
builder.Services.AddSingleton<TxService>();
builder.Services.AddSingleton<TxAudioIngest>();
// Resolve at startup so the MicPcmReceived subscription attaches before the
// first client connects (lazy resolution would leave early frames unhandled).
builder.Services.AddHostedService<TxAudioIngestStartup>();
builder.Services.AddSingleton<TxMetersService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TxMetersService>());
// TxTuneDriver pumps silent mic blocks through WDSP TXA while TUN is on so
// the post-gen tone actually reaches the ring (no mic uplink during TUN).
builder.Services.AddHostedService<TxTuneDriver>();

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.UseDefaultFiles();
app.UseStaticFiles();

var log = app.Services.GetRequiredService<ILogger<Program>>();

app.MapGet("/api/state", (RadioService r) => r.Snapshot());

// TX diagnostic — exposes the producer/consumer counts for the mic-to-IQ ring
// so we can verify end-to-end wiring without relying on logging. Safe to leave
// in as it's free to call and reveals nothing that isn't already in DI.
// TX wiring diagnostic: verifies producer (TxAudioIngest) and consumer
// (Protocol1Client via ITxIqSource) stats. Useful for "is the mic reaching
// TXA, and is the EP2 packer actually reading the ring" questions without
// hunting through logs. Free to call, exposes no secrets.
app.MapGet("/api/tx/diag", (Nereus.Protocol1.TxIqRing ring, Nereus.Protocol1.ITxIqSource src, TxAudioIngest ingest) =>
{
    return Results.Ok(new
    {
        iqSourceType = src.GetType().FullName,
        iqSourceIsRing = ReferenceEquals(src, ring),
        ring = new { ring.TotalWritten, ring.TotalRead, ring.Count, ring.Dropped, ring.Capacity, ring.RecentMag },
        ingest = new { ingest.TotalMicSamples, ingest.TotalTxBlocks, ingest.DroppedFrames },
    });
});

app.MapGet("/api/radios", async (IRadioDiscovery discovery, HttpContext ctx) =>
{
    var radios = await discovery.DiscoverAsync(TimeSpan.FromMilliseconds(1500), ctx.RequestAborted);
    return radios.Select(r => new RadioInfo(
        MacAddress: r.Mac.ToString(),
        IpAddress: r.Ip.ToString(),
        BoardId: r.Board.ToString(),
        FirmwareVersion: r.FirmwareString,
        Busy: r.Details.Busy,
        Details: BuildDetails(r))).ToArray();

    static IReadOnlyDictionary<string, string> BuildDetails(DiscoveredRadio r)
    {
        var d = new Dictionary<string, string>
        {
            ["rawBoardId"] = $"0x{r.Details.RawBoardId:X2}",
            ["gatewareBuild"] = r.Details.GatewareBuild.ToString(),
        };
        if (r.Details.FixedIpEnabled) d["fixedIpEnabled"] = "true";
        if (r.Details.FixedIpOverridesDhcp) d["fixedIpOverridesDhcp"] = "true";
        if (r.Details.MacAddressModified) d["macAddressModified"] = "true";
        if (r.Details.FixedIpAddress is { } ip) d["fixedIpAddress"] = ip.ToString();
        if (r.Details.HermesLite2MinorVersion is { } minor) d["hl2MinorVersion"] = minor.ToString();
        return d;
    }
});

app.MapPost("/api/connect", async (ConnectRequest req, RadioService r, HttpContext ctx) =>
{
    log.LogInformation(
        "api.connect endpoint={Ep} rate={Rate} preamp={Pre} atten={Atten}",
        req.Endpoint, req.SampleRate, req.PreampOn, req.Atten);

    if (!TryValidateSampleRate(req.SampleRate, out var rateErr))
        return Results.BadRequest(new { error = rateErr });
    if (req.Atten is int a && !TryValidateAttenDb(a, out var attenErr))
        return Results.BadRequest(new { error = attenErr });

    if (req.PreampOn is bool preamp) r.SetPreamp(preamp);
    if (req.Atten is int atten) r.SetAttenuator(new HpsdrAtten(atten));

    try
    {
        var state = await r.ConnectAsync(req.Endpoint, req.SampleRate, ctx.RequestAborted);
        return Results.Ok(state);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
});

app.MapPost("/api/disconnect", async (RadioService r, HttpContext ctx) =>
{
    log.LogInformation("api.disconnect");
    return await r.DisconnectAsync(ctx.RequestAborted);
});

app.MapPost("/api/vfo", (VfoSetRequest req, RadioService r) =>
{
    log.LogInformation("api.vfo hz={Hz}", req.Hz);
    return r.SetVfo(req.Hz);
});

app.MapPost("/api/mode", (ModeSetRequest req, RadioService r) =>
{
    log.LogInformation("api.mode mode={Mode}", req.Mode);
    return r.SetMode(req.Mode);
});

app.MapPost("/api/bandwidth", (BandwidthSetRequest req, RadioService r) =>
{
    log.LogInformation("api.bandwidth low={L} high={H}", req.Low, req.High);
    return r.SetFilter(req.Low, req.High);
});

app.MapPost("/api/sampleRate", (SampleRateSetRequest req, RadioService r) =>
{
    log.LogInformation("api.sampleRate rate={Rate}", req.Rate);
    if (!TryValidateSampleRate(req.Rate, out var err))
        return Results.BadRequest(new { error = err });
    return Results.Ok(r.SetSampleRate(MapHpsdrSampleRate(req.Rate)));
});

app.MapPost("/api/preamp", (PreampSetRequest req, RadioService r) =>
{
    log.LogInformation("api.preamp on={On}", req.On);
    return r.SetPreamp(req.On);
});

app.MapPost("/api/agcGain", (AgcGainSetRequest req, RadioService r) =>
{
    log.LogInformation("api.agcGain topDb={TopDb:F1}", req.TopDb);
    return r.SetAgcTop(req.TopDb);
});

app.MapPost("/api/attenuator", (AttenuatorSetRequest req, RadioService r) =>
{
    log.LogInformation("api.attenuator db={Db}", req.Db);
    if (!TryValidateAttenDb(req.Db, out var err))
        return Results.BadRequest(new { error = err });
    return Results.Ok(r.SetAttenuator(new HpsdrAtten(req.Db)));
});

app.MapPost("/api/auto-att", (AutoAttSetRequest req, RadioService r) =>
{
    log.LogInformation("api.auto-att enabled={Enabled}", req.Enabled);
    return r.SetAutoAtt(req.Enabled);
});

app.MapPost("/api/tx/mox", (MoxSetRequest req, TxService tx) =>
{
    log.LogInformation("api.tx.mox on={On}", req.On);
    if (!tx.TrySetMox(req.On, out var err)) return Results.Conflict(new { error = err });
    return Results.Ok(new { moxOn = tx.IsMoxOn });
});

// Mic-gain: +N dB (0..20), scales WDSP TXA panel-gain-1 per Thetis audio.cs:218-224
// (linear gain = 10^(db/20)). Clamp before applying so the UI can't push the
// chain outside the PRD's 0..+20 dB window.
app.MapPost("/api/mic-gain", (MicGainSetRequest req, DspPipelineService pipe) =>
{
    int db = Math.Clamp(req.Db, 0, 20);
    double gain = Math.Pow(10.0, db / 20.0);
    pipe.CurrentEngine?.SetTxPanelGain(gain);
    return Results.Ok(new { micGainDb = db });
});

// TUN: internal-tune carrier. Flips SetTXAPostGenRun on WDSP; server-side is
// where the PRD's drive clamp to min(drive, 25) lives, and where we gate
// mutual exclusion with MOX so the HL2 sees exactly one of them active.
app.MapPost("/api/tx/tun", (TunSetRequest req, TxService tx) =>
{
    if (!tx.TrySetTun(req.On, out var err))
        return Results.Conflict(new { error = err });
    return Results.Ok(new { tunOn = tx.IsTunOn });
});

app.MapPost("/api/tx/drive", (DriveSetRequest req, RadioService r) =>
{
    log.LogInformation("api.tx.drive percent={Pct}", req.Percent);
    if (req.Percent < 0 || req.Percent > 100)
        return Results.BadRequest(new { error = "percent must be 0..100" });
    r.SetDrive(req.Percent);
    return Results.Ok(new { drivePercent = req.Percent });
});

app.MapPost("/api/rx/nr", (NrSetRequest req, RadioService r) =>
{
    log.LogInformation(
        "api.rx.nr nr={Nr} anf={Anf} snb={Snb} notches={Notches} nb={Nb} thr={Thr:F2}",
        req.Nr.NrMode, req.Nr.AnfEnabled, req.Nr.SnbEnabled,
        req.Nr.NbpNotchesEnabled, req.Nr.NbMode, req.Nr.NbThreshold);
    if (!Enum.IsDefined(req.Nr.NrMode))
        return Results.BadRequest(new { error = $"unknown NrMode {req.Nr.NrMode}" });
    if (!Enum.IsDefined(req.Nr.NbMode))
        return Results.BadRequest(new { error = $"unknown NbMode {req.Nr.NbMode}" });
    return Results.Ok(r.SetNr(req.Nr));
});

app.MapPost("/api/rx/zoom", (ZoomSetRequest req, RadioService r) =>
{
    log.LogInformation("api.rx.zoom level={Level}", req.Level);
    if (req.Level < SyntheticDspEngine.MinZoomLevel || req.Level > SyntheticDspEngine.MaxZoomLevel)
        return Results.BadRequest(new { error = $"zoom level must be in [{SyntheticDspEngine.MinZoomLevel},{SyntheticDspEngine.MaxZoomLevel}]; got {req.Level}" });
    return Results.Ok(r.SetZoom(req.Level));
});

app.Map("/ws", async (HttpContext ctx, StreamingHub hub) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await hub.AttachClientAsync(ws, ctx.RequestAborted);
});

app.Run();

static bool TryValidateSampleRate(int rate, out string error)
{
    if (rate is 48_000 or 96_000 or 192_000 or 384_000) { error = ""; return true; }
    error = $"sampleRate must be one of {{48000, 96000, 192000, 384000}}, got {rate}.";
    return false;
}

static bool TryValidateAttenDb(int db, out string error)
{
    if (db >= HpsdrAtten.MinDb && db <= HpsdrAtten.MaxDb) { error = ""; return true; }
    error = $"atten must be in {HpsdrAtten.MinDb}..{HpsdrAtten.MaxDb} dB, got {db}.";
    return false;
}

static HpsdrSampleRate MapHpsdrSampleRate(int hz) => hz switch
{
    48_000 => HpsdrSampleRate.Rate48k,
    96_000 => HpsdrSampleRate.Rate96k,
    192_000 => HpsdrSampleRate.Rate192k,
    384_000 => HpsdrSampleRate.Rate384k,
    _ => throw new ArgumentOutOfRangeException(nameof(hz), hz, "validate before calling"),
};

public partial class Program;
