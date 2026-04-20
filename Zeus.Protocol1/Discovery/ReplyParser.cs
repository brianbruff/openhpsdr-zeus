using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;

namespace Nereus.Protocol1.Discovery;

public static class ReplyParser
{
    public const int MinimumReplyLength = 24;
    public const byte StatusIdle = 0x02;
    public const byte StatusBusy = 0x03;

    private const byte Hl2FixedIpBit = 0x80;
    private const byte Hl2DhcpOverrideBit = 0x20;
    private const byte Hl2MacModifiedBit = 0x40;
    private const byte HermesLite2CodeVersionThreshold = 40;

    public static bool TryParse(
        ReadOnlySpan<byte> raw,
        IPAddress fromIp,
        [NotNullWhen(true)] out DiscoveredRadio? radio)
    {
        radio = null;
        if (raw.Length < MinimumReplyLength) return false;
        if (raw[0] != 0xEF || raw[1] != 0xFE) return false;

        var status = raw[2];
        if (status != StatusIdle && status != StatusBusy) return false;

        var macBytes = raw.Slice(3, 6).ToArray();
        var mac = new PhysicalAddress(macBytes);

        var codeVersion = raw[9];
        var rawBoardId = raw[10];
        var hl2Flags = raw[11];
        var gatewareBuild = raw[19];
        var hl2Minor = raw[21];

        var board = MapBoard(rawBoardId);
        var isHl2 = board == HpsdrBoardKind.HermesLite2 && codeVersion >= HermesLite2CodeVersionThreshold;

        var fixedIp = isHl2 && (hl2Flags & Hl2FixedIpBit) != 0;
        var fixedIpOverridesDhcp = isHl2 && (hl2Flags & (Hl2FixedIpBit | Hl2DhcpOverrideBit)) == (Hl2FixedIpBit | Hl2DhcpOverrideBit);
        var macModified = isHl2 && (hl2Flags & Hl2MacModifiedBit) != 0;

        IPAddress? overrideIp = null;
        if (fixedIp)
        {
            overrideIp = new IPAddress(raw.Slice(13, 4).ToArray());
        }

        var firmwareString = FormatFirmware(board, codeVersion, hl2Minor, isHl2);

        var details = new DiscoveryDetails(
            RawReply: raw.ToArray(),
            RawBoardId: rawBoardId,
            Busy: status == StatusBusy,
            FixedIpEnabled: fixedIp,
            FixedIpOverridesDhcp: fixedIpOverridesDhcp,
            MacAddressModified: macModified,
            FixedIpAddress: overrideIp,
            GatewareBuild: gatewareBuild,
            HermesLite2MinorVersion: isHl2 ? hl2Minor : null);

        radio = new DiscoveredRadio(
            Ip: fromIp,
            Mac: mac,
            Board: board,
            FirmwareVersion: codeVersion,
            FirmwareString: firmwareString,
            Details: details);
        return true;
    }

    private static HpsdrBoardKind MapBoard(byte raw) => raw switch
    {
        0x00 => HpsdrBoardKind.Metis,
        0x01 => HpsdrBoardKind.Hermes,
        0x02 => HpsdrBoardKind.Griffin,
        0x04 => HpsdrBoardKind.Angelia,
        0x05 => HpsdrBoardKind.Orion,
        0x06 => HpsdrBoardKind.HermesLite2,
        0x0A => HpsdrBoardKind.OrionMkII,
        _ => HpsdrBoardKind.Unknown,
    };

    private static string FormatFirmware(HpsdrBoardKind board, byte codeVersion, byte hl2Minor, bool isHl2)
    {
        if (isHl2) return $"{codeVersion}.{hl2Minor}";
        return $"{codeVersion / 10}.{codeVersion % 10}";
    }
}
