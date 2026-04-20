// Manual smoke tool: run on a LAN with a real HPSDR Protocol-1 radio to
// confirm discovery works end-to-end. NOT executed in CI.
//
// Usage: dotnet run --project tools/discovery-probe [-- <timeout-seconds>]

using Microsoft.Extensions.Logging.Abstractions;
using Nereus.Protocol1.Discovery;

var timeout = TimeSpan.FromSeconds(args.Length > 0 && double.TryParse(args[0], out var s) ? s : 3.0);
var discovery = new RadioDiscoveryService(NullLogger<RadioDiscoveryService>.Instance);

Console.WriteLine($"Broadcasting discovery; listening for {timeout.TotalSeconds:F1}s...");
var found = await discovery.DiscoverAsync(timeout, CancellationToken.None);

if (found.Count == 0)
{
    Console.WriteLine("No radios responded.");
    return 1;
}

foreach (var r in found)
{
    Console.WriteLine($"{r.Ip,-16} {r.Mac}  {r.Board,-13} fw={r.FirmwareString} busy={r.Details.Busy}");
    if (r.Details.FixedIpEnabled)
    {
        Console.WriteLine($"    HL2 fixed-IP={r.Details.FixedIpAddress} dhcpOverride={r.Details.FixedIpOverridesDhcp}");
    }
    if (r.Details.MacAddressModified)
    {
        Console.WriteLine("    HL2 MAC modified");
    }
}

return 0;
