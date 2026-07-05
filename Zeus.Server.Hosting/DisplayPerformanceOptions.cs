// SPDX-License-Identifier: GPL-2.0-or-later
//
// Runtime display-performance defaults and normalization bounds. Startup
// options provide the initial cap; persisted operator preferences can tune the
// same display path without rebuilding.

using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Zeus.Server;

public sealed record DisplayPerformanceSnapshot(
    string Profile,
    double MaxFrameRateHz,
    bool LowPower,
    bool PreferWebglWaterfall);

public static class DisplayPerformanceOptions
{
    public const double DefaultFrameRateHz = 30.0;
    public const double LowPowerFrameRateHz = 15.0;
    public const double MinFrameRateHz = 1.0;
    public const double MaxFrameRateHz = 640.0;
    public const int DefaultDisplayDecimation = 1;
    public const int MinDisplayDecimation = 1;
    public const int MaxDisplayDecimation = 16;
    public const int DefaultWaterfallUpdatePeriod = 1;
    public const int MinWaterfallUpdatePeriod = 1;
    public const int MaxWaterfallUpdatePeriod = 1000;

    public static DisplayPerformanceSnapshot Resolve(
        IConfiguration? configuration = null,
        Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;

        if (TryParseFrameRate(environment("ZEUS_DISPLAY_MAX_FPS"), out var envFps) ||
            TryParseFrameRate(configuration?["Zeus:Display:MaxFps"], out envFps) ||
            TryParseFrameRate(configuration?["Zeus:Display:FrameRateHz"], out envFps))
        {
            return new DisplayPerformanceSnapshot(
                Profile: Math.Abs(envFps - DefaultFrameRateHz) < 0.0001 ? "normal" : "custom",
                MaxFrameRateHz: envFps,
                LowPower: envFps < DefaultFrameRateHz,
                PreferWebglWaterfall: envFps < DefaultFrameRateHz);
        }

        var profile = FirstNonBlank(
            environment("ZEUS_DISPLAY_PROFILE"),
            configuration?["Zeus:Display:Profile"]);
        var lowPower =
            IsLowPowerProfile(profile) ||
            IsTruthy(environment("ZEUS_LOW_POWER_DISPLAY")) ||
            IsTruthy(configuration?["Zeus:Display:LowPower"]);

        if (lowPower)
        {
            return new DisplayPerformanceSnapshot(
                Profile: "low-power",
                MaxFrameRateHz: LowPowerFrameRateHz,
                LowPower: true,
                PreferWebglWaterfall: true);
        }

        return new DisplayPerformanceSnapshot(
            Profile: "normal",
            MaxFrameRateHz: DefaultFrameRateHz,
            LowPower: false,
            PreferWebglWaterfall: false);
    }

    public static bool TryParseFrameRate(string? raw, out double frameRateHz)
    {
        frameRateHz = DefaultFrameRateHz;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (!double.TryParse(
                raw.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed) ||
            !double.IsFinite(parsed))
        {
            return false;
        }

        frameRateHz = Math.Clamp(parsed, MinFrameRateHz, MaxFrameRateHz);
        return true;
    }

    public static double NormalizeFrameRate(double raw) =>
        double.IsFinite(raw)
            ? Math.Clamp(raw, MinFrameRateHz, MaxFrameRateHz)
            : DefaultFrameRateHz;

    public static double NormalizeFrameRate(double? raw, double fallback) =>
        raw.HasValue && double.IsFinite(raw.Value)
            ? NormalizeFrameRate(raw.Value)
            : NormalizeFrameRate(fallback);

    public static int NormalizeDisplayDecimation(int? raw) =>
        raw.HasValue
            ? Math.Clamp(raw.Value, MinDisplayDecimation, MaxDisplayDecimation)
            : DefaultDisplayDecimation;

    public static int NormalizeWaterfallUpdatePeriod(int? raw) =>
        raw.HasValue
            ? Math.Clamp(raw.Value, MinWaterfallUpdatePeriod, MaxWaterfallUpdatePeriod)
            : DefaultWaterfallUpdatePeriod;

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return null;
    }

    private static bool IsLowPowerProfile(string? raw) =>
        raw is not null &&
        (string.Equals(raw, "low-power", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(raw, "lowpower", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(raw, "pi", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(raw, "raspberry-pi", StringComparison.OrdinalIgnoreCase));

    private static bool IsTruthy(string? raw) =>
        raw is not null &&
        (string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(raw, "on", StringComparison.OrdinalIgnoreCase));
}
