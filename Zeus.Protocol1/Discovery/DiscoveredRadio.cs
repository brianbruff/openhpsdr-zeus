using System.Net;
using System.Net.NetworkInformation;

namespace Zeus.Protocol1.Discovery;

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
    bool FixedIpEnabled,
    bool FixedIpOverridesDhcp,
    bool MacAddressModified,
    IPAddress? FixedIpAddress,
    byte GatewareBuild,
    byte? HermesLite2MinorVersion);
