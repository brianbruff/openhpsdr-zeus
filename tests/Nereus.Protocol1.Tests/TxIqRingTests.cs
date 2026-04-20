namespace Nereus.Protocol1.Tests;

public class TxIqRingTests
{
    [Fact]
    public void Empty_ReturnsSilentIq()
    {
        var ring = new TxIqRing(capacityPairs: 16);
        for (int i = 0; i < 10; i++)
        {
            var (ii, qq) = ring.Next(1.0);
            Assert.Equal(0, ii);
            Assert.Equal(0, qq);
        }
        Assert.Equal(0, ring.TotalRead);
    }

    [Fact]
    public void WriteReadRoundtrip_PreservesS16Values()
    {
        var ring = new TxIqRing(capacityPairs: 32);
        // Four (I, Q) pairs at distinct amplitudes.
        var block = new float[] { 0.5f, -0.5f, 0.25f, 0.75f, -1.0f, 1.0f, 0.0f, 0.1f };
        ring.Write(block);
        Assert.Equal(4, ring.Count);

        (short i0, short q0) = ring.Next(1.0);
        (short i1, short q1) = ring.Next(1.0);
        (short i2, short q2) = ring.Next(1.0);
        (short i3, short q3) = ring.Next(1.0);

        Assert.Equal((short)Math.Round(0.5f * short.MaxValue), i0);
        Assert.Equal((short)Math.Round(-0.5f * short.MaxValue), q0);
        Assert.Equal((short)Math.Round(0.25f * short.MaxValue), i1);
        Assert.Equal((short)Math.Round(0.75f * short.MaxValue), q1);
        Assert.Equal(-short.MaxValue, i2);                // saturated at -1
        Assert.Equal(short.MaxValue, q2);                 // saturated at +1
        Assert.Equal(0, i3);
        Assert.Equal((short)Math.Round(0.1f * short.MaxValue), q3);
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void OverflowDropsOldest_NotNewest()
    {
        var ring = new TxIqRing(capacityPairs: 4);
        // Write 6 pairs → ring holds the last 4, first 2 are dropped.
        var block = new float[12];
        for (int k = 0; k < 6; k++)
        {
            block[2 * k] = (k + 1) / 10.0f;      // I = .1 .. .6
            block[2 * k + 1] = -(k + 1) / 10.0f; // Q = -.1 .. -.6
        }
        ring.Write(block);
        Assert.Equal(4, ring.Count);
        Assert.Equal(2, ring.Dropped);

        // Oldest valid sample should be the 3rd write (I=0.3).
        var (i0, _) = ring.Next(1.0);
        Assert.Equal((short)Math.Round(0.3f * short.MaxValue), i0);
    }

    [Fact]
    public void Saturation_ClampsOutOfRangeFloats()
    {
        var ring = new TxIqRing(capacityPairs: 4);
        ring.Write(new float[] { 2.5f, -3.0f });
        var (i, q) = ring.Next(1.0);
        Assert.Equal(short.MaxValue, i);
        Assert.Equal(-short.MaxValue, q);
    }

    [Fact]
    public void AmplitudeScale_IsApplied()
    {
        var ring = new TxIqRing(capacityPairs: 4);
        ring.Write(new float[] { 1.0f, 0.0f });
        var (i, q) = ring.Next(0.5);
        Assert.InRange(i, (short)(short.MaxValue * 0.49), (short)(short.MaxValue * 0.51));
        Assert.Equal(0, q);
    }

    [Fact]
    public void Clear_DrainsAllPendingPairs()
    {
        var ring = new TxIqRing(capacityPairs: 8);
        ring.Write(new float[] { 0.5f, 0.5f, 0.5f, 0.5f });
        Assert.Equal(2, ring.Count);
        ring.Clear();
        Assert.Equal(0, ring.Count);
        var (i, q) = ring.Next(1.0);
        Assert.Equal(0, i);
        Assert.Equal(0, q);
    }

    [Fact]
    public void Write_OddLength_Throws()
    {
        var ring = new TxIqRing(capacityPairs: 4);
        Assert.Throws<ArgumentException>(() => ring.Write(new float[] { 0f, 0f, 0f }));
    }
}
