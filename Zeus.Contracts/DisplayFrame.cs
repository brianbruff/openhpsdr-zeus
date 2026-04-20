using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Zeus.Contracts;

[Flags]
public enum DisplayBodyFlags : byte
{
    None = 0,
    PanValid = 1 << 0,
    WfValid = 1 << 1,
}

public readonly record struct DisplayFrame(
    uint Seq,
    double TsUnixMs,
    byte RxId,
    DisplayBodyFlags BodyFlags,
    ushort Width,
    long CenterHz,
    float HzPerPixel,
    ReadOnlyMemory<float> PanDb,
    ReadOnlyMemory<float> WfDb)
{
    public const int BodyHeaderSize = 1 + 1 + 2 + 8 + 4;

    public int BodyByteLength => BodyHeaderSize + Width * 4 * 2;

    public int TotalByteLength => WireFormat.HeaderSize + BodyByteLength;

    public void Serialize(IBufferWriter<byte> writer, byte headerFlags = 1)
    {
        if (PanDb.Length != Width || WfDb.Length != Width)
            throw new InvalidOperationException("PanDb/WfDb must be Width floats long.");

        int total = TotalByteLength;
        var span = writer.GetSpan(total);

        WireFormat.WriteHeader(
            span,
            MsgType.DisplayFrame,
            headerFlags,
            checked((ushort)BodyByteLength),
            Seq,
            TsUnixMs);

        var body = span.Slice(WireFormat.HeaderSize, BodyByteLength);
        body[0] = RxId;
        body[1] = (byte)BodyFlags;
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(2, 2), Width);
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(4, 8), CenterHz);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(12, 4), HzPerPixel);

        int panBytes = Width * 4;
        MemoryMarshal.AsBytes(PanDb.Span).CopyTo(body.Slice(16, panBytes));
        MemoryMarshal.AsBytes(WfDb.Span).CopyTo(body.Slice(16 + panBytes, panBytes));

        writer.Advance(total);
    }

    public static DisplayFrame Deserialize(ReadOnlySpan<byte> bytes)
    {
        WireFormat.ReadHeader(bytes, out var msgType, out _, out var payloadLen, out var seq, out var ts);
        if (msgType != MsgType.DisplayFrame)
            throw new InvalidDataException($"expected DisplayFrame, got {msgType}");

        var body = bytes.Slice(WireFormat.HeaderSize, payloadLen);
        byte rxId = body[0];
        var flags = (DisplayBodyFlags)body[1];
        ushort width = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2, 2));
        long centerHz = BinaryPrimitives.ReadInt64LittleEndian(body.Slice(4, 8));
        float hzPerPixel = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(12, 4));

        int panBytes = width * 4;
        var panArr = new float[width];
        var wfArr = new float[width];
        body.Slice(16, panBytes).CopyTo(MemoryMarshal.AsBytes(panArr.AsSpan()));
        body.Slice(16 + panBytes, panBytes).CopyTo(MemoryMarshal.AsBytes(wfArr.AsSpan()));

        return new DisplayFrame(seq, ts, rxId, flags, width, centerHz, hzPerPixel, panArr, wfArr);
    }
}
