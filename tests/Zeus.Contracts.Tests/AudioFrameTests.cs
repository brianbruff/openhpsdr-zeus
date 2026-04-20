using System.Buffers;
using Nereus.Contracts;
using Xunit;

namespace Nereus.Contracts.Tests;

public class AudioFrameTests
{
    [Theory]
    [InlineData(1, 256)]
    [InlineData(1, 2048)]
    [InlineData(2, 1024)]
    public void RoundTrip_PreservesAllFields(int channels, int sampleCount)
    {
        int total = channels * sampleCount;
        var samples = new float[total];
        for (int i = 0; i < total; i++)
            samples[i] = MathF.Sin(i * 0.01f) * 0.5f;

        var frame = new AudioFrame(
            Seq: 7,
            TsUnixMs: 1_700_000_000_456.25,
            RxId: 0,
            Channels: (byte)channels,
            SampleRateHz: 48_000u,
            SampleCount: (ushort)sampleCount,
            Samples: samples);

        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);
        Assert.Equal(frame.TotalByteLength, writer.WrittenCount);

        int expectedBody = 1 + 1 + 4 + 2 + total * 4;
        WireFormat.ReadHeader(writer.WrittenSpan, out var mt, out _, out var payloadLen, out var seq, out var ts);
        Assert.Equal(MsgType.AudioPcm, mt);
        Assert.Equal(expectedBody, payloadLen);
        Assert.Equal(7u, seq);
        Assert.Equal(1_700_000_000_456.25, ts);

        var decoded = AudioFrame.Deserialize(writer.WrittenSpan);
        Assert.Equal(frame.Seq, decoded.Seq);
        Assert.Equal(frame.TsUnixMs, decoded.TsUnixMs);
        Assert.Equal(frame.RxId, decoded.RxId);
        Assert.Equal(frame.Channels, decoded.Channels);
        Assert.Equal(frame.SampleRateHz, decoded.SampleRateHz);
        Assert.Equal(frame.SampleCount, decoded.SampleCount);
        Assert.Equal(samples, decoded.Samples.ToArray());
    }

    [Fact]
    public void Serialize_RejectsLengthMismatch()
    {
        var frame = new AudioFrame(
            Seq: 1,
            TsUnixMs: 0,
            RxId: 0,
            Channels: 1,
            SampleRateHz: 48_000u,
            SampleCount: 10,
            Samples: new float[9]);

        var writer = new ArrayBufferWriter<byte>();
        Assert.Throws<InvalidOperationException>(() => frame.Serialize(writer));
    }

    [Fact]
    public void MsgType_IsAudioPcm_InHeader()
    {
        var frame = new AudioFrame(
            Seq: 1,
            TsUnixMs: 0,
            RxId: 0,
            Channels: 1,
            SampleRateHz: 48_000u,
            SampleCount: 4,
            Samples: new float[4]);
        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);
        Assert.Equal((byte)MsgType.AudioPcm, writer.WrittenSpan[0]);
    }
}
