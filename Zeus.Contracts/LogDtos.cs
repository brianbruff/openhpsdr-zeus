namespace Zeus.Contracts;

public sealed record LogEntry(
    string Id,
    DateTime QsoDateTimeUtc,
    string Callsign,
    string? Name,
    double FrequencyMhz,
    string Band,
    string Mode,
    string RstSent,
    string RstRcvd,
    string? Grid,
    string? Country,
    int? Dxcc,
    int? CqZone,
    int? ItuZone,
    string? State,
    string? Comment,
    DateTime CreatedUtc,
    string? QrzLogId = null,
    DateTime? QrzUploadedUtc = null);

public sealed record CreateLogEntryRequest(
    string Callsign,
    string? Name,
    double FrequencyMhz,
    string Band,
    string Mode,
    string RstSent,
    string RstRcvd,
    string? Grid = null,
    string? Country = null,
    int? Dxcc = null,
    int? CqZone = null,
    int? ItuZone = null,
    string? State = null,
    string? Comment = null,
    DateTime? QsoDateTimeUtc = null);

public sealed record LogEntriesResponse(
    IEnumerable<LogEntry> Entries,
    int TotalCount);

public sealed record QrzPublishRequest(
    IEnumerable<string> LogEntryIds);

public sealed record QrzPublishResponse(
    int TotalCount,
    int SuccessCount,
    int FailedCount,
    IEnumerable<QrzPublishResult> Results);

public sealed record QrzPublishResult(
    string LogEntryId,
    bool Success,
    string? QrzLogId,
    string? Message);
