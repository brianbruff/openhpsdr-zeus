using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.Extensions.Options;
using Zeus.Contracts;

namespace Zeus.Server.Tci;

/// <summary>
/// TCI (Transceiver Control Interface) server. Hosts a WebSocket listener on
/// a dedicated port (default 40001) for ExpertSDR3-compatible remote control.
/// Spoken by loggers (Log4OM, N1MM+), digital-mode apps (JTDX, WSJT-X), and
/// SDR display tools. Implements TCI v1.8 protocol per the ExpertSDR3 spec.
/// </summary>
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
    private HttpListener? _listener;
    private Task? _acceptTask;
    private CancellationTokenSource? _cts;

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

        try
        {
            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            // HttpListener prefix syntax: "+" is the all-interfaces wildcard.
            // Translate common user values ("0.0.0.0", "*", empty) so the
            // config stays intuitive. Explicit IPs (e.g. "127.0.0.1") pass through.
            string host = _options.BindAddress switch
            {
                "0.0.0.0" or "*" or "" or null => "+",
                _ => _options.BindAddress,
            };
            string prefix = $"http://{host}:{_options.Port}/";
            _listener.Prefixes.Add(prefix);
            _listener.Start();

            _log.LogInformation("tci.listening bind={Bind} port={Port}", _options.BindAddress, _options.Port);

            // Subscribe to radio events for broadcasting state changes
            _radio.StateChanged += OnRadioStateChanged;
            _radio.Connected += OnRadioConnected;
            _radio.Disconnected += OnRadioDisconnected;

            _acceptTask = AcceptLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "tci.start failed");
            throw;
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_listener is null) return;

        _log.LogInformation("tci.stopping");

        _radio.StateChanged -= OnRadioStateChanged;
        _radio.Connected -= OnRadioConnected;
        _radio.Disconnected -= OnRadioDisconnected;

        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        _listener.Stop();

        if (_acceptTask is not null)
        {
            try { await _acceptTask; } catch { /* shutdown */ }
        }

        // Close all client sessions
        foreach (var session in _clients.Values)
        {
            session.Dispose();
        }
        _clients.Clear();

        _cts?.Dispose();
        _cts = null;
        _listener.Close();
        _listener = null;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var context = await _listener!.GetContextAsync();
                _ = Task.Run(() => HandleClientAsync(context, ct), ct);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "tci accept loop error");
            }
        }
    }

    private async Task HandleClientAsync(HttpListenerContext context, CancellationToken ct)
    {
        if (!context.Request.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            context.Response.Close();
            return;
        }

        HttpListenerWebSocketContext wsContext;
        try
        {
            wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "tci websocket upgrade failed");
            context.Response.StatusCode = 500;
            context.Response.Close();
            return;
        }

        var ws = wsContext.WebSocket;
        var id = Guid.NewGuid();
        var sessionLog = _loggerFactory.CreateLogger<TciSession>();
        var session = new TciSession(id, ws, sessionLog, _radio, _tx, _pipeline, _spots, _options);

        _clients[id] = session;
        _log.LogInformation("tci.client.connected id={Id} total={Count}", id, _clients.Count);

        try
        {
            await session.RunAsync(ct);
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
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        _cts?.Dispose();
        _cts = null;

        _listener?.Stop();
        _listener?.Close();
        _listener = null;

        foreach (var session in _clients.Values)
        {
            session.Dispose();
        }
        _clients.Clear();
    }
}
