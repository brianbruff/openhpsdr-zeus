using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Zeus.Protocol1.Tests;

/// <summary>
/// Loopback test for <see cref="Protocol1Client.AdcOverloadObserved"/>. Stands up a
/// fake-radio UDP socket, lets the client send the Metis-start, then feeds crafted
/// EP6 packets back so we can assert the overload bits propagate through the RX
/// loop to event subscribers. No real HL2/ANAN required.
/// </summary>
public class Protocol1Client_AdcOverloadEventTests
{
    [Fact]
    public async Task AdcOverloadObserved_FiresOncePerPacket_WithCorrectBits()
    {
        using var fakeRadio = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        fakeRadio.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        fakeRadio.ReceiveTimeout = 500;
        var fakeRadioEp = (IPEndPoint)fakeRadio.LocalEndPoint!;

        using var client = new Protocol1Client();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var observed = new List<AdcOverloadStatus>();
        var firstPacketGate = new TaskCompletionSource();
        client.AdcOverloadObserved += status =>
        {
            lock (observed)
            {
                observed.Add(status);
                if (observed.Count == 1) firstPacketGate.TrySetResult();
            }
        };

        await client.ConnectAsync(fakeRadioEp, cts.Token);
        await client.StartAsync(
            new StreamConfig(HpsdrSampleRate.Rate192k, PreampOn: false, Atten: HpsdrAtten.Zero),
            cts.Token);

        // Drain the Metis-start (plus possible mac retries) to learn the client's local port.
        IPEndPoint clientEp = ReceiveFirstFromClient(fakeRadio);

        // Send 4 packets with varied overload bit patterns.
        var expected = new[]
        {
            (adc0: false, adc1: false),
            (adc0: true,  adc1: false),
            (adc0: false, adc1: true),
            (adc0: true,  adc1: true),
        };
        for (uint seq = 0; seq < expected.Length; seq++)
        {
            var (adc0, adc1) = expected[seq];
            var packet = BuildEp6Packet(seq, adc0Overload: adc0, adc1Overload: adc1);
            fakeRadio.SendTo(packet, clientEp);
        }

        // Wait for at least one event.
        await firstPacketGate.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
        // Grace for the remaining three packets.
        await Task.Delay(200, cts.Token);

        await client.StopAsync(CancellationToken.None);
        await client.DisconnectAsync(CancellationToken.None);

        List<AdcOverloadStatus> captured;
        lock (observed) captured = new(observed);

        Assert.True(captured.Count >= expected.Length,
            $"expected ≥{expected.Length} overload events, got {captured.Count}");

        // Event ordering is preserved across packets (single RX thread).
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].adc0, captured[i].Adc0);
            Assert.Equal(expected[i].adc1, captured[i].Adc1);
        }
    }

    private static IPEndPoint ReceiveFirstFromClient(Socket fakeRadio)
    {
        // Read one packet to learn the client's local endpoint, then stop reading.
        // The TX loop will keep sending control frames at ~3ms; we let the kernel
        // buffer them — fakeRadio doesn't need to drain to stay functional.
        var buf = new byte[2048];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        fakeRadio.ReceiveFrom(buf, ref remote);
        return (IPEndPoint)remote;
    }

    private static byte[] BuildEp6Packet(uint seq, bool adc0Overload, bool adc1Overload)
    {
        var packet = new byte[1032];
        packet[0] = 0xEF; packet[1] = 0xFE;
        packet[2] = 0x01; packet[3] = 0x06;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), seq);

        for (int f = 0; f < 2; f++)
        {
            int fs = 8 + f * 512;
            packet[fs + 0] = 0x7F; packet[fs + 1] = 0x7F; packet[fs + 2] = 0x7F;
            packet[fs + 4] = adc0Overload ? (byte)0x01 : (byte)0x00; // C1[0] = ADC0 overload
            packet[fs + 5] = adc1Overload ? (byte)0x01 : (byte)0x00; // C2[0] = ADC1 overload
        }
        return packet;
    }
}
