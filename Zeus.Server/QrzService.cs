using System.Xml.Linq;
using Zeus.Contracts;

namespace Zeus.Server;

public sealed class QrzService
{
    private const string QrzXmlApiUrl = "https://xmldata.qrz.com/xml/current/";
    private const string Agent = "Zeus";
    private const string ServiceName = "qrz";
    private static readonly XNamespace Ns = "http://xmldata.qrz.com";

    private readonly HttpClient _http;
    private readonly ILogger<QrzService> _log;
    private readonly CredentialStore _credStore;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _username;
    private string? _password;
    private string? _sessionKey;
    private DateTime _sessionExpiry;
    private QrzStation? _home;
    private bool _hasXmlSubscription;

    public QrzService(IHttpClientFactory httpClientFactory, ILogger<QrzService> log, CredentialStore credStore)
    {
        _http = httpClientFactory.CreateClient("Qrz");
        _log = log;
        _credStore = credStore;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        // Attempt silent re-login from stored credentials
        var stored = await _credStore.GetAsync(ServiceName, ct);
        if (stored != null)
        {
            _log.LogInformation("Found stored QRZ credentials for user={User}; attempting silent login", stored.Username);
            try
            {
                await LoginAsync(stored.Username, stored.Password, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Silent QRZ login failed; clearing stored credentials");
                await _credStore.DeleteAsync(ServiceName, ct);
            }
        }
    }

    public QrzStatus GetStatus() => new(
        Connected: _sessionKey != null && _home != null,
        HasXmlSubscription: _hasXmlSubscription,
        Home: _home,
        Error: null,
        HasStoredCredentials: !string.IsNullOrWhiteSpace(_username));

    public async Task<QrzStatus> LoginAsync(string username, string password, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _username = username;
            _password = password;
            _sessionKey = null;
            _home = null;
            _hasXmlSubscription = false;

            var key = await AcquireSessionKeyAsync(ct);
            if (key == null)
            {
                return new QrzStatus(false, false, null, "QRZ login failed");
            }

            // Look up the user's own callsign to populate home station info. Success here
            // is also proof the account has an active XML subscription (the session key
            // alone doesn't guarantee lookup rights).
            try
            {
                var home = await LookupInternalAsync(username, ct);
                _home = home;
                _hasXmlSubscription = home != null;

                // Persist credentials on successful login
                await _credStore.SetAsync(ServiceName, username, password, ct);

                return new QrzStatus(
                    Connected: true,
                    HasXmlSubscription: _hasXmlSubscription,
                    Home: _home,
                    Error: _hasXmlSubscription ? null : "XML subscription required; login OK but lookups will fail",
                    HasStoredCredentials: true);
            }
            catch (QrzSubscriptionRequiredException ex)
            {
                _hasXmlSubscription = false;

                // Still persist credentials even without XML subscription
                await _credStore.SetAsync(ServiceName, username, password, ct);

                return new QrzStatus(true, false, null, ex.Message, HasStoredCredentials: true);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<QrzStation?> LookupAsync(string callsign, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (string.IsNullOrWhiteSpace(_username) || string.IsNullOrWhiteSpace(_password))
            {
                throw new InvalidOperationException("QRZ not logged in");
            }
            return await LookupInternalAsync(callsign, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        _username = null;
        _password = null;
        _sessionKey = null;
        _sessionExpiry = default;
        _home = null;
        _hasXmlSubscription = false;

        // Delete stored credentials
        await _credStore.DeleteAsync(ServiceName, ct);
    }

    // Assumes _gate is held.
    private async Task<QrzStation?> LookupInternalAsync(string callsign, CancellationToken ct)
    {
        var key = await AcquireSessionKeyAsync(ct);
        if (key == null) return null;

        var url = $"{QrzXmlApiUrl}?s={key}&callsign={Uri.EscapeDataString(callsign)}";
        var xml = await _http.GetStringAsync(url, ct);
        var doc = XDocument.Parse(xml);

        var session = doc.Descendants(Ns + "Session").FirstOrDefault()
                      ?? doc.Descendants("Session").FirstOrDefault();
        var sessionError = Get(session, "Error");
        if (!string.IsNullOrEmpty(sessionError))
        {
            if (sessionError.Contains("subscription", StringComparison.OrdinalIgnoreCase))
                throw new QrzSubscriptionRequiredException(sessionError);
            if (sessionError.Contains("Invalid session", StringComparison.OrdinalIgnoreCase))
            {
                // Session timed out — force re-auth and retry once.
                _sessionKey = null;
                var retryKey = await AcquireSessionKeyAsync(ct);
                if (retryKey == null) return null;
                var retryUrl = $"{QrzXmlApiUrl}?s={retryKey}&callsign={Uri.EscapeDataString(callsign)}";
                xml = await _http.GetStringAsync(retryUrl, ct);
                doc = XDocument.Parse(xml);
            }
            else
            {
                _log.LogWarning("QRZ lookup error for {Callsign}: {Err}", callsign, sessionError);
                return null;
            }
        }

        var el = doc.Descendants(Ns + "Callsign").FirstOrDefault()
                 ?? doc.Descendants("Callsign").FirstOrDefault();
        if (el == null) return null;

        var lat = ParseDouble(Get(el, "lat"));
        var lon = ParseDouble(Get(el, "lon"));
        return new QrzStation(
            Callsign: (Get(el, "call") ?? callsign).ToUpperInvariant(),
            Name: Get(el, "name"),
            FirstName: Get(el, "fname"),
            Country: Get(el, "country"),
            State: Get(el, "state"),
            City: Get(el, "addr2"),
            Grid: Get(el, "grid"),
            Lat: NormalizeCoord(lat, maxAbs: 90),
            Lon: NormalizeCoord(lon, maxAbs: 180),
            Dxcc: ParseInt(Get(el, "dxcc")),
            CqZone: ParseInt(Get(el, "cqzone")),
            ItuZone: ParseInt(Get(el, "ituzone")),
            ImageUrl: Get(el, "image"));
    }

    // Assumes _gate is held.
    private async Task<string?> AcquireSessionKeyAsync(CancellationToken ct)
    {
        if (_sessionKey != null && _sessionExpiry > DateTime.UtcNow) return _sessionKey;
        if (_username == null || _password == null) return null;

        var url = $"{QrzXmlApiUrl}?username={Uri.EscapeDataString(_username)}&password={Uri.EscapeDataString(_password)}&agent={Agent}";
        var xml = await _http.GetStringAsync(url, ct);
        var doc = XDocument.Parse(xml);
        var session = doc.Descendants(Ns + "Session").FirstOrDefault()
                      ?? doc.Descendants("Session").FirstOrDefault();
        if (session == null)
        {
            _log.LogWarning("QRZ login response had no Session element");
            return null;
        }

        var err = Get(session, "Error");
        if (!string.IsNullOrEmpty(err))
        {
            if (err.Contains("subscription", StringComparison.OrdinalIgnoreCase))
                throw new QrzSubscriptionRequiredException(err);
            _log.LogWarning("QRZ login error: {Err}", err);
            return null;
        }

        _sessionKey = Get(session, "Key");
        _sessionExpiry = DateTime.UtcNow.AddHours(1);
        return _sessionKey;
    }

    private static string? Get(XElement? parent, string name)
    {
        if (parent == null) return null;
        return parent.Element(Ns + name)?.Value ?? parent.Element(name)?.Value;
    }

    private static double? ParseDouble(string? s) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    private static int? ParseInt(string? s) =>
        int.TryParse(s, out var v) ? v : null;

    // QRZ occasionally returns microdegree-scaled coordinates (value × 1e6). If the raw
    // value is outside the valid range but would be valid when divided by 1e6, normalize.
    private static double? NormalizeCoord(double? value, double maxAbs)
    {
        if (value is null) return null;
        var v = value.Value;
        if (Math.Abs(v) <= maxAbs) return v;
        var scaled = v / 1_000_000.0;
        return Math.Abs(scaled) <= maxAbs ? scaled : null;
    }
}

public sealed class QrzSubscriptionRequiredException : Exception
{
    public QrzSubscriptionRequiredException(string message) : base(message) { }
}
