namespace Nereus.Protocol1.Discovery;

public enum HpsdrBoardKind : byte
{
    Metis = 0x00,
    Hermes = 0x01,
    Griffin = 0x02,
    Angelia = 0x04,
    Orion = 0x05,
    HermesLite2 = 0x06,
    OrionMkII = 0x0A,
    Unknown = 0xFF,
}
