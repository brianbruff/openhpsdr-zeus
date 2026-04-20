using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Zeus.Protocol1.Discovery;

public sealed class RadioDiscoveryService : IRadioDiscovery
{
    private const int HpsdrPort = 1024;
    private const int DiscoveryPacketLength = 63;
    private const int ReceiveBufferSize = 2048;
    private const int MacOsSendAttempts = 3;
    private static readonly TimeSpan SendGap = TimeSpan.FromMilliseconds(50);

    private static readonly IPEndPoint BroadcastEndpoint = new(IPAddress.Broadcast, HpsdrPort);

    private readonly ILogger<RadioDiscoveryService> _log;

    public RadioDiscoveryService(ILogger<RadioDiscoveryService> log)
    {
        _log = log;
    }

    public async Task<IReadOnlyList<DiscoveredRadio>> DiscoverAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            EnableBroadcast = true,
        };
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));

        var packet = BuildDiscoveryPacket();
        await SendProbesAsync(socket, packet, ct).ConfigureAwait(false);

        var byMac = new Dictionary<PhysicalAddress, DiscoveredRadio>();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var receiveBuffer = new byte[ReceiveBufferSize];
        var any = new IPEndPoint(IPAddress.Any, 0);

        try
        {
            while (!timeoutCts.IsCancellationRequested)
            {
                SocketReceiveFromResult res;
                try
                {
                    res = await socket.ReceiveFromAsync(
                        receiveBuffer,
                        SocketFlags.None,
                        any,
                        timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    _log.LogWarning(ex, "discovery.socket.error");
                    break;
                }

                var fromIp = ((IPEndPoint)res.RemoteEndPoint).Address;
                var slice = new ReadOnlySpan<byte>(receiveBuffer, 0, res.ReceivedBytes);

                if (!ReplyParser.TryParse(slice, fromIp, out var radio))
                {
                    _log.LogDebug(
                        "discovery.reply.invalid from={Ip} len={Len}",
                        fromIp,
                        res.ReceivedBytes);
                    continue;
                }

                byMac[radio.Mac] = radio;
                _log.LogInformation(
                    "discovery.reply from={Ip} board={Board} mac={Mac} fw={Firmware}",
                    radio.Ip,
                    radio.Board,
                    radio.Mac,
                    radio.FirmwareString);
            }
        }
        finally
        {
            try { socket.Shutdown(SocketShutdown.Both); } catch (SocketException) { }
        }

        ct.ThrowIfCancellationRequested();

        return byMac.Values.OrderBy(IpSortKey).ToList();
    }

    private async Task SendProbesAsync(Socket socket, ReadOnlyMemory<byte> packet, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MacOsSendAttempts; attempt++)
        {
            await socket.SendToAsync(packet, SocketFlags.None, BroadcastEndpoint, ct).ConfigureAwait(false);
            if (attempt < MacOsSendAttempts - 1)
            {
                try
                {
                    await Task.Delay(SendGap, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private static byte[] BuildDiscoveryPacket()
    {
        var buf = new byte[DiscoveryPacketLength];
        buf[0] = 0xEF;
        buf[1] = 0xFE;
        buf[2] = 0x02;
        return buf;
    }

    private static uint IpSortKey(DiscoveredRadio r)
    {
        var bytes = r.Ip.GetAddressBytes();
        if (bytes.Length != 4) return uint.MaxValue;
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }
}
