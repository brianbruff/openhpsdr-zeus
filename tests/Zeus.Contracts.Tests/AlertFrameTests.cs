using Zeus.Contracts;
using Xunit;

namespace Zeus.Contracts.Tests;

public class AlertFrameTests
{
    [Fact]
    public void SerializeDeserialize_RoundTrip()
    {
        var frame = new AlertFrame(AlertKind.SwrTrip, "SWR 3.0:1 sustained >500 ms");
        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        frame.Serialize(writer);
        var bytes = writer.WrittenSpan;

        Assert.Equal((byte)MsgType.Alert, bytes[0]);
        Assert.Equal((byte)AlertKind.SwrTrip, bytes[1]);

        var decoded = AlertFrame.Deserialize(bytes);
        Assert.Equal(AlertKind.SwrTrip, decoded.Kind);
        Assert.Equal("SWR 3.0:1 sustained >500 ms", decoded.Message);
    }

    [Fact]
    public void Serialize_WritesCorrectMsgType()
    {
        var frame = new AlertFrame(AlertKind.SwrTrip, "test");
        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        frame.Serialize(writer);
        Assert.Equal((byte)MsgType.Alert, writer.WrittenSpan[0]);
    }

    [Fact]
    public void Deserialize_RejectsWrongMsgType()
    {
        var bytes = new byte[] { (byte)MsgType.TxMeters, 0, 0x74, 0x65, 0x73, 0x74 }; // "test"
        Assert.Throws<InvalidDataException>(() => AlertFrame.Deserialize(bytes));
    }

    [Fact]
    public void Deserialize_RequiresAtLeast2Bytes()
    {
        var bytes = new byte[] { (byte)MsgType.Alert };
        Assert.Throws<InvalidDataException>(() => AlertFrame.Deserialize(bytes));
    }

    [Fact]
    public void Serialize_EmptyMessage()
    {
        var frame = new AlertFrame(AlertKind.SwrTrip, "");
        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        frame.Serialize(writer);
        var bytes = writer.WrittenSpan;

        Assert.Equal(2, bytes.Length); // type + kind only
        var decoded = AlertFrame.Deserialize(bytes);
        Assert.Equal(string.Empty, decoded.Message);
    }

    [Fact]
    public void Serialize_Utf8EncodedMessage()
    {
        var frame = new AlertFrame(AlertKind.SwrTrip, "SWR 3.0:1 — dropped TX");
        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        frame.Serialize(writer);
        var bytes = writer.WrittenSpan;

        var decoded = AlertFrame.Deserialize(bytes);
        Assert.Equal("SWR 3.0:1 — dropped TX", decoded.Message);
    }
}
