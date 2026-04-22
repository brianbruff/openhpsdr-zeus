using System.Buffers;
using Zeus.Contracts;
using Xunit;

namespace Zeus.Contracts.Tests;

public class TxMetersV2FrameTests
{
    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var frame = new TxMetersV2Frame(
            FwdWatts: 4.75f,
            RefWatts: 0.08f,
            Swr: 1.32f,
            MicPk: -20.1f,
            MicAv: -23.5f,
            EqPk: -18.2f,
            EqAv: -21.4f,
            LvlrPk: -12.1f,
            LvlrAv: -14.8f,
            LvlrGr: 2.1f,
            CfcPk: -9.0f,
            CfcAv: -11.0f,
            CfcGr: 1.5f,
            CompPk: -7.5f,
            CompAv: -10.2f,
            AlcPk: -6.0f,
            AlcAv: -8.1f,
            AlcGr: 3.5f,
            OutPk: -2.0f,
            OutAv: -4.3f);

        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);

        Assert.Equal(TxMetersV2Frame.ByteLength, writer.WrittenCount);
        Assert.Equal(81, writer.WrittenCount);

        var bytes = writer.WrittenSpan;
        Assert.Equal((byte)MsgType.TxMetersV2, bytes[0]);

        var decoded = TxMetersV2Frame.Deserialize(bytes);
        Assert.Equal(frame.FwdWatts, decoded.FwdWatts);
        Assert.Equal(frame.RefWatts, decoded.RefWatts);
        Assert.Equal(frame.Swr, decoded.Swr);
        Assert.Equal(frame.MicPk, decoded.MicPk);
        Assert.Equal(frame.MicAv, decoded.MicAv);
        Assert.Equal(frame.EqPk, decoded.EqPk);
        Assert.Equal(frame.EqAv, decoded.EqAv);
        Assert.Equal(frame.LvlrPk, decoded.LvlrPk);
        Assert.Equal(frame.LvlrAv, decoded.LvlrAv);
        Assert.Equal(frame.LvlrGr, decoded.LvlrGr);
        Assert.Equal(frame.CfcPk, decoded.CfcPk);
        Assert.Equal(frame.CfcAv, decoded.CfcAv);
        Assert.Equal(frame.CfcGr, decoded.CfcGr);
        Assert.Equal(frame.CompPk, decoded.CompPk);
        Assert.Equal(frame.CompAv, decoded.CompAv);
        Assert.Equal(frame.AlcPk, decoded.AlcPk);
        Assert.Equal(frame.AlcAv, decoded.AlcAv);
        Assert.Equal(frame.AlcGr, decoded.AlcGr);
        Assert.Equal(frame.OutPk, decoded.OutPk);
        Assert.Equal(frame.OutAv, decoded.OutAv);
    }

    [Fact]
    public void Deserialize_RejectsWrongMsgType()
    {
        var bogus = new byte[TxMetersV2Frame.ByteLength];
        bogus[0] = (byte)MsgType.TxMeters; // 0x11, not 0x16 — should be rejected
        Assert.Throws<InvalidDataException>(() => TxMetersV2Frame.Deserialize(bogus));
    }

    [Fact]
    public void Deserialize_RejectsTruncated()
    {
        var buf = new byte[TxMetersV2Frame.ByteLength - 1];
        buf[0] = (byte)MsgType.TxMetersV2;
        Assert.Throws<InvalidDataException>(() => TxMetersV2Frame.Deserialize(buf));
    }

    [Fact]
    public void Serialize_WritesLittleEndian()
    {
        // 1.0 f32 LE = 0x00 0x00 0x80 0x3F — first float slot (FwdWatts)
        // lives at bytes 1..4.
        var frame = new TxMetersV2Frame(
            FwdWatts: 1.0f, RefWatts: 0f, Swr: 0f,
            MicPk: 0f, MicAv: 0f, EqPk: 0f, EqAv: 0f,
            LvlrPk: 0f, LvlrAv: 0f, LvlrGr: 0f,
            CfcPk: 0f, CfcAv: 0f, CfcGr: 0f,
            CompPk: 0f, CompAv: 0f,
            AlcPk: 0f, AlcAv: 0f, AlcGr: 0f,
            OutPk: 0f, OutAv: 0f);
        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);
        var bytes = writer.WrittenSpan;
        Assert.Equal(0x00, bytes[1]);
        Assert.Equal(0x00, bytes[2]);
        Assert.Equal(0x80, bytes[3]);
        Assert.Equal(0x3F, bytes[4]);
    }

    [Fact]
    public void ByteLength_Is81()
    {
        // Sanity check so a future field insertion without updating
        // ByteLength fails the test suite immediately: 1 type byte +
        // 20 × f32 = 81 bytes.
        Assert.Equal(81, TxMetersV2Frame.ByteLength);
    }
}
