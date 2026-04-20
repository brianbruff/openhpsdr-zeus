// Phase 2 demo artifact — manually invoked. Discovers HPSDR P1 radios on the LAN,
// connects to one, streams IQ for the requested duration, and prints packet stats
// per second. Not in CI; it needs a real radio. See docs/prd/06-roadmap-mvp.md Phase 2.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Protocol1;
using Zeus.Protocol1.Discovery;

int durationSec = 60;
HpsdrSampleRate rate = HpsdrSampleRate.Rate192k;
string? forcedIp = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--seconds" or "-s" when i + 1 < args.Length:
            durationSec = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--rate" or "-r" when i + 1 < args.Length:
            rate = args[++i] switch
            {
                "48" => HpsdrSampleRate.Rate48k,
                "96" => HpsdrSampleRate.Rate96k,
                "192" => HpsdrSampleRate.Rate192k,
                "384" => HpsdrSampleRate.Rate384k,
                _ => throw new ArgumentException($"unknown rate {args[i]} (use 48/96/192/384)"),
            };
            break;
        case "--ip" when i + 1 < args.Length:
            forcedIp = args[++i];
            break;
        case "--help" or "-h":
            Console.WriteLine("zeus-dump [--seconds N] [--rate 48|96|192|384] [--ip 192.168.x.y]");
            return 0;
    }
}

IPEndPoint endpoint;
if (forcedIp is not null)
{
    endpoint = new IPEndPoint(IPAddress.Parse(forcedIp), 1024);
    Console.WriteLine($"Using forced IP {endpoint}");
}
else
{
    Console.WriteLine("Discovering radios on LAN...");
    var discovery = new RadioDiscoveryService(NullLogger<RadioDiscoveryService>.Instance);
    var found = await discovery.DiscoverAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
    if (found.Count == 0)
    {
        Console.Error.WriteLine("No radios found. Pass --ip <addr> to force.");
        return 2;
    }
    foreach (var r in found)
        Console.WriteLine($"  {r.Ip}  {r.Mac}  {r.Board} fw={r.FirmwareString}");
    var picked = found[0];
    endpoint = new IPEndPoint(picked.Ip, 1024);
    Console.WriteLine($"Connecting to {picked.Board} at {endpoint}");
}

using var client = new Protocol1Client(NullLogger<Protocol1Client>.Instance);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await client.ConnectAsync(endpoint, cts.Token);
await client.StartAsync(new StreamConfig(rate, PreampOn: false, Atten: HpsdrAtten.Zero), cts.Token);
Console.WriteLine($"Streaming at {rate}, duration={durationSec}s, ctrl-c to stop.");
Console.WriteLine("  t    frames  drops  rate(p/s)  iq/s");

var drain = Task.Run(async () =>
{
    await foreach (var _ in client.IqFrames.ReadAllAsync(cts.Token)) { /* drop frames; stats are elsewhere */ }
}, cts.Token);

var started = Stopwatch.StartNew();
long lastFrames = 0;
long lastDrops = 0;
for (int t = 1; t <= durationSec; t++)
{
    try { await Task.Delay(1000, cts.Token); } catch (OperationCanceledException) { break; }
    long frames = client.TotalFrames;
    long drops = client.DroppedFrames;
    long dFrames = frames - lastFrames;
    long dDrops = drops - lastDrops;
    lastFrames = frames;
    lastDrops = drops;
    Console.WriteLine(
        $"  {t,3}  {frames,7}  {drops,5}  {dFrames,8}  {(long)dFrames * 126,8}");
}
started.Stop();

try { await client.StopAsync(CancellationToken.None); } catch { }
try { await client.DisconnectAsync(CancellationToken.None); } catch { }

double elapsedSec = started.Elapsed.TotalSeconds;
long finalFrames = client.TotalFrames;
long finalDrops = client.DroppedFrames;
double dropRate = finalFrames > 0 ? (double)finalDrops / finalFrames : 0;

Console.WriteLine();
Console.WriteLine(
    $"Done. elapsed={elapsedSec:F1}s  frames={finalFrames}  drops={finalDrops}  drop-rate={dropRate:P2}");
return finalDrops == 0 ? 0 : (dropRate < 0.01 ? 0 : 3);
