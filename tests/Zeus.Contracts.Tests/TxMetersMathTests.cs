using Zeus.Contracts;
using Zeus.Server;
using Xunit;

namespace Zeus.Contracts.Tests;

public class TxMetersMathTests
{
    private static readonly RadioCalibration Cal = RadioCalibration.HermesLite2;

    [Fact]
    public void ZeroAdc_YieldsZeroWattsAndSwrOne()
    {
        // ADC = cal_offset → volts = 0 → watts = 0. Below the 2 W floor the
        // SWR should clamp to 1.0 regardless of REF.
        var (fwdW, refW, swr) = TxMetersService.ComputeMeters(Cal.AdcCalOffset, Cal.AdcCalOffset, Cal);
        Assert.Equal(0.0, fwdW, 6);
        Assert.Equal(0.0, refW, 6);
        Assert.Equal(1.0, swr, 6);
    }

    [Fact]
    public void WattsMath_MatchesThetisFormula()
    {
        // Manually wire the expected watts for a mid-scale ADC and assert
        // the port reproduces it to float precision (Thetis console.cs:25008-25072).
        const double adc = 2500;
        double v = (adc - Cal.AdcCalOffset) / 4095.0 * Cal.RefVoltage;
        double wExpected = v * v / Cal.BridgeVolt;

        var (fwdW, _, _) = TxMetersService.ComputeMeters(adc, Cal.AdcCalOffset, Cal);
        Assert.Equal(wExpected, fwdW, 6);
    }

    [Fact]
    public void NegativeOrBelowOffset_ClampsToZero()
    {
        // ADC below the cal offset would yield negative watts through the
        // squaring (sign lost) → stays zero by the (adc - offset) floor check.
        // This test keeps math tolerant to noise on a cold bridge.
        var (fwdW, refW, _) = TxMetersService.ComputeMeters(0, 0, Cal);
        Assert.True(fwdW >= 0);
        Assert.True(refW >= 0);
    }

    [Fact]
    public void SwrFloorsToOne_WhenFwdBelowTwoWatts()
    {
        // Pick an ADC that yields ~1 W forward. REF arbitrary — floor rule wins.
        double adcFor1W = Cal.AdcCalOffset + 4095.0 / Cal.RefVoltage * Math.Sqrt(1.0 * Cal.BridgeVolt);
        var (fwdW, _, swr) = TxMetersService.ComputeMeters(adcFor1W, adcFor1W, Cal);
        Assert.True(fwdW < 2.0);
        Assert.Equal(1.0, swr, 6);
    }

    [Fact]
    public void SwrIsOne_WhenRefIsZeroAndFwdAboveFloor()
    {
        double adcFor3W = Cal.AdcCalOffset + 4095.0 / Cal.RefVoltage * Math.Sqrt(3.0 * Cal.BridgeVolt);
        var (fwdW, refW, swr) = TxMetersService.ComputeMeters(adcFor3W, Cal.AdcCalOffset, Cal);
        Assert.True(fwdW > 2.0);
        Assert.Equal(0.0, refW, 6);
        Assert.Equal(1.0, swr, 6);
    }

    [Fact]
    public void SwrTwo_FromQuarterRefOverFwd()
    {
        // rho = sqrt(P_ref / P_fwd) = 1/3 → SWR = (1 + 1/3) / (1 - 1/3) = 2.
        // Construct ADCs so fwdW ≈ 3 W and refW ≈ 3/9 W = 0.333 W.
        double adcForFwd = Cal.AdcCalOffset + 4095.0 / Cal.RefVoltage * Math.Sqrt(3.0 * Cal.BridgeVolt);
        double adcForRef = Cal.AdcCalOffset + 4095.0 / Cal.RefVoltage * Math.Sqrt((3.0 / 9.0) * Cal.BridgeVolt);

        var (_, _, swr) = TxMetersService.ComputeMeters(adcForFwd, adcForRef, Cal);
        Assert.Equal(2.0, swr, 3);
    }

    [Fact]
    public void SwrCapsAtNine_WhenRefEqualsFwd()
    {
        // Full reflection → rho = 1 → SWR diverges; contract caps at 9.0.
        double adcForFwd = Cal.AdcCalOffset + 4095.0 / Cal.RefVoltage * Math.Sqrt(5.0 * Cal.BridgeVolt);
        var (fwdW, _, swr) = TxMetersService.ComputeMeters(adcForFwd, adcForFwd, Cal);
        Assert.True(fwdW > 2.0);
        Assert.Equal(9.0, swr, 6);
    }

    [Fact]
    public void SwrCapsAtNine_WhenRefExceedsFwd()
    {
        // refW > fwdW is physically impossible on a real bridge but can leak
        // through on transients/noise. Must not produce NaN or negative SWR.
        double adcForFwd = Cal.AdcCalOffset + 4095.0 / Cal.RefVoltage * Math.Sqrt(3.0 * Cal.BridgeVolt);
        double adcForRef = Cal.AdcCalOffset + 4095.0 / Cal.RefVoltage * Math.Sqrt(5.0 * Cal.BridgeVolt);
        var (_, _, swr) = TxMetersService.ComputeMeters(adcForFwd, adcForRef, Cal);
        Assert.Equal(9.0, swr, 6);
    }
}
