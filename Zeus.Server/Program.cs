// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Zeus.Contracts;
using Zeus.Dsp;
using Zeus.Dsp.Wdsp;
using Zeus.Protocol1;
using Zeus.Protocol1.Discovery;
using Zeus.Server;
using Zeus.Server.Tci;

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
// ZEUS_PORT overrides the default (used by the /run skill's portOffset).
var zeusPort = int.TryParse(Environment.GetEnvironmentVariable("ZEUS_PORT"), out var zp) ? zp : 6060;

// Resolve TCI bind settings from configuration before DI builds, because Kestrel's
// listeners have to be declared now. TCI shares Kestrel (rather than a separate
// HttpListener) so clone-and-run on Windows doesn't need an http.sys URL ACL — see #30.
var tciSection = builder.Configuration.GetSection("Tci");
var tciEnabled = tciSection.GetValue<bool>("Enabled");
var tciBindAddress = tciSection.GetValue<string?>("BindAddress") ?? "0.0.0.0";
var tciPort = tciSection.GetValue<int?>("Port") ?? 40001;

builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(zeusPort);
    if (tciEnabled)
    {
        if (tciBindAddress is "0.0.0.0" or "*" or "")
            k.ListenAnyIP(tciPort);
        else if (string.Equals(tciBindAddress, "localhost", StringComparison.OrdinalIgnoreCase))
            k.ListenLocalhost(tciPort);
        else if (IPAddress.TryParse(tciBindAddress, out var tciIp))
            k.Listen(tciIp, tciPort);
        else
            k.ListenAnyIP(tciPort);
    }
});

// DspPipelineService owns engine selection directly: Synthetic while idle,
// WDSP while a Protocol1Client is attached. No IDspEngine DI registration —
// swapping requires lifecycle control the container can't express.
builder.Services.AddSingleton<IRadioDiscovery, RadioDiscoveryService>();
builder.Services.AddSingleton<
    Zeus.Protocol2.Discovery.IRadioDiscovery,
    Zeus.Protocol2.Discovery.RadioDiscoveryService>();
// TxIqRing is shared: TxAudioIngest writes modulated IQ into it, Protocol1Client
// (constructed inside RadioService) reads from it for the EP2 payload.
builder.Services.AddSingleton<Zeus.Protocol1.TxIqRing>();
builder.Services.AddSingleton<Zeus.Protocol1.ITxIqSource>(sp =>
    sp.GetRequiredService<Zeus.Protocol1.TxIqRing>());
builder.Services.AddSingleton<RadioService>();
builder.Services.AddSingleton<StreamingHub>();
// WDSPwisdom bootstrap: run FFTW plan caching on a worker at app start so the
// first /api/connect isn't blocked for ~2 min while WDSP plans FFTs 64..262144.
// Clients are told to keep Connect disabled until phase=Ready.
builder.Services.AddSingleton<WdspWisdomInitializer>();
builder.Services.AddHostedService<WisdomBootstrapService>();
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
// PS auto-attenuate timer2code-equivalent: ramps the radio's TX step
// attenuator (Protocol2 only today) when calcc feedback level lands outside
// the 128..181 ideal window, so PS has a recovery path on first arm. Idle
// when PS is off or AutoAttenuate is off — no wire, no engine pokes.
builder.Services.AddHostedService<PsAutoAttenuateService>();

// QRZ.com XML client. HttpClient default timeout is 100 s — cap at 10 s so a
// hung login surfaces quickly in the UI.
builder.Services.AddHttpClient("Qrz", c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<CredentialStore>();
builder.Services.AddSingleton<BandMemoryStore>();
builder.Services.AddSingleton<LayoutStore>();
builder.Services.AddSingleton<DspSettingsStore>();
builder.Services.AddSingleton<PaSettingsStore>();
builder.Services.AddSingleton<PreferredRadioStore>();
builder.Services.AddSingleton<PsSettingsStore>();
builder.Services.AddSingleton<FilterPresetStore>();
builder.Services.AddSingleton<QrzService>();
builder.Services.AddSingleton<LogService>();

// rotctld (hamlib rotator daemon) client. BackgroundService with persistent
// TCP and reconnect-on-failure. Singleton so config/state survive across
// requests; hosted-service registration runs ExecuteAsync.
builder.Services.AddSingleton<RotctldService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RotctldService>());

// TCI (Transceiver Control Interface) — ExpertSDR3-compatible WebSocket server
// for remote control by loggers (Log4OM, N1MM+), digital-mode apps (JTDX, WSJT-X),
// and SDR display tools. Disabled by default; enable via appsettings.json Tci:Enabled=true.
builder.Services.Configure<TciOptions>(builder.Configuration.GetSection("Tci"));
builder.Services.AddSingleton<SpotManager>();
builder.Services.AddSingleton<TciServer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TciServer>());

var app = builder.Build();

// Initialize QrzService to restore stored credentials (silent login)
var qrzService = app.Services.GetRequiredService<QrzService>();
await qrzService.InitializeAsync(CancellationToken.None);

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

// Port-branch: any request arriving on the TCI listener (default :40001) is
// routed straight to TciServer.AcceptAsync. Keeps TCI clients connecting to
// ws://host:40001/ (root path, per ExpertSDR3 spec) from colliding with the
// API/SPA on the main port.
if (tciEnabled)
{
    app.UseWhen(
        ctx => ctx.Connection.LocalPort == tciPort,
        tciBranch => tciBranch.Run(ctx =>
            ctx.RequestServices.GetRequiredService<TciServer>().AcceptAsync(ctx)));
}

app.UseDefaultFiles();
app.UseStaticFiles();

var log = app.Services.GetRequiredService<ILogger<Program>>();

// Wire wisdom initializer → hub so every phase change is broadcast to all
// connected clients. Seed the hub's cached phase with whatever the
// initializer currently reports (Idle at first boot, Ready on restart once
// the file is cached).
{
    var wisdom = app.Services.GetRequiredService<WdspWisdomInitializer>();
    var hub = app.Services.GetRequiredService<StreamingHub>();
    hub.SetWisdomPhase(wisdom.Phase);
    wisdom.PhaseChanged += phase => hub.Broadcast(new WisdomStatusFrame(phase));
}

app.MapGet("/api/version", () =>
{
    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
    var attr = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute;
    var version = attr?.InformationalVersion ?? "unknown";
    return Results.Ok(new { version });
});

app.MapGet("/api/state", (RadioService r) => r.Snapshot());

// TX diagnostic — exposes the producer/consumer counts for the mic-to-IQ ring
// so we can verify end-to-end wiring without relying on logging. Safe to leave
// in as it's free to call and reveals nothing that isn't already in DI.
// TX wiring diagnostic: verifies producer (TxAudioIngest) and consumer
// (Protocol1Client via ITxIqSource) stats. Useful for "is the mic reaching
// TXA, and is the EP2 packer actually reading the ring" questions without
// hunting through logs. Free to call, exposes no secrets.
app.MapGet("/api/tx/diag", (Zeus.Protocol1.TxIqRing ring, Zeus.Protocol1.ITxIqSource src, TxAudioIngest ingest) =>
{
    return Results.Ok(new
    {
        iqSourceType = src.GetType().FullName,
        iqSourceIsRing = ReferenceEquals(src, ring),
        ring = new { ring.TotalWritten, ring.TotalRead, ring.Count, ring.Dropped, ring.Capacity, ring.RecentMag },
        ingest = new { ingest.TotalMicSamples, ingest.TotalTxBlocks, ingest.DroppedFrames },
    });
});

app.MapGet("/api/radios", async (
    IRadioDiscovery p1Discovery,
    Zeus.Protocol2.Discovery.IRadioDiscovery p2Discovery,
    HttpContext ctx) =>
{
    var timeout = TimeSpan.FromMilliseconds(1500);
    var p1Task = p1Discovery.DiscoverAsync(timeout, ctx.RequestAborted);
    var p2Task = p2Discovery.DiscoverAsync(timeout, ctx.RequestAborted);
    await Task.WhenAll(p1Task, p2Task);

    var p1Infos = p1Task.Result.Select(MapP1);
    var p2Infos = p2Task.Result.Select(MapP2);
    return p1Infos.Concat(p2Infos).ToArray();

    static RadioInfo MapP1(DiscoveredRadio r) => new(
        MacAddress: r.Mac.ToString(),
        IpAddress: r.Ip.ToString(),
        BoardId: r.Board.ToString(),
        FirmwareVersion: r.FirmwareString,
        Busy: r.Details.Busy,
        Details: BuildP1Details(r));

    static RadioInfo MapP2(Zeus.Protocol2.Discovery.DiscoveredRadio r) => new(
        MacAddress: r.Mac.ToString(),
        IpAddress: r.Ip.ToString(),
        BoardId: r.Board.ToString(),
        FirmwareVersion: r.FirmwareString,
        Busy: r.Details.Busy,
        Details: BuildP2Details(r));

    static IReadOnlyDictionary<string, string> BuildP1Details(DiscoveredRadio r)
    {
        var d = new Dictionary<string, string>
        {
            ["protocol"] = "P1",
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

    static IReadOnlyDictionary<string, string> BuildP2Details(Zeus.Protocol2.Discovery.DiscoveredRadio r)
    {
        var d = new Dictionary<string, string>
        {
            ["protocol"] = "P2",
            ["rawBoardId"] = $"0x{r.Details.RawBoardId:X2}",
            ["protocolSupported"] = r.Details.ProtocolSupported.ToString(),
            ["numReceivers"] = r.Details.NumReceivers.ToString(),
        };
        if (r.Details.BetaVersion != 0) d["betaVersion"] = r.Details.BetaVersion.ToString();
        return d;
    }
});

app.MapPost("/api/connect", async (ConnectRequest req, RadioService r, WdspWisdomInitializer wisdom, HttpContext ctx) =>
{
    log.LogInformation(
        "api.connect endpoint={Ep} rate={Rate} preamp={Pre} atten={Atten}",
        req.Endpoint, req.SampleRate, req.PreampOn, req.Atten);

    // WDSPwisdom must finish before OpenChannel, otherwise FFTW runs its slow
    // per-size planner on the pipeline thread and RX packets pile up until
    // the radio drops. The UI keeps Connect disabled during build; this is
    // the server-side guard for non-UI callers (curl, older clients).
    if (wisdom.Phase != WisdomPhase.Ready)
        return Results.Json(
            new { error = "DSP is preparing FFTW plans — try again in a moment." },
            statusCode: StatusCodes.Status503ServiceUnavailable);

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

app.MapPost("/api/connect/p2", async (ConnectRequest req, DspPipelineService dsp, WdspWisdomInitializer wisdom, HttpContext ctx) =>
{
    log.LogInformation("api.connect.p2 endpoint={Ep} rate={Rate}", req.Endpoint, req.SampleRate);

    if (wisdom.Phase != WisdomPhase.Ready)
        log.LogWarning("api.connect.p2 proceeding before wisdom ready; WDSP may fall back to synthetic");

    if (!TryParseIpEndpoint(req.Endpoint, out var ipEndpoint))
        return Results.BadRequest(new { error = $"Invalid endpoint '{req.Endpoint}'." });

    var rateKhz = req.SampleRate switch
    {
        48_000 => 48,
        96_000 => 96,
        192_000 => 192,
        384_000 => 384,
        _ => 48,
    };

    try
    {
        await dsp.ConnectP2Async(ipEndpoint, rateKhz, numAdc: 2, ctx.RequestAborted);
        return Results.Ok(new { protocol = "P2", endpoint = req.Endpoint, sampleRateKhz = rateKhz });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "api.connect.p2 failed");
        return Results.Problem(ex.Message, statusCode: 500);
    }
});

app.MapPost("/api/disconnect/p2", async (DspPipelineService dsp, HttpContext ctx) =>
{
    log.LogInformation("api.disconnect.p2");
    await dsp.DisconnectP2Async(ctx.RequestAborted);
    return Results.Ok(new { status = "disconnected" });
});

static bool TryParseIpEndpoint(string raw, out IPEndPoint ep)
{
    ep = null!;
    var idx = raw.LastIndexOf(':');
    string host = idx > 0 ? raw[..idx] : raw;
    int port = 1024;
    if (idx > 0 && int.TryParse(raw[(idx + 1)..], out var p)) port = p;
    if (!IPAddress.TryParse(host, out var ip)) return false;
    ep = new IPEndPoint(ip, port);
    return true;
}

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

// TX bandpass filter — signed Hz pair (LSB negative, DSB symmetric). Per-mode
// family memory is managed in RadioService, identical shape to the RX filter.
// Operator-editable via Settings → TX Filter panel.
app.MapPost("/api/tx-filter", (TxFilterSetRequest req, RadioService r) =>
{
    log.LogInformation("api.tx-filter low={L} high={H}", req.LowHz, req.HighHz);
    return r.SetTxFilter(req.LowHz, req.HighHz);
});

// Filter preset endpoints (PRD §5.2). These are the preferred filter surface;
// /api/bandwidth remains for backward compat. POST /api/filter also accepts
// an optional PresetName to track which chip is active.
app.MapPost("/api/filter", (FilterSetRequest req, RadioService r) =>
{
    log.LogInformation("api.filter low={L} high={H} preset={P}", req.LowHz, req.HighHz, req.PresetName);
    return r.SetFilter(req.LowHz, req.HighHz, req.PresetName);
});

app.MapGet("/api/filter/presets", (string? mode, RadioService r) =>
{
    if (mode is null || !Enum.TryParse<RxMode>(mode, ignoreCase: true, out var rxMode))
        return Results.BadRequest(new { error = $"Unknown mode '{mode}'. Expected one of: {string.Join(", ", Enum.GetNames<RxMode>())}" });
    return Results.Ok(r.GetFilterPresets(rxMode));
});

app.MapPost("/api/filter/presets", (FilterPresetWriteRequest req, RadioService r) =>
{
    log.LogInformation("api.filter.presets mode={M} slot={S} low={L} high={H}", req.Mode, req.SlotName, req.LowHz, req.HighHz);
    if (req.SlotName is not ("VAR1" or "VAR2"))
        return Results.Conflict(new { error = "Fixed presets cannot be edited. Only VAR1 and VAR2 slots are writable." });
    if (!Enum.IsDefined(req.Mode))
        return Results.BadRequest(new { error = $"Unknown mode '{req.Mode}'." });
    r.SetFilterPresetOverride(req.Mode, req.SlotName, req.LowHz, req.HighHz);
    return Results.Ok(r.GetFilterPresets(req.Mode));
});

// Advanced-ribbon pane visibility. Persisted via FilterPresetStore so the
// operator's close-the-ribbon choice survives a Zeus.Server restart.
app.MapPost("/api/filter/advanced-pane", (FilterAdvancedPaneRequest req, RadioService r) =>
{
    log.LogInformation("api.filter.advancedPane open={Open}", req.Open);
    return r.SetFilterAdvancedPaneOpen(req.Open);
});

// Get favorite filter slots for a mode.
app.MapGet("/api/filter/favorites", (string? mode, RadioService r) =>
{
    if (mode is null || !Enum.TryParse<RxMode>(mode, ignoreCase: true, out var rxMode))
        return Results.BadRequest(new { error = $"Unknown mode '{mode}'. Expected one of: {string.Join(", ", Enum.GetNames<RxMode>())}" });
    var slotNames = r.GetFavoriteFilterSlots(rxMode);
    return Results.Ok(new FilterFavoriteSlotsResponse(slotNames));
});

// Set favorite filter slots for a mode (up to 3).
app.MapPost("/api/filter/favorites", (FilterFavoriteSlotsRequest req, RadioService r) =>
{
    log.LogInformation("api.filter.favorites mode={M} slots={S}", req.Mode, string.Join(",", req.SlotNames));
    if (!Enum.IsDefined(req.Mode))
        return Results.BadRequest(new { error = $"Unknown mode '{req.Mode}'." });
    if (req.SlotNames.Length > 3)
        return Results.BadRequest(new { error = "Maximum 3 favorite slots allowed." });
    return Results.Ok(r.SetFavoriteFilterSlots(req.Mode, req.SlotNames));
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

app.MapPost("/api/rx/afGain", (RxAfGainSetRequest req, RadioService r) =>
{
    log.LogInformation("api.rx.afGain db={Db:F1}", req.Db);
    return r.SetRxAfGain(req.Db);
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

// Leveler max-gain ceiling in dB. Operator-safe band is 0..15 dB: 0 disables
// the headroom entirely (unity-cap Leveler) and 15 matches Thetis's stock
// ceiling (radio.cs:2979 tx_leveler_max_gain = 15.0). Anything outside is a
// 400 so a misbehaving client can't hand WDSP a value that'd saturate on
// the first voiced sample. The server is stateless for this setting —
// frontend re-POSTs on WS reconnect to re-sync after a server restart.
app.MapPost("/api/tx/leveler-max-gain", (LevelerMaxGainSetRequest req, DspPipelineService pipe) =>
{
    if (req.Gain < 0.0 || req.Gain > 15.0 || double.IsNaN(req.Gain))
        return Results.BadRequest(new { error = "gain must be 0..15 dB" });
    log.LogInformation("api.tx.levelerMaxGain dB={Db:F1}", req.Gain);
    pipe.CurrentEngine?.SetTxLevelerMaxGain(req.Gain);
    return Results.Ok(new { levelerMaxGainDb = req.Gain });
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

// TUN drive %. Symmetric with /api/tx/drive; the same PA-gain math applies,
// so equal slider positions emit equal watts. Backend selects between the
// two sources based on whether TUN is keyed (TxService.TrySetTun →
// RadioService.NotifyTunActive).
app.MapPost("/api/tx/tune-drive", (TuneDriveSetRequest req, RadioService r) =>
{
    log.LogInformation("api.tx.tune-drive percent={Pct}", req.Percent);
    if (req.Percent < 0 || req.Percent > 100)
        return Results.BadRequest(new { error = "percent must be 0..100" });
    r.SetTuneDrive(req.Percent);
    return Results.Ok(new { tunePercent = req.Percent });
});

// Two-tone test generator (TXA PostGen mode=1). Protocol-agnostic — works
// on both P1 and P2 because it only touches WDSP TXA, not the wire format.
app.MapPost("/api/tx/twotone", (TwoToneSetRequest req, RadioService r, TxService tx) =>
{
    log.LogInformation(
        "api.tx.twotone enabled={On} f1={F1} f2={F2} mag={Mag}",
        req.Enabled, req.Freq1, req.Freq2, req.Mag);
    if (req.Mag is double m && (m < 0.0 || m > 1.0 || double.IsNaN(m)))
        return Results.BadRequest(new { error = "mag must be 0..1" });
    if (req.Freq1 is double f1 && (f1 < 50.0 || f1 > 5000.0 || double.IsNaN(f1)))
        return Results.BadRequest(new { error = "freq1 must be 50..5000 Hz" });
    if (req.Freq2 is double f2 && (f2 < 50.0 || f2 > 5000.0 || double.IsNaN(f2)))
        return Results.BadRequest(new { error = "freq2 must be 50..5000 Hz" });
    // TrySetTwoTone owns both the engine state (RadioService.SetTwoTone) and
    // the MOX side-effect — Thetis parity, setup.cs:11162-11165. Returns the
    // post-mutate snapshot via Snapshot(); on a connect-interlock failure
    // the request is rejected with 400.
    if (!tx.TrySetTwoTone(req, out var err))
        return Results.BadRequest(new { error = err });
    return Results.Ok(r.Snapshot());
});

// PureSignal master arm + cal-mode. P1 is gated off in the frontend in v1
// because the Protocol1Client wire-format work for PS isn't done yet, but
// the server endpoint stays open — RadioService.SetPs sets the StateDto bit
// and the engine receives SetPsEnabled either way; only the radio-side
// feedback path is P2-only. See hermes.md / TODO(ps-p1).
app.MapPost("/api/tx/ps", (PsControlSetRequest req, RadioService r) =>
{
    log.LogInformation(
        "api.tx.ps enabled={On} auto={Auto} single={Single}",
        req.Enabled, req.Auto, req.Single);
    return Results.Ok(r.SetPs(req));
});

app.MapPost("/api/tx/ps/advanced", (PsAdvancedSetRequest req, RadioService r) =>
{
    if (req.HwPeak is double p && (p <= 0.0 || p > 2.0 || double.IsNaN(p)))
        return Results.BadRequest(new { error = "hwPeak must be in (0, 2]" });
    if (req.MoxDelaySec is double mox && (mox < 0.0 || mox > 10.0 || double.IsNaN(mox)))
        return Results.BadRequest(new { error = "moxDelaySec must be 0..10" });
    if (req.LoopDelaySec is double loop && (loop < 0.0 || loop > 100.0 || double.IsNaN(loop)))
        return Results.BadRequest(new { error = "loopDelaySec must be 0..100" });
    if (req.AmpDelayNs is double amp && (amp < 0.0 || amp > 25e6 || double.IsNaN(amp)))
        return Results.BadRequest(new { error = "ampDelayNs must be 0..25e6" });
    log.LogInformation("api.tx.ps.advanced");
    return Results.Ok(r.SetPsAdvanced(req));
});

// PS feedback antenna selector. Internal coupler vs External (Bypass).
// On G2/MkII this flips ALEX_RX_ANTENNA_BYPASS in alex0 during xmit + PS
// armed. WDSP cal/iqc are unaffected — same DDC0/DDC1 paired feed either
// way; only the radio routes a different physical signal into DDC0.
app.MapPost("/api/tx/ps/feedback-source",
    (PsFeedbackSourceSetRequest req, RadioService r) =>
{
    log.LogInformation("api.tx.ps.feedbackSource source={Source}", req.Source);
    return Results.Ok(r.SetPsFeedbackSource(req));
});

app.MapPost("/api/tx/ps/reset", (DspPipelineService pipe) =>
{
    log.LogInformation("api.tx.ps.reset");
    pipe.CurrentEngine?.ResetPs();
    return Results.Ok(new { reset = true });
});

app.MapPost("/api/tx/ps/save", (PsSaveRequest req, DspPipelineService pipe) =>
{
    if (string.IsNullOrWhiteSpace(req.Filename))
        return Results.BadRequest(new { error = "filename required" });
    log.LogInformation("api.tx.ps.save filename={Filename}", req.Filename);
    pipe.CurrentEngine?.SavePsCorrection(req.Filename);
    return Results.Ok(new { saved = req.Filename });
});

app.MapPost("/api/tx/ps/restore", (PsRestoreRequest req, DspPipelineService pipe) =>
{
    if (string.IsNullOrWhiteSpace(req.Filename))
        return Results.BadRequest(new { error = "filename required" });
    log.LogInformation("api.tx.ps.restore filename={Filename}", req.Filename);
    pipe.CurrentEngine?.RestorePsCorrection(req.Filename);
    return Results.Ok(new { restored = req.Filename });
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

// Per-popover PATCH endpoints for the right-click NR settings panels (issue
// #79). Each merges nullable fields onto the persisted NrConfig so the
// operator can edit one knob without resending the whole NR block. Skipping
// fields (or sending null) is a no-op for that field.
app.MapPost("/api/rx/nr2/post2", (Nr2Post2ConfigSetRequest req, RadioService r) =>
{
    log.LogInformation(
        "api.rx.nr2.post2 run={Run} factor={Factor} nlevel={Nlevel} rate={Rate} taper={Taper}",
        req.Post2Run, req.Post2Factor, req.Post2Nlevel, req.Post2Rate, req.Post2Taper);
    return Results.Ok(r.SetNr2Post2(req));
});

app.MapPost("/api/rx/nr4", (Nr4ConfigSetRequest req, RadioService r) =>
{
    log.LogInformation(
        "api.rx.nr4 reduction={Red} smoothing={Smo} whitening={Whi} noiseRescale={Nr} postThr={Pft} scaling={Sc} pos={Pos}",
        req.ReductionAmount, req.SmoothingFactor, req.WhiteningFactor,
        req.NoiseRescale, req.PostFilterThreshold, req.NoiseScalingType, req.Position);
    return Results.Ok(r.SetNr4(req));
});

app.MapPost("/api/rx/zoom", (ZoomSetRequest req, RadioService r) =>
{
    log.LogInformation("api.rx.zoom level={Level}", req.Level);
    if (req.Level < SyntheticDspEngine.MinZoomLevel || req.Level > SyntheticDspEngine.MaxZoomLevel)
        return Results.BadRequest(new { error = $"zoom level must be in [{SyntheticDspEngine.MinZoomLevel},{SyntheticDspEngine.MaxZoomLevel}]; got {req.Level}" });
    return Results.Ok(r.SetZoom(req.Level));
});

// Band memory: last-used (hz, mode) per HF band. GET returns the full map so
// the BandButtons UI can restore on load with one round-trip. PUT upserts one
// entry — the web debounces writes so tuning doesn't hammer LiteDB.
app.MapGet("/api/bands/memory", (BandMemoryStore store) => Results.Ok(store.GetAll()));

app.MapPut("/api/bands/memory/{band}", (string band, BandMemorySetRequest req, BandMemoryStore store) =>
{
    if (string.IsNullOrWhiteSpace(band))
        return Results.BadRequest(new { error = "band name required" });
    if (req.Hz <= 0)
        return Results.BadRequest(new { error = "hz must be positive" });
    store.Upsert(band, req.Hz, req.Mode);
    return Results.Ok(new BandMemoryDto(band, req.Hz, req.Mode));
});

// PA settings — per-band gain/OC masks + globals. Single PUT replaces the
// whole snapshot because the UI edits rows as a table; incremental PATCHing
// would deadlock with the RadioService recompute subscription fired on Save.
// The GET uses the effective board's defaults to fill missing rows so the
// panel opens with model-appropriate seeds on first load. Optional
// ?board= override lets the radio-selector preview defaults for a board
// other than the effective one without persisting the preference — the
// operator's saved per-band calibration still wins over the preview.
app.MapGet("/api/pa-settings", (string? board, PaSettingsStore store, RadioService radio) =>
{
    var preview = ParseBoardKind(board);
    var effective = preview ?? radio.EffectiveBoardKind;
    return Results.Ok(store.GetAll(effective));
});

// Pure board defaults — "Reset to defaults" button in the PA panel. Skips
// the pa_bands collection entirely and returns piHPSDR/Thetis seed values
// for the requested board (or the effective board if none specified).
app.MapGet("/api/pa-settings/defaults", (string? board, PaSettingsStore store, RadioService radio) =>
{
    var preview = ParseBoardKind(board);
    var target = preview ?? radio.EffectiveBoardKind;
    return Results.Ok(store.GetDefaults(target));
});

app.MapPut("/api/pa-settings", (PaSettingsSetRequest req, PaSettingsStore store, RadioService radio) =>
{
    if (req.Global is null || req.Bands is null)
        return Results.BadRequest(new { error = "global and bands required" });
    if (req.Global.PaMaxPowerWatts < 0)
        return Results.BadRequest(new { error = "paMaxPowerWatts must be >= 0" });
    store.Save(new PaSettingsDto(req.Global, req.Bands));
    return Results.Ok(store.GetAll(radio.EffectiveBoardKind));
});

// Radio selection — operator preference seeding, with discovery as the
// tiebreaker. Preferred=="Auto" removes the override (stored as absence,
// not a sentinel enum value). Effective = Connected when connected,
// Preferred when not, Unknown otherwise.
app.MapGet("/api/radio/selection", (PreferredRadioStore prefs, RadioService radio) =>
{
    var preferred = prefs.Get();
    return Results.Ok(new RadioSelectionDto(
        Preferred: preferred?.ToString() ?? "Auto",
        Connected: radio.ConnectedBoardKind.ToString(),
        Effective: radio.EffectiveBoardKind.ToString()));
});

app.MapPut("/api/radio/selection", (RadioSelectionSetRequest req, PreferredRadioStore prefs, RadioService radio) =>
{
    if (req is null || string.IsNullOrWhiteSpace(req.Preferred))
        return Results.BadRequest(new { error = "preferred required" });

    HpsdrBoardKind? chosen;
    if (string.Equals(req.Preferred, "Auto", StringComparison.OrdinalIgnoreCase))
    {
        chosen = null;
    }
    else if (Enum.TryParse<HpsdrBoardKind>(req.Preferred, ignoreCase: true, out var kind)
             && kind != HpsdrBoardKind.Unknown)
    {
        chosen = kind;
    }
    else
    {
        return Results.BadRequest(new { error = $"unknown board '{req.Preferred}'" });
    }

    prefs.Set(chosen);
    return Results.Ok(new RadioSelectionDto(
        Preferred: chosen?.ToString() ?? "Auto",
        Connected: radio.ConnectedBoardKind.ToString(),
        Effective: radio.EffectiveBoardKind.ToString()));
});

static HpsdrBoardKind? ParseBoardKind(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return null;
    if (string.Equals(raw, "Auto", StringComparison.OrdinalIgnoreCase)) return null;
    return Enum.TryParse<HpsdrBoardKind>(raw, ignoreCase: true, out var kind)
        ? kind
        : null;
}

// UI layout: flexlayout-react panel arrangement, persisted per operator profile.
// GET returns 404 when no layout has been saved yet (frontend falls back to
// DEFAULT_LAYOUT). PUT replaces; DELETE resets to default on next load.
app.MapGet("/api/ui/layout", (LayoutStore store) =>
{
    var layout = store.Get();
    return layout is null ? Results.NotFound() : Results.Ok(layout);
});

app.MapPut("/api/ui/layout", (UiLayoutSetRequest req, LayoutStore store) =>
{
    if (string.IsNullOrWhiteSpace(req.LayoutJson))
        return Results.BadRequest(new { error = "layoutJson required" });
    store.Upsert(req.LayoutJson);
    return Results.Ok(store.Get());
});

app.MapDelete("/api/ui/layout", (LayoutStore store) =>
{
    store.Delete();
    return Results.NoContent();
});

// Beacon endpoint: navigator.sendBeacon posts a Blob with Content-Type
// application/json; minimal response so the browser's 204-check passes.
app.MapPost("/api/ui/layout-beacon", async (LayoutStore store, HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync(ctx.RequestAborted);
    try
    {
        var req = System.Text.Json.JsonSerializer.Deserialize<UiLayoutSetRequest>(
            body, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (req?.LayoutJson is { } json && !string.IsNullOrWhiteSpace(json))
            store.Upsert(json);
    }
    catch { /* sendBeacon is fire-and-forget; swallow parse errors */ }
    return Results.Ok();
});

app.MapGet("/api/qrz/status", (QrzService qrz) => qrz.GetStatus());

app.MapPost("/api/qrz/login", async (QrzLoginRequest req, QrzService qrz, HttpContext ctx) =>
{
    if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { error = "username and password required" });
    log.LogInformation("api.qrz.login user={User}", req.Username);
    try
    {
        var status = await qrz.LoginAsync(req.Username, req.Password, ctx.RequestAborted);
        if (!status.Connected && status.Error != null)
            return Results.Json(status, statusCode: StatusCodes.Status401Unauthorized);
        return Results.Ok(status);
    }
    catch (HttpRequestException ex)
    {
        return Results.Json(new { error = $"QRZ unreachable: {ex.Message}" }, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/qrz/lookup", async (QrzLookupRequest req, QrzService qrz, HttpContext ctx) =>
{
    if (string.IsNullOrWhiteSpace(req.Callsign))
        return Results.BadRequest(new { error = "callsign required" });
    try
    {
        var station = await qrz.LookupAsync(req.Callsign.Trim().ToUpperInvariant(), ctx.RequestAborted);
        if (station == null) return Results.NotFound(new { error = $"no QRZ record for {req.Callsign}" });
        return Results.Ok(station);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status401Unauthorized);
    }
    catch (QrzSubscriptionRequiredException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status402PaymentRequired);
    }
});

app.MapPost("/api/qrz/logout", async (QrzService qrz, HttpContext ctx) =>
{
    await qrz.LogoutAsync(ctx.RequestAborted);
    return Results.Ok(qrz.GetStatus());
});

app.MapPost("/api/qrz/apikey", async (QrzSetApiKeyRequest req, QrzService qrz, HttpContext ctx) =>
{
    await qrz.SetApiKeyAsync(req.ApiKey, ctx.RequestAborted);
    return Results.Ok(qrz.GetStatus());
});

app.MapGet("/api/log/entries", async (LogService logService, HttpContext ctx, int skip = 0, int take = 100) =>
{
    var response = await logService.GetLogEntriesAsync(skip, take, ctx.RequestAborted);
    return Results.Ok(response);
});

app.MapPost("/api/log/entry", async (CreateLogEntryRequest req, LogService logService, HttpContext ctx) =>
{
    if (string.IsNullOrWhiteSpace(req.Callsign))
        return Results.BadRequest(new { error = "callsign required" });
    var entry = await logService.CreateLogEntryAsync(req, ctx.RequestAborted);
    return Results.Ok(entry);
});

app.MapGet("/api/log/export/adif", async (LogService logService, HttpContext ctx) =>
{
    var adif = await logService.ExportToAdifAsync(null, ctx.RequestAborted);
    var fileName = $"zeus-log-{DateTime.UtcNow:yyyyMMdd-HHmmss}.adi";
    return Results.File(
        System.Text.Encoding.UTF8.GetBytes(adif),
        "text/plain",
        fileName);
});

app.MapPost("/api/log/publish/qrz", async (QrzPublishRequest req, QrzService qrz, LogService logService, HttpContext ctx) =>
{
    if (req.LogEntryIds == null || !req.LogEntryIds.Any())
        return Results.BadRequest(new { error = "no log entry IDs provided" });

    var entries = await logService.GetLogEntriesByIdsAsync(req.LogEntryIds, ctx.RequestAborted);
    var results = new List<QrzPublishResult>();

    foreach (var entry in entries)
    {
        var result = await qrz.PublishLogEntryAsync(entry, ctx.RequestAborted);
        results.Add(result);

        // Update log entry with QRZ log ID if successful
        if (result.Success && !string.IsNullOrEmpty(result.QrzLogId))
        {
            await logService.UpdateQrzUploadStatusAsync(entry.Id, result.QrzLogId, ctx.RequestAborted);
        }
    }

    var successCount = results.Count(r => r.Success);
    var failedCount = results.Count - successCount;

    return Results.Ok(new QrzPublishResponse(
        TotalCount: results.Count,
        SuccessCount: successCount,
        FailedCount: failedCount,
        Results: results));
});


app.MapGet("/api/rotator/status", (RotctldService rot) => rot.GetStatus());

app.MapPost("/api/rotator/config", async (RotctldConfig req, RotctldService rot, HttpContext ctx) =>
{
    log.LogInformation("api.rotator.config enabled={En} host={Host} port={Port}", req.Enabled, req.Host, req.Port);
    var status = await rot.SetConfigAsync(req, ctx.RequestAborted);
    return Results.Ok(status);
});

app.MapPost("/api/rotator/set", async (RotctldSetAzRequest req, RotctldService rot, HttpContext ctx) =>
{
    if (!double.IsFinite(req.Azimuth)) return Results.BadRequest(new { error = "azimuth must be finite" });
    var status = await rot.SetAzAsync(req.Azimuth, ctx.RequestAborted);
    if (!status.Connected) return Results.Json(status, statusCode: StatusCodes.Status503ServiceUnavailable);
    return Results.Ok(status);
});

app.MapPost("/api/rotator/stop", async (RotctldService rot, HttpContext ctx) =>
{
    var status = await rot.StopRotatorAsync(ctx.RequestAborted);
    return Results.Ok(status);
});

app.MapPost("/api/rotator/test", async (RotctldTestRequest req, RotctldService rot, HttpContext ctx) =>
{
    if (string.IsNullOrWhiteSpace(req.Host) || req.Port is <= 0 or >= 65536)
        return Results.BadRequest(new { error = "host and port required" });
    var result = await rot.TestAsync(req.Host.Trim(), req.Port, ctx.RequestAborted);
    return Results.Ok(result);
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
