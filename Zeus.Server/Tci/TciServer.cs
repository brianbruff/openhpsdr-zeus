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
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.Extensions.Options;
using Zeus.Contracts;

namespace Zeus.Server.Tci;

/// <summary>
/// TCI (Transceiver Control Interface) server. Accepts ExpertSDR3-compatible
/// WebSocket clients on a dedicated Kestrel listener (default :40001) wired
/// in Program.cs via a port-branched middleware. Spoken by loggers (Log4OM,
/// N1MM+), digital-mode apps (JTDX, WSJT-X), and SDR display tools. Implements
/// TCI v1.8 per the ExpertSDR3 spec.
/// </summary>
// Hosted lifetime here only wires/unwires radio-event subscriptions and closes
// live sessions on shutdown — the HTTP accept loop lives in Kestrel, not here.
// (HttpListener on Windows needs a per-user urlacl for wildcard binds; Kestrel
// binds sockets directly, so clone-and-run works without elevation. See #30.)
public sealed class TciServer : IHostedService, IDisposable
{
    private readonly ILogger<TciServer> _log;
    private readonly TciOptions _options;
    private readonly RadioService _radio;
    private readonly TxService _tx;
    private readonly DspPipelineService _pipeline;
    private readonly SpotManager _spots;
    private readonly ILoggerFactory _loggerFactory;

    private readonly ConcurrentDictionary<Guid, TciSession> _clients = new();
    private bool _subscribed;

    public TciServer(
        IOptions<TciOptions> options,
        RadioService radio,
        TxService tx,
        DspPipelineService pipeline,
        SpotManager spots,
        ILoggerFactory loggerFactory)
    {
        _log = loggerFactory.CreateLogger<TciServer>();
        _options = options.Value;
        _radio = radio;
        _tx = tx;
        _pipeline = pipeline;
        _spots = spots;
        _loggerFactory = loggerFactory;
    }

    public int ClientCount => _clients.Count;

    public Task StartAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _log.LogInformation("tci.disabled (set Tci:Enabled=true to enable)");
            return Task.CompletedTask;
        }

        _radio.StateChanged += OnRadioStateChanged;
        _radio.Connected += OnRadioConnected;
        _radio.Disconnected += OnRadioDisconnected;
        _subscribed = true;

        _log.LogInformation("tci.listening bind={Bind} port={Port}", _options.BindAddress, _options.Port);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_subscribed)
        {
            _radio.StateChanged -= OnRadioStateChanged;
            _radio.Connected -= OnRadioConnected;
            _radio.Disconnected -= OnRadioDisconnected;
            _subscribed = false;
        }

        _log.LogInformation("tci.stopping active={Count}", _clients.Count);

        foreach (var session in _clients.Values)
        {
            session.Dispose();
        }
        _clients.Clear();

        await Task.CompletedTask;
    }

    /// <summary>
    /// Handle an incoming HTTP request on the TCI listener port. Upgrades to a
    /// WebSocket, registers a session, and runs it to completion. Invoked from
    /// the port-branch middleware in Program.cs.
    /// </summary>
    public async Task AcceptAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        WebSocket ws;
        try
        {
            ws = await context.WebSockets.AcceptWebSocketAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "tci websocket upgrade failed");
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        var id = Guid.NewGuid();
        var sessionLog = _loggerFactory.CreateLogger<TciSession>();
        var session = new TciSession(id, ws, sessionLog, _radio, _tx, _pipeline, _spots, _options);

        _clients[id] = session;
        _log.LogInformation("tci.client.connected id={Id} total={Count}", id, _clients.Count);

        try
        {
            await session.RunAsync(context.RequestAborted);
        }
        finally
        {
            _clients.TryRemove(id, out _);
            session.Dispose();
            _log.LogInformation("tci.client.disconnected id={Id} total={Count}", id, _clients.Count);
        }
    }

    // --- Event handlers: broadcast state changes to all connected clients ---

    private void OnRadioStateChanged(StateDto state)
    {
        // Broadcast VFO, mode, filter changes to all clients
        // Use rate limiting for VFO (can fire rapidly during tuning)
        BroadcastRateLimited("vfo:0,0", TciProtocol.Command("vfo", 0, 0, state.VfoHz));
        BroadcastRateLimited("vfo:0,1", TciProtocol.Command("vfo", 0, 1, state.VfoHz));
        BroadcastRateLimited("dds:0", TciProtocol.Command("dds", 0, state.VfoHz));

        // Mode and filter are less frequent — send immediately
        string tciMode = TciProtocol.ModeToTci(state.Mode);
        Broadcast(TciProtocol.Command("modulation", 0, tciMode));
        Broadcast(TciProtocol.Command("rx_filter_band", 0, state.FilterLowHz, state.FilterHighHz));

        // TX frequency event (derived from VFO)
        Broadcast(TciProtocol.Command("tx_frequency", state.VfoHz));

        // IF limits on sample rate change
        int halfRate = state.SampleRate / 2;
        Broadcast(TciProtocol.Command("if_limits", -halfRate, halfRate));
    }

    private void OnRadioConnected(Protocol1.IProtocol1Client client)
    {
        Broadcast(TciProtocol.Command("start"));
    }

    private void OnRadioDisconnected()
    {
        Broadcast(TciProtocol.Command("stop"));
    }

    /// <summary>
    /// Broadcast a command to all connected clients immediately.
    /// </summary>
    private void Broadcast(string commandLine)
    {
        if (_clients.IsEmpty) return;
        foreach (var session in _clients.Values)
        {
            session.Send(commandLine);
        }
    }

    /// <summary>
    /// Broadcast a rate-limited command (VFO/DDS) to all connected clients.
    /// </summary>
    private void BroadcastRateLimited(string key, string commandLine)
    {
        if (_clients.IsEmpty) return;
        foreach (var session in _clients.Values)
        {
            session.SendRateLimited(key, commandLine);
        }
    }

    public void Dispose()
    {
        foreach (var session in _clients.Values)
        {
            session.Dispose();
        }
        _clients.Clear();
    }
}
