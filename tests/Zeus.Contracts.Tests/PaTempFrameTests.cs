using System.Buffers;
using Zeus.Contracts;
using Xunit;

namespace Zeus.Contracts.Tests;

public class PaTempFrameTests
{
    [Fact]
    public void RoundTrip_PreservesTempC()
    {
        var frame = new PaTempFrame(47.25f);

        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);

        Assert.Equal(PaTempFrame.ByteLength, writer.WrittenCount);
        Assert.Equal(5, writer.WrittenCount);

        var bytes = writer.WrittenSpan;
        Assert.Equal((byte)MsgType.PaTemp, bytes[0]);

        var decoded = PaTempFrame.Deserialize(bytes);
        Assert.Equal(frame.TempC, decoded.TempC);
    }

    [Fact]
    public void Deserialize_RejectsWrongMsgType()
    {
        var bogus = new byte[PaTempFrame.ByteLength];
        bogus[0] = (byte)MsgType.TxMetersV2; // 0x16, not 0x17
        Assert.Throws<InvalidDataException>(() => PaTempFrame.Deserialize(bogus));
    }

    [Fact]
    public void Deserialize_RejectsTruncated()
    {
        var buf = new byte[PaTempFrame.ByteLength - 1];
        buf[0] = (byte)MsgType.PaTemp;
        Assert.Throws<InvalidDataException>(() => PaTempFrame.Deserialize(buf));
    }

    [Fact]
    public void Serialize_WritesLittleEndian()
    {
        // 1.0 f32 LE = 0x00 0x00 0x80 0x3F
        var frame = new PaTempFrame(1.0f);
        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);
        var bytes = writer.WrittenSpan;
        Assert.Equal(0x00, bytes[1]);
        Assert.Equal(0x00, bytes[2]);
        Assert.Equal(0x80, bytes[3]);
        Assert.Equal(0x3F, bytes[4]);
    }

    [Fact]
    public void ByteLength_Is5()
    {
        Assert.Equal(5, PaTempFrame.ByteLength);
    }
}
