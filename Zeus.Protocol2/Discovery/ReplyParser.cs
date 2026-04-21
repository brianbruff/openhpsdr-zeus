using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;

namespace Zeus.Protocol2.Discovery;

public static class ReplyParser
{
    public const int MinimumReplyLength = 24;
    public const byte StatusIdle = 0x02;
    public const byte StatusBusy = 0x03;

    public static bool TryParse(
        ReadOnlySpan<byte> raw,
        IPAddress fromIp,
        [NotNullWhen(true)] out DiscoveredRadio? radio)
    {
        radio = null;
        if (raw.Length < MinimumReplyLength) return false;
        if (raw[0] != 0x00 || raw[1] != 0x00 || raw[2] != 0x00 || raw[3] != 0x00) return false;

        var status = raw[4];
        if (status != StatusIdle && status != StatusBusy) return false;

        var macBytes = raw.Slice(5, 6).ToArray();
        var mac = new PhysicalAddress(macBytes);

        var rawBoardId = raw[11];
        var protocolSupported = raw[12];
        var codeVersion = raw[13];
        var mercuryVersion0 = raw[14];
        var mercuryVersion1 = raw[15];
        var mercuryVersion2 = raw[16];
        var mercuryVersion3 = raw[17];
        var pennyVersion = raw[18];
        var metisVersion = raw[19];
        var numReceivers = raw[20];
        var betaVersion = raw.Length > 23 ? raw[23] : (byte)0;

        var board = MapBoard(rawBoardId);
        var firmwareString = FormatFirmware(codeVersion, betaVersion);

        var details = new DiscoveryDetails(
            RawReply: raw.ToArray(),
            RawBoardId: rawBoardId,
            Busy: status == StatusBusy,
            ProtocolSupported: protocolSupported,
            NumReceivers: numReceivers,
            BetaVersion: betaVersion,
            MercuryVersion0: mercuryVersion0,
            MercuryVersion1: mercuryVersion1,
            MercuryVersion2: mercuryVersion2,
            MercuryVersion3: mercuryVersion3,
            PennyVersion: pennyVersion,
            MetisVersion: metisVersion);

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
        0x00 => HpsdrBoardKind.Atlas,
        0x01 => HpsdrBoardKind.Hermes,
        0x02 => HpsdrBoardKind.HermesII,
        0x04 => HpsdrBoardKind.Angelia,
        0x05 => HpsdrBoardKind.Orion,
        0x06 => HpsdrBoardKind.HermesLite2,
        0x0A => HpsdrBoardKind.OrionMkII,
        _ => HpsdrBoardKind.Unknown,
    };

    private static string FormatFirmware(byte codeVersion, byte betaVersion)
    {
        var major = codeVersion / 10;
        var minor = codeVersion % 10;
        return betaVersion == 0 ? $"{major}.{minor}" : $"{major}.{minor}b{betaVersion}";
    }
}
