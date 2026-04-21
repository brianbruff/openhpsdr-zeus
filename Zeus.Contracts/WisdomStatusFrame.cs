using System.Buffers;

namespace Zeus.Contracts;

public enum WisdomPhase : byte
{
    Idle = 0,
    Building = 1,
    Ready = 2,
}

// [0x15][phase:u8] = 2 bytes. Latest value wins; no seq/timestamp needed.
public readonly record struct WisdomStatusFrame(WisdomPhase Phase)
{
    public const int ByteLength = 1 + 1;

    public void Serialize(IBufferWriter<byte> writer)
    {
        var span = writer.GetSpan(ByteLength);
        span[0] = (byte)MsgType.WisdomStatus;
        span[1] = (byte)Phase;
        writer.Advance(ByteLength);
    }

    public static WisdomStatusFrame Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < ByteLength)
            throw new InvalidDataException($"WisdomStatusFrame requires {ByteLength} bytes, got {bytes.Length}");
        if (bytes[0] != (byte)MsgType.WisdomStatus)
            throw new InvalidDataException($"expected WisdomStatus (0x{(byte)MsgType.WisdomStatus:X2}), got 0x{bytes[0]:X2}");
        return new WisdomStatusFrame((WisdomPhase)bytes[1]);
    }
}
