using System.Net;
using Xunit;

namespace Nereus.Protocol1.Tests;

/// <summary>
/// Live HL2 / ANAN regression harness. Skipped unless <c>NEREUS_LIVE_RADIO=ip[:port]</c>
/// is set in the environment. Do NOT auto-connect to the user's radio in CI.
/// </summary>
public class Protocol1Client_LiveRx_IntegrationTest
{
    [SkippableFact]
    public async Task Streams300FramesAt192k_WithLessThan10PercentDropped()
    {
        var endpoint = ResolveEndpointFromEnv();
        Skip.If(endpoint is null,
            "Set NEREUS_LIVE_RADIO=ip[:port] to exercise this harness. Default port 1024.");

        using var client = new Protocol1Client();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await client.ConnectAsync(endpoint, cts.Token).ConfigureAwait(false);
        await client.StartAsync(
            new StreamConfig(HpsdrSampleRate.Rate192k, PreampOn: false, Atten: HpsdrAtten.Zero),
            cts.Token).ConfigureAwait(false);

        int frameCount = 0;
        await foreach (var frame in client.IqFrames.ReadAllAsync(cts.Token).ConfigureAwait(false))
        {
            Assert.Equal(192_000, frame.SampleRateHz);
            Assert.Equal(PacketParser.ComplexSamplesPerPacket, frame.SampleCount);
            if (++frameCount >= 300) break;
        }

        await client.StopAsync(CancellationToken.None).ConfigureAwait(false);
        await client.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);

        long dropped = client.DroppedFrames;
        long total = client.TotalFrames;
        Assert.True(total >= 300, $"expected >=300 parsed frames, got {total}");
        Assert.True(dropped * 10 < total, $"dropped {dropped}/{total} > 10%");
    }

    private static IPEndPoint? ResolveEndpointFromEnv()
    {
        var raw = Environment.GetEnvironmentVariable("NEREUS_LIVE_RADIO");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Split(':', 2);
        if (!IPAddress.TryParse(parts[0], out var ip)) return null;
        int port = 1024;
        if (parts.Length == 2 && int.TryParse(parts[1], out var parsedPort)) port = parsedPort;
        return new IPEndPoint(ip, port);
    }
}
