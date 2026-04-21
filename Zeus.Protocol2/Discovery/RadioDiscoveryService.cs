using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Zeus.Protocol2.Discovery;

public sealed class RadioDiscoveryService : IRadioDiscovery
{
    private const int HpsdrPort = 1024;
    private const int DiscoveryPacketLength = 60;
    private const int ReceiveBufferSize = 2048;
    private const int SendAttempts = 3;
    private static readonly TimeSpan SendGap = TimeSpan.FromMilliseconds(50);

    private readonly ILogger<RadioDiscoveryService> _log;

    public RadioDiscoveryService(ILogger<RadioDiscoveryService> log)
    {
        _log = log;
    }

    public async Task<IReadOnlyList<DiscoveredRadio>> DiscoverAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var broadcastTargets = GetBroadcastTargets();

        _log.LogDebug("p2.discovery.start interfaces={Count}", broadcastTargets.Count);
        foreach (var (ifaceAddr, bcastAddr) in broadcastTargets)
        {
            _log.LogDebug("p2.discovery.interface local={Local} broadcast={Broadcast}", ifaceAddr, bcastAddr);
        }

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            EnableBroadcast = true,
        };
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));

        var packet = BuildDiscoveryPacket();
        await SendProbesAsync(socket, packet, broadcastTargets, ct).ConfigureAwait(false);

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
                    _log.LogWarning(ex, "p2.discovery.socket.error");
                    break;
                }

                var fromIp = ((IPEndPoint)res.RemoteEndPoint).Address;
                var slice = new ReadOnlySpan<byte>(receiveBuffer, 0, res.ReceivedBytes);

                if (!ReplyParser.TryParse(slice, fromIp, out var radio))
                {
                    _log.LogDebug(
                        "p2.discovery.reply.invalid from={Ip} len={Len}",
                        fromIp,
                        res.ReceivedBytes);
                    continue;
                }

                byMac[radio.Mac] = radio;
                _log.LogInformation(
                    "p2.discovery.reply from={Ip} board={Board} mac={Mac} fw={Firmware} rxs={NumRx} protoVer={ProtoVer}",
                    radio.Ip,
                    radio.Board,
                    radio.Mac,
                    radio.FirmwareString,
                    radio.Details.NumReceivers,
                    radio.Details.ProtocolSupported);
            }
        }
        finally
        {
            try { socket.Shutdown(SocketShutdown.Both); } catch (SocketException) { }
        }

        ct.ThrowIfCancellationRequested();

        return byMac.Values.OrderBy(IpSortKey).ToList();
    }

    private async Task SendProbesAsync(
        Socket socket,
        ReadOnlyMemory<byte> packet,
        IReadOnlyList<(IPAddress ifaceAddr, IPAddress bcastAddr)> broadcastTargets,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < SendAttempts; attempt++)
        {
            foreach (var (_, bcastAddr) in broadcastTargets)
            {
                var endpoint = new IPEndPoint(bcastAddr, HpsdrPort);
                try
                {
                    await socket.SendToAsync(packet, SocketFlags.None, endpoint, ct).ConfigureAwait(false);
                }
                catch (SocketException ex)
                {
                    _log.LogWarning(ex, "p2.discovery.send.error broadcast={Broadcast}", bcastAddr);
                }
            }

            if (attempt < SendAttempts - 1)
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

    private IReadOnlyList<(IPAddress ifaceAddr, IPAddress bcastAddr)> GetBroadcastTargets()
    {
        var targets = new List<(IPAddress, IPAddress)>();

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var iface in interfaces)
            {
                if (iface.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                if (iface.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    continue;

                var ipProps = iface.GetIPProperties();
                foreach (var unicast in ipProps.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    var ip = unicast.Address;
                    var mask = unicast.IPv4Mask;

                    if (mask == null || mask.Equals(IPAddress.Any))
                        continue;

                    var ipBytes = ip.GetAddressBytes();
                    var maskBytes = mask.GetAddressBytes();
                    var bcastBytes = new byte[4];

                    for (var i = 0; i < 4; i++)
                    {
                        bcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
                    }

                    var bcastAddr = new IPAddress(bcastBytes);
                    targets.Add((ip, bcastAddr));
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "p2.discovery.enumerate.error");
        }

        if (targets.Count == 0)
        {
            _log.LogWarning("p2.discovery.enumerate.empty, using global broadcast");
            targets.Add((IPAddress.Any, IPAddress.Broadcast));
        }

        return targets;
    }

    private static byte[] BuildDiscoveryPacket()
    {
        var buf = new byte[DiscoveryPacketLength];
        buf[4] = 0x02;
        return buf;
    }

    private static uint IpSortKey(DiscoveredRadio r)
    {
        var bytes = r.Ip.GetAddressBytes();
        if (bytes.Length != 4) return uint.MaxValue;
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }
}
