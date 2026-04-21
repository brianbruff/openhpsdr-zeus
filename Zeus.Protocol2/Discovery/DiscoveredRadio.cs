using System.Net;
using System.Net.NetworkInformation;

namespace Zeus.Protocol2.Discovery;

public sealed record DiscoveredRadio(
    IPAddress Ip,
    PhysicalAddress Mac,
    HpsdrBoardKind Board,
    byte FirmwareVersion,
    string FirmwareString,
    DiscoveryDetails Details);

public sealed record DiscoveryDetails(
    byte[] RawReply,
    byte RawBoardId,
    bool Busy,
    byte ProtocolSupported,
    byte NumReceivers,
    byte BetaVersion,
    byte MercuryVersion0,
    byte MercuryVersion1,
    byte MercuryVersion2,
    byte MercuryVersion3,
    byte PennyVersion,
    byte MetisVersion);
