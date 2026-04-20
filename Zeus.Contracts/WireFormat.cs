// Web-side wire is deliberately little-endian: JS DataView/TypedArrays are LE-by-default
// on all target machines. Big-endian is only used on the HPSDR radio side (Protocol 1 UDP),
// which is a distinct concern handled inside Zeus.Protocol1.
using System.Buffers.Binary;

namespace Zeus.Contracts;

public static class WireFormat
{
    public const int HeaderSize = 16;

    public static void WriteHeader(
        Span<byte> dst,
        MsgType msgType,
        byte flags,
        ushort payloadLen,
        uint seq,
        double tsUnixMs)
    {
        if (dst.Length < HeaderSize)
            throw new ArgumentException($"header buffer must be at least {HeaderSize} bytes", nameof(dst));

        dst[0] = (byte)msgType;
        dst[1] = flags;
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(2, 2), payloadLen);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(4, 4), seq);
        BinaryPrimitives.WriteDoubleLittleEndian(dst.Slice(8, 8), tsUnixMs);
    }

    public static void ReadHeader(
        ReadOnlySpan<byte> src,
        out MsgType msgType,
        out byte flags,
        out ushort payloadLen,
        out uint seq,
        out double tsUnixMs)
    {
        if (src.Length < HeaderSize)
            throw new ArgumentException($"header buffer must be at least {HeaderSize} bytes", nameof(src));

        msgType = (MsgType)src[0];
        flags = src[1];
        payloadLen = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(2, 2));
        seq = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(4, 4));
        tsUnixMs = BinaryPrimitives.ReadDoubleLittleEndian(src.Slice(8, 8));
    }
}
