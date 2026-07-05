// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Extensions.Configuration;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class DisplayPerformanceOptionsTests
{
    [Fact]
    public void Resolve_DefaultsToNormalThirtyFps()
    {
        var options = DisplayPerformanceOptions.Resolve(environment: _ => null);

        Assert.Equal("normal", options.Profile);
        Assert.Equal(30.0, options.MaxFrameRateHz);
        Assert.False(options.LowPower);
        Assert.False(options.PreferWebglWaterfall);
    }

    [Fact]
    public void Resolve_LowPowerProfileCapsDisplayAtFifteenFps()
    {
        var options = DisplayPerformanceOptions.Resolve(environment: key =>
            key == "ZEUS_DISPLAY_PROFILE" ? "low-power" : null);

        Assert.Equal("low-power", options.Profile);
        Assert.Equal(15.0, options.MaxFrameRateHz);
        Assert.True(options.LowPower);
        Assert.True(options.PreferWebglWaterfall);
    }

    [Fact]
    public void Resolve_ExplicitEnvironmentFpsWinsOverProfile()
    {
        var options = DisplayPerformanceOptions.Resolve(environment: key => key switch
        {
            "ZEUS_DISPLAY_PROFILE" => "low-power",
            "ZEUS_DISPLAY_MAX_FPS" => "10",
            _ => null,
        });

        Assert.Equal("custom", options.Profile);
        Assert.Equal(10.0, options.MaxFrameRateHz);
        Assert.True(options.LowPower);
        Assert.True(options.PreferWebglWaterfall);
    }

    [Fact]
    public void Resolve_ExplicitHighEnvironmentFpsReportsCustom()
    {
        var options = DisplayPerformanceOptions.Resolve(environment: key =>
            key == "ZEUS_DISPLAY_MAX_FPS" ? "120" : null);

        Assert.Equal("custom", options.Profile);
        Assert.Equal(120.0, options.MaxFrameRateHz);
        Assert.False(options.LowPower);
        Assert.False(options.PreferWebglWaterfall);
    }

    [Fact]
    public void Resolve_ConfigurationFpsIsAcceptedWhenEnvironmentIsUnset()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Zeus:Display:MaxFps"] = "12.5",
            })
            .Build();

        var options = DisplayPerformanceOptions.Resolve(configuration, environment: _ => null);

        Assert.Equal("custom", options.Profile);
        Assert.Equal(12.5, options.MaxFrameRateHz);
    }

    [Theory]
    [InlineData("0", 1.0)]
    [InlineData("60", 60.0)]
    [InlineData("1000", 640.0)]
    public void TryParseFrameRate_ClampsToSupportedRange(string raw, double expected)
    {
        Assert.True(DisplayPerformanceOptions.TryParseFrameRate(raw, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null, DisplayPerformanceOptions.DefaultDisplayDecimation)]
    [InlineData(0, DisplayPerformanceOptions.MinDisplayDecimation)]
    [InlineData(8, 8)]
    [InlineData(99, DisplayPerformanceOptions.MaxDisplayDecimation)]
    public void NormalizeDisplayDecimation_ClampsToSupportedRange(int? raw, int expected)
    {
        Assert.Equal(expected, DisplayPerformanceOptions.NormalizeDisplayDecimation(raw));
    }

    [Theory]
    [InlineData(null, DisplayPerformanceOptions.DefaultWaterfallUpdatePeriod)]
    [InlineData(0, DisplayPerformanceOptions.MinWaterfallUpdatePeriod)]
    [InlineData(4, 4)]
    [InlineData(5000, DisplayPerformanceOptions.MaxWaterfallUpdatePeriod)]
    public void NormalizeWaterfallUpdatePeriod_ClampsToSupportedRange(int? raw, int expected)
    {
        Assert.Equal(expected, DisplayPerformanceOptions.NormalizeWaterfallUpdatePeriod(raw));
    }
}
