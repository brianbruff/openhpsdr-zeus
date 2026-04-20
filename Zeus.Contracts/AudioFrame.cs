using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Zeus.Contracts;

public readonly record struct AudioFrame(
    uint Seq,
    double TsUnixMs,
    byte RxId,
    byte Channels,
    uint SampleRateHz,
    ushort SampleCount,
    ReadOnlyMemory<float> Samples)
{
    public const int BodyHeaderSize = 1 + 1 + 4 + 2;

    public int BodyByteLength => BodyHeaderSize + SampleCount * Channels * 4;

    public int TotalByteLength => WireFormat.HeaderSize + BodyByteLength;

    public void Serialize(IBufferWriter<byte> writer, byte headerFlags = 0)
    {
        if (Channels == 0) throw new InvalidOperationException("Channels must be >= 1.");
        if (Samples.Length != SampleCount * Channels)
            throw new InvalidOperationException("Samples length must equal SampleCount * Channels.");

        int total = TotalByteLength;
        var span = writer.GetSpan(total);

        WireFormat.WriteHeader(
            span,
            MsgType.AudioPcm,
            headerFlags,
            checked((ushort)BodyByteLength),
            Seq,
            TsUnixMs);

        var body = span.Slice(WireFormat.HeaderSize, BodyByteLength);
        body[0] = RxId;
        body[1] = Channels;
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(2, 4), SampleRateHz);
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(6, 2), SampleCount);

        int sampleBytes = SampleCount * Channels * 4;
        MemoryMarshal.AsBytes(Samples.Span).CopyTo(body.Slice(BodyHeaderSize, sampleBytes));

        writer.Advance(total);
    }

    public static AudioFrame Deserialize(ReadOnlySpan<byte> bytes)
    {
        WireFormat.ReadHeader(bytes, out var msgType, out _, out var payloadLen, out var seq, out var ts);
        if (msgType != MsgType.AudioPcm)
            throw new InvalidDataException($"expected AudioPcm, got {msgType}");

        var body = bytes.Slice(WireFormat.HeaderSize, payloadLen);
        byte rxId = body[0];
        byte channels = body[1];
        uint sampleRateHz = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(2, 4));
        ushort sampleCount = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(6, 2));

        int sampleFloats = sampleCount * channels;
        var samples = new float[sampleFloats];
        int sampleBytes = sampleFloats * 4;
        body.Slice(BodyHeaderSize, sampleBytes).CopyTo(MemoryMarshal.AsBytes(samples.AsSpan()));

        return new AudioFrame(seq, ts, rxId, channels, sampleRateHz, sampleCount, samples);
    }
}
