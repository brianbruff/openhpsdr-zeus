using Xunit;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class CwOffsetTests
{
    [Theory]
    [InlineData(RxMode.USB, 14_046_000L, 14_046_000L)]
    [InlineData(RxMode.LSB, 14_046_000L, 14_046_000L)]
    [InlineData(RxMode.AM,  14_046_000L, 14_046_000L)]
    [InlineData(RxMode.FM,  14_046_000L, 14_046_000L)]
    [InlineData(RxMode.SAM, 14_046_000L, 14_046_000L)]
    [InlineData(RxMode.DSB, 14_046_000L, 14_046_000L)]
    [InlineData(RxMode.DIGU, 14_046_000L, 14_046_000L)]
    [InlineData(RxMode.DIGL, 14_046_000L, 14_046_000L)]
    [InlineData(RxMode.CWU, 14_046_000L, 14_045_400L)] // dial − 600
    [InlineData(RxMode.CWL, 14_046_000L, 14_046_600L)] // dial + 600
    public void EffectiveLoHz_AppliesPitchOnlyForCw(RxMode mode, long vfoHz, long expected)
    {
        Assert.Equal(expected, CwOffset.EffectiveLoHz(mode, vfoHz));
    }

    // Thetis console.cs:34203-34298 — the dial absorbs the cw_pitch step on
    // SSB↔CW transitions so the operator stays on the same physical signal.
    [Theory]
    [InlineData(RxMode.USB, RxMode.CWU, +600)]
    [InlineData(RxMode.LSB, RxMode.CWU, -600)]
    [InlineData(RxMode.CWL, RxMode.CWU, 0)]
    [InlineData(RxMode.AM,  RxMode.CWU, +600)]
    [InlineData(RxMode.FM,  RxMode.CWU, +600)]
    [InlineData(RxMode.DIGU, RxMode.CWU, +600)]
    [InlineData(RxMode.USB, RxMode.CWL, +600)]
    [InlineData(RxMode.LSB, RxMode.CWL, -600)]
    [InlineData(RxMode.CWU, RxMode.CWL, 0)]
    [InlineData(RxMode.CWU, RxMode.USB, -600)]
    [InlineData(RxMode.CWU, RxMode.LSB, +600)]
    [InlineData(RxMode.CWU, RxMode.AM,  -600)]
    [InlineData(RxMode.CWL, RxMode.LSB, +600)]
    [InlineData(RxMode.CWL, RxMode.USB, -600)]
    [InlineData(RxMode.CWL, RxMode.AM,  +600)]
    public void DialBumpForModeTransition_MatchesThetis(RxMode oldMode, RxMode newMode, long expected)
    {
        Assert.Equal(expected, CwOffset.DialBumpForModeTransition(oldMode, newMode));
    }

    [Theory]
    [InlineData(RxMode.USB, RxMode.LSB)] // SSB↔SSB
    [InlineData(RxMode.USB, RxMode.AM)]
    [InlineData(RxMode.AM,  RxMode.FM)]
    [InlineData(RxMode.DIGU, RxMode.DIGL)]
    [InlineData(RxMode.USB, RxMode.USB)] // same mode
    [InlineData(RxMode.CWU, RxMode.CWU)]
    public void DialBumpForModeTransition_NonCwOrSameMode_NoBump(RxMode oldMode, RxMode newMode)
    {
        Assert.Equal(0, CwOffset.DialBumpForModeTransition(oldMode, newMode));
    }

    // Round-trip safety: USB → CWU → USB returns the dial to its original
    // value, so a casual operator flipping modes doesn't drift their tuning.
    [Theory]
    [InlineData(RxMode.USB, RxMode.CWU)]
    [InlineData(RxMode.LSB, RxMode.CWU)]
    [InlineData(RxMode.USB, RxMode.CWL)]
    [InlineData(RxMode.LSB, RxMode.CWL)]
    [InlineData(RxMode.AM,  RxMode.CWU)]
    [InlineData(RxMode.AM,  RxMode.CWL)]
    public void DialBumpForModeTransition_IsInvertedOnReturn(RxMode ssbMode, RxMode cwMode)
    {
        long forward = CwOffset.DialBumpForModeTransition(ssbMode, cwMode);
        long back = CwOffset.DialBumpForModeTransition(cwMode, ssbMode);
        Assert.Equal(0, forward + back);
    }
}
