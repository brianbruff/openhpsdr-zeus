// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Regression lock for the 2026-07-04 P3 analyzer/display calibration fix:
// raw n9dsp dBFS analyzer bins sat ~25-30 dB above the P2/WDSP render for
// the same antenna, blowing out the waterfall's fixed color window. The fix
// is a -27 dB default offset (overridable via
// ZEUS_PROTOCOL3_ANALYZER_CALIBRATION_DB / Zeus:Protocol3:Sidecar:AnalyzerCalibrationDb)
// applied by n9dsp via the sidecar's --analyzer-calibration-db launch
// argument, composed in Protocol3SidecarBridge.BuildStartInfo.
//
// BuildStartInfo is a pure argument-list builder — it never touches the
// network — so this is asserted directly rather than through a live sidecar
// process. The actual native n9dsp option parsing/application (does the
// sidecar process really apply this dB offset to every analyzer bin) is not
// reachable from Zeus.Server.Tests: it lives in the n9dsp native analyzer,
// which this project has no handle to. Only the Zeus-side arg emission is
// asserted here.

using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class Protocol3CalibrationE2ETests
{
    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("network not expected in these tests");
    }

    private static Protocol3SidecarBridge NewBridge(IConfiguration? configuration = null) =>
        new(new ThrowingHttpClientFactory(),
            configuration ?? new ConfigurationBuilder().Build(),
            NullLogger<Protocol3SidecarBridge>.Instance);

    private static StateDto MinimalState() => new(
        ConnectionStatus.Connected,
        "192.168.1.25:1024",
        VfoHz: 14_200_000,
        Mode: RxMode.USB,
        FilterLowHz: 150,
        FilterHighHz: 2_850,
        SampleRate: 1_536_000,
        MaxReceivers: 1);

    private static ProcessStartInfo BuildStartInfoWithArgOverrides(
        IConfiguration? configuration,
        int rxStreams = 1)
    {
        using var bridge = NewBridge(configuration);
        return bridge.BuildStartInfo(
            project: "n9dsp-sidecar.csproj",
            n9dspLibrary: "n9dsp.dll",
            listenUrl: new Uri("http://127.0.0.1:2074"),
            radioIp: IPAddress.Parse("192.168.1.25"),
            p3Port: 1024,
            sampleRateHz: 1_536_000,
            rxStreams: rxStreams,
            initialState: MinimalState());
    }

    [Fact]
    public void BuildStartInfo_EmitsMinusTwentySevenDbDefaultCalibration()
    {
        var oldEnv = Environment.GetEnvironmentVariable("ZEUS_PROTOCOL3_ANALYZER_CALIBRATION_DB");
        try
        {
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_ANALYZER_CALIBRATION_DB", null);
            var psi = BuildStartInfoWithArgOverrides(configuration: null);

            var args = psi.ArgumentList;
            var flagIndex = args.IndexOf("--analyzer-calibration-db");

            Assert.True(flagIndex >= 0, "expected --analyzer-calibration-db in the sidecar arg list");
            Assert.True(flagIndex + 1 < args.Count, "expected a value after --analyzer-calibration-db");
            Assert.Equal(-27.0, double.Parse(args[flagIndex + 1], System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_ANALYZER_CALIBRATION_DB", oldEnv);
        }
    }

    [Fact]
    public void BuildStartInfo_HonorsEnvironmentOverrideForCalibration()
    {
        var oldEnv = Environment.GetEnvironmentVariable("ZEUS_PROTOCOL3_ANALYZER_CALIBRATION_DB");
        try
        {
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_ANALYZER_CALIBRATION_DB", "-14.5");
            var psi = BuildStartInfoWithArgOverrides(configuration: null);

            var args = psi.ArgumentList;
            var flagIndex = args.IndexOf("--analyzer-calibration-db");

            Assert.True(flagIndex >= 0);
            Assert.Equal(-14.5, double.Parse(args[flagIndex + 1], System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_ANALYZER_CALIBRATION_DB", oldEnv);
        }
    }

    [Fact]
    public void BuildStartInfo_HonorsConfigurationOverrideForCalibration()
    {
        var oldEnv = Environment.GetEnvironmentVariable("ZEUS_PROTOCOL3_ANALYZER_CALIBRATION_DB");
        try
        {
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_ANALYZER_CALIBRATION_DB", null);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Zeus:Protocol3:Sidecar:AnalyzerCalibrationDb"] = "-5",
                })
                .Build();

            var psi = BuildStartInfoWithArgOverrides(configuration);
            var args = psi.ArgumentList;
            var flagIndex = args.IndexOf("--analyzer-calibration-db");

            Assert.True(flagIndex >= 0);
            Assert.Equal(-5.0, double.Parse(args[flagIndex + 1], System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_ANALYZER_CALIBRATION_DB", oldEnv);
        }
    }

    [Fact]
    public void BuildStartInfo_OmitsCalibrationFlagWhenOverrideIsExactlyZero()
    {
        // Zero is a valid "disable calibration" override — the arg is only
        // emitted for a nonzero offset (BuildStartInfo: "if
        // (analyzerCalibrationDb != 0.0)"). Locks in that a 0 override truly
        // suppresses the flag rather than emitting "--analyzer-calibration-db 0".
        var oldEnv = Environment.GetEnvironmentVariable("ZEUS_PROTOCOL3_ANALYZER_CALIBRATION_DB");
        try
        {
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_ANALYZER_CALIBRATION_DB", "0");
            var psi = BuildStartInfoWithArgOverrides(configuration: null);

            Assert.DoesNotContain("--analyzer-calibration-db", psi.ArgumentList);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_ANALYZER_CALIBRATION_DB", oldEnv);
        }
    }

    [Fact]
    public void BuildStartInfo_EnvironmentOverrideWinsOverConfiguration()
    {
        var oldEnv = Environment.GetEnvironmentVariable("ZEUS_PROTOCOL3_ANALYZER_CALIBRATION_DB");
        try
        {
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_ANALYZER_CALIBRATION_DB", "-11");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Zeus:Protocol3:Sidecar:AnalyzerCalibrationDb"] = "-5",
                })
                .Build();

            var psi = BuildStartInfoWithArgOverrides(configuration);
            var args = psi.ArgumentList;
            var flagIndex = args.IndexOf("--analyzer-calibration-db");

            Assert.True(flagIndex >= 0);
            Assert.Equal(-11.0, double.Parse(args[flagIndex + 1], System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZEUS_PROTOCOL3_ANALYZER_CALIBRATION_DB", oldEnv);
        }
    }
}
