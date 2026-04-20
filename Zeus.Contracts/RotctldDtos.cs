namespace Zeus.Contracts;

public sealed record RotctldConfig(
    bool Enabled = false,
    string Host = "127.0.0.1",
    int Port = 4533,
    int PollingIntervalMs = 500);

public sealed record RotctldStatus(
    bool Enabled,
    bool Connected,
    string Host,
    int Port,
    double? CurrentAz,
    double? TargetAz,
    bool Moving,
    string? Error);

public sealed record RotctldSetAzRequest(double Azimuth);

public sealed record RotctldTestRequest(string Host, int Port);

public sealed record RotctldTestResult(bool Ok, string? Error);
