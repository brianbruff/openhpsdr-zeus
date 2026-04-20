using System.Buffers;
using Nereus.Contracts;
using Xunit;

namespace Nereus.Contracts.Tests;

public class DisplayFrameTests
{
    [Theory]
    [InlineData(64)]
    [InlineData(2048)]
    public void RoundTrip_PreservesAllFields(int width)
    {
        var pan = new float[width];
        var wf = new float[width];
        for (int i = 0; i < width; i++)
        {
            pan[i] = -90f + i * 0.25f;
            wf[i] = -80f - i * 0.125f;
        }

        var frame = new DisplayFrame(
            Seq: 42,
            TsUnixMs: 1_700_000_000_123.5,
            RxId: 0,
            BodyFlags: DisplayBodyFlags.PanValid | DisplayBodyFlags.WfValid,
            Width: (ushort)width,
            CenterHz: 14_200_000,
            HzPerPixel: 192_000f / width,
            PanDb: pan,
            WfDb: wf);

        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);
        Assert.Equal(frame.TotalByteLength, writer.WrittenCount);

        int expectedBody = 1 + 1 + 2 + 8 + 4 + width * 4 * 2;
        WireFormat.ReadHeader(writer.WrittenSpan, out var mt, out _, out var payloadLen, out var seq, out var ts);
        Assert.Equal(MsgType.DisplayFrame, mt);
        Assert.Equal(expectedBody, payloadLen);
        Assert.Equal(42u, seq);
        Assert.Equal(1_700_000_000_123.5, ts);

        var decoded = DisplayFrame.Deserialize(writer.WrittenSpan);
        Assert.Equal(frame.Seq, decoded.Seq);
        Assert.Equal(frame.TsUnixMs, decoded.TsUnixMs);
        Assert.Equal(frame.RxId, decoded.RxId);
        Assert.Equal(frame.BodyFlags, decoded.BodyFlags);
        Assert.Equal(frame.Width, decoded.Width);
        Assert.Equal(frame.CenterHz, decoded.CenterHz);
        Assert.Equal(frame.HzPerPixel, decoded.HzPerPixel);
        Assert.Equal(pan, decoded.PanDb.ToArray());
        Assert.Equal(wf, decoded.WfDb.ToArray());
    }

    [Fact]
    public void WireFormat_IsLittleEndian()
    {
        Span<byte> buf = stackalloc byte[WireFormat.HeaderSize];
        WireFormat.WriteHeader(buf, MsgType.DisplayFrame, 0x01, 0x1234, 0xAABBCCDD, 0.0);
        Assert.Equal(0x01, buf[0]);
        Assert.Equal(0x01, buf[1]);
        Assert.Equal(0x34, buf[2]);
        Assert.Equal(0x12, buf[3]);
        Assert.Equal(0xDD, buf[4]);
        Assert.Equal(0xCC, buf[5]);
        Assert.Equal(0xBB, buf[6]);
        Assert.Equal(0xAA, buf[7]);
    }
}
