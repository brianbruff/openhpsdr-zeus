using Zeus.Dsp;
using Xunit;

namespace Zeus.Dsp.Tests;

public class SyntheticDspEngineTests
{
    [Fact]
    public void OpenChannel_ReturnsIds_AndPixoutsHaveExpectedShape()
    {
        using var eng = new SyntheticDspEngine();
        int id = eng.OpenChannel(192_000, 256);
        Assert.True(id > 0);

        var pan = new float[256];
        Assert.True(eng.TryGetDisplayPixels(id, DisplayPixout.Panadapter, pan));
        Assert.True(pan.Length == 256);
        Assert.Contains(pan, v => v > -60f);
    }

    [Fact]
    public void Panadapter_PeakColumnAdvancesOverTime()
    {
        using var eng = new SyntheticDspEngine();
        int id = eng.OpenChannel(192_000, 256);

        var pan = new float[256];
        eng.TryGetDisplayPixels(id, DisplayPixout.Panadapter, pan);
        int first = ArgMax(pan);

        Thread.Sleep(120);

        eng.TryGetDisplayPixels(id, DisplayPixout.Panadapter, pan);
        int second = ArgMax(pan);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void TryGetDisplayPixels_ReturnsFalseForUnknownChannel()
    {
        using var eng = new SyntheticDspEngine();
        Assert.False(eng.TryGetDisplayPixels(42, DisplayPixout.Panadapter, new float[256]));
    }

    [Fact]
    public void OpenTxChannel_ReturnsNegativeOne_AndSetMoxIsNoOp()
    {
        using var eng = new SyntheticDspEngine();
        Assert.Equal(-1, eng.OpenTxChannel());
        eng.SetMox(true);
        eng.SetMox(false);
    }

    private static int ArgMax(ReadOnlySpan<float> s)
    {
        int best = 0;
        float bestVal = float.NegativeInfinity;
        for (int i = 0; i < s.Length; i++) if (s[i] > bestVal) { bestVal = s[i]; best = i; }
        return best;
    }
}
