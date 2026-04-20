using System.Buffers;
using Zeus.Contracts;
using Xunit;

namespace Zeus.Contracts.Tests;

public class TxMetersFrameTests
{
    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var frame = new TxMetersFrame(
            FwdWatts: 4.75f,
            RefWatts: 0.08f,
            Swr: 1.32f,
            MicDbfs: -23.5f,
            EqPk: -18.2f,
            LvlrPk: -12.1f,
            AlcPk: -6.0f,
            AlcGr: 3.5f,
            OutPk: -2.0f);

        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);

        Assert.Equal(TxMetersFrame.ByteLength, writer.WrittenCount);
        Assert.Equal(37, writer.WrittenCount);

        var bytes = writer.WrittenSpan;
        Assert.Equal((byte)MsgType.TxMeters, bytes[0]);

        var decoded = TxMetersFrame.Deserialize(bytes);
        Assert.Equal(frame.FwdWatts, decoded.FwdWatts);
        Assert.Equal(frame.RefWatts, decoded.RefWatts);
        Assert.Equal(frame.Swr, decoded.Swr);
        Assert.Equal(frame.MicDbfs, decoded.MicDbfs);
        Assert.Equal(frame.EqPk, decoded.EqPk);
        Assert.Equal(frame.LvlrPk, decoded.LvlrPk);
        Assert.Equal(frame.AlcPk, decoded.AlcPk);
        Assert.Equal(frame.AlcGr, decoded.AlcGr);
        Assert.Equal(frame.OutPk, decoded.OutPk);
    }

    [Fact]
    public void Deserialize_RejectsWrongMsgType()
    {
        var bogus = new byte[TxMetersFrame.ByteLength];
        bogus[0] = (byte)MsgType.DisplayFrame; // 0x01, not 0x11
        Assert.Throws<InvalidDataException>(() => TxMetersFrame.Deserialize(bogus));
    }

    [Fact]
    public void Deserialize_RejectsTruncated()
    {
        var buf = new byte[TxMetersFrame.ByteLength - 1];
        buf[0] = (byte)MsgType.TxMeters;
        Assert.Throws<InvalidDataException>(() => TxMetersFrame.Deserialize(buf));
    }

    [Fact]
    public void Serialize_WritesLittleEndian()
    {
        // 1.0 f32 LE = 0x00 0x00 0x80 0x3F
        var frame = new TxMetersFrame(1.0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f);
        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);
        var bytes = writer.WrittenSpan;
        Assert.Equal(0x00, bytes[1]);
        Assert.Equal(0x00, bytes[2]);
        Assert.Equal(0x80, bytes[3]);
        Assert.Equal(0x3F, bytes[4]);
    }
}
