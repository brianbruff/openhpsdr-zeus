// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// Pins the inline display-tick throttle (#960 G2E bench follow-up). The
/// display cadence is paced by whichever RX-thread frames arrive — user-RX IQ
/// frames normally, and PS-feedback frames during a single-ADC (HermesC10 /
/// ANAN-G2E) keyed PureSignal burst, when the board's only DDC is diverted to
/// feedback and no IQ frame arrives at all. Both producers funnel through
/// <see cref="DspPipelineService.ShouldTickInline"/>, so this predicate is
/// what guarantees (a) the panadapter/waterfall keep updating during a G2E PS
/// transmission instead of freezing, and (b) dual-ADC boards — where IQ and
/// feedback frames flow simultaneously — still tick only once per configured
/// interval, not once per producer.
/// </summary>
public sealed class DspPipelineDisplayTickTests
{
    private const long Period = 400; // arbitrary units; contract is relative

    [Fact]
    public void FirstCall_AlwaysTicks()
    {
        // last == 0 is the "never ticked" sentinel — the first frame after
        // sink attach must render immediately, whatever the clock reads.
        Assert.True(DspPipelineService.ShouldTickInline(
            nowTicks: 12345, lastTicks: 0, periodTicks: Period));
    }

    [Fact]
    public void WithinPeriod_DoesNotTick()
    {
        // A second producer (e.g. a PS-feedback frame landing right after an
        // IQ frame on a dual-ADC board) must not double the display rate.
        Assert.False(DspPipelineService.ShouldTickInline(
            nowTicks: 1000 + Period - 1, lastTicks: 1000, periodTicks: Period));
    }

    [Fact]
    public void AtOrPastPeriod_Ticks()
    {
        Assert.True(DspPipelineService.ShouldTickInline(
            nowTicks: 1000 + Period, lastTicks: 1000, periodTicks: Period));
        Assert.True(DspPipelineService.ShouldTickInline(
            nowTicks: 1000 + Period + 1, lastTicks: 1000, periodTicks: Period));
    }

    [Fact]
    public void SoleProducer_TicksEveryPeriod_LikeTheG2eBurst()
    {
        // Simulate the G2E keyed-burst regime: PS-feedback frames are the ONLY
        // pacer, arriving faster than the display period. Exactly one tick per
        // period must fire — a live display, throttled to cadence.
        long last = 0;
        int ticks = 0;
        for (long now = 100; now <= 100 + Period * 10; now += Period / 4)
        {
            if (DspPipelineService.ShouldTickInline(now, last, Period))
            {
                last = now;
                ticks++;
            }
        }

        Assert.Equal(11, ticks); // first immediate tick + one per full period
    }

    [Fact]
    public void AttachedSinkTimer_DoesNotStealFreshInlineTicks()
    {
        Assert.False(DspPipelineService.ShouldTimerTickWhenSinkAttached(
            nowTicks: 1000 + Period,
            lastInlineTicks: 1000 + Period - 1,
            lastAnyTickTicks: 1000,
            periodTicks: Period));
    }

    [Fact]
    public void AttachedSinkTimer_TakesOverWhenInlineTicksStall()
    {
        Assert.True(DspPipelineService.ShouldTimerTickWhenSinkAttached(
            nowTicks: 1000 + Period * 2,
            lastInlineTicks: 1000,
            lastAnyTickTicks: 1000,
            periodTicks: Period));
    }

    [Fact]
    public void AttachedSinkTimer_KeepsFallbackCadenceAfterTakeover()
    {
        // Once the inline pacer is stale, lastAnyTickTicks controls fallback
        // cadence so the timer can keep draining preview audio at the normal
        // tick rate until RX packets resume.
        Assert.False(DspPipelineService.ShouldTimerTickWhenSinkAttached(
            nowTicks: 1000 + Period * 3 - 1,
            lastInlineTicks: 1000,
            lastAnyTickTicks: 1000 + Period * 2,
            periodTicks: Period));
        Assert.True(DspPipelineService.ShouldTimerTickWhenSinkAttached(
            nowTicks: 1000 + Period * 3,
            lastInlineTicks: 1000,
            lastAnyTickTicks: 1000 + Period * 2,
            periodTicks: Period));
    }

    [Fact]
    public void InlineDisplayPeriod_UsesDisplayFpsAboveDefault()
    {
        Assert.Equal(Period, DspPipelineService.InlineDisplayPeriodTicks(
            displayMaxFrameRateHz: 30,
            defaultPeriodTicks: Period,
            stopwatchFrequency: 1200));

        Assert.Equal(10, DspPipelineService.InlineDisplayPeriodTicks(
            displayMaxFrameRateHz: 120,
            defaultPeriodTicks: Period,
            stopwatchFrequency: 1200));
    }

    [Fact]
    public void DownsampleDisplayBins_UsesMaxPerDecimatedBucket()
    {
        var source = new float[] { -100, -80, -95, -70, -120, -110, -90, -60 };
        var dest = new float[8];

        var width = DspPipelineService.DownsampleDisplayBins(source, dest, displayDecimation: 2);

        Assert.Equal(4, width);
        Assert.Equal(new[] { -80f, -70f, -110f, -60f }, dest.AsSpan(0, width).ToArray());
    }

    [Fact]
    public void DecimatedDisplayWidth_ClampsFactorAndNeverReturnsZero()
    {
        Assert.Equal(2048, DspPipelineService.DecimatedDisplayWidth(2048, 1));
        Assert.Equal(128, DspPipelineService.DecimatedDisplayWidth(2048, 16));
        Assert.Equal(1, DspPipelineService.DecimatedDisplayWidth(8, 99));
    }
}
