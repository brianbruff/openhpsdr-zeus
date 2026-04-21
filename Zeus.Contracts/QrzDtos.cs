namespace Zeus.Contracts;

public sealed record QrzLoginRequest(string Username, string Password);

public sealed record QrzLookupRequest(string Callsign);

public sealed record QrzStation(
    string Callsign,
    string? Name,
    string? FirstName,
    string? Country,
    string? State,
    string? City,
    string? Grid,
    double? Lat,
    double? Lon,
    int? Dxcc,
    int? CqZone,
    int? ItuZone,
    string? ImageUrl);

public sealed record QrzStatus(
    bool Connected,
    bool HasXmlSubscription,
    QrzStation? Home,
    string? Error,
    bool HasStoredCredentials = false);
