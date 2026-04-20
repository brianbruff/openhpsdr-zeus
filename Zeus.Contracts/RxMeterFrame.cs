using System.Buffers;
using System.Buffers.Binary;

namespace Zeus.Contracts;

// Compact RX-meter frame: [0x14][rxDbm : f32 LE] = 5 bytes.
// Broadcast at ~5 Hz from DspPipelineService while an engine is live.
// Uses the same header-less convention as TxMetersFrame — the client treats
// the latest value as authoritative and never needs per-frame seq/ts for a
// signal meter.
public readonly record struct RxMeterFrame(float RxDbm)
{
    public const int ByteLength = 1 + 4;

    public void Serialize(IBufferWriter<byte> writer)
    {
        var span = writer.GetSpan(ByteLength);
        span[0] = (byte)MsgType.RxMeter;
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(1, 4), RxDbm);
        writer.Advance(ByteLength);
    }

    public static RxMeterFrame Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < ByteLength)
            throw new InvalidDataException($"RxMeterFrame requires {ByteLength} bytes, got {bytes.Length}");
        if (bytes[0] != (byte)MsgType.RxMeter)
            throw new InvalidDataException($"expected RxMeter (0x{(byte)MsgType.RxMeter:X2}), got 0x{bytes[0]:X2}");
        return new RxMeterFrame(BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(1, 4)));
    }
}
