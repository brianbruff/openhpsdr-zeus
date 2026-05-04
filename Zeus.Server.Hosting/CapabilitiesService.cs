// SPDX-License-Identifier: GPL-2.0-or-later
//
// CapabilitiesService — single source of truth for the /api/capabilities
// endpoint. Captures host-mode, platform / architecture, and per-feature
// availability once at construction and serves the same snapshot for the
// lifetime of the process.
//
// Probe-once-at-startup is deliberate. Sidecar binaries don't appear or
// disappear at runtime in normal operation; revisit if/when we ship a
// hot-install path. The frontend caches the response anyway.
//
// Adding a new feature gate (issue #185 — amp manager, MIDI, …):
//   1. Probe its availability here in the constructor.
//   2. Add a field to FeatureMatrix and populate it in Snapshot().
//   3. Update zeus-web/src/api/capabilities.ts to mirror the shape.

using System.Reflection;
using System.Runtime.InteropServices;
using Zeus.PluginHost.Native;

namespace Zeus.Server;

public sealed class CapabilitiesService
{
    private readonly CapabilitiesSnapshot _snapshot;

    public CapabilitiesService(ZeusHostOptions options)
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

        var platform = DetectPlatform();
        var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

        _snapshot = new CapabilitiesSnapshot(
            Host: options.HostMode == ZeusHostMode.Desktop ? "desktop" : "server",
            Platform: platform,
            Architecture: architecture,
            Version: version,
            Features: new FeatureMatrix(VstHost: ProbeVstHost(platform)));
    }

    public CapabilitiesSnapshot Snapshot() => _snapshot;

    // VST host availability gate. Today the C++ sidecar only ships for
    // Linux; macOS and Windows builds are not in this release. The
    // platform check stays here (server-side) so the frontend never has
    // to duplicate the OS-support matrix. When we add macOS / Windows
    // sidecar binaries this gate flips automatically.
    private static FeatureGate ProbeVstHost(string platform)
    {
        if (platform != "linux")
        {
            return new FeatureGate(
                Available: false,
                Reason: "VST host is only supported on Linux in this release.",
                SidecarPath: null);
        }

        var probe = SidecarLocator.Probe();
        if (probe.Path == null)
        {
            return new FeatureGate(
                Available: false,
                Reason: probe.MissingReason,
                SidecarPath: null);
        }

        return new FeatureGate(Available: true, Reason: null, SidecarPath: probe.Path);
    }

    private static string DetectPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "darwin";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        return "unknown";
    }
}

// JSON shape returned by /api/capabilities. Property names land lower-case
// on the wire via the default minimal-API camel-case policy, matching the
// rest of the Zeus REST surface.

public sealed record CapabilitiesSnapshot(
    string Host,
    string Platform,
    string Architecture,
    string Version,
    FeatureMatrix Features);

public sealed record FeatureMatrix(FeatureGate VstHost);

public sealed record FeatureGate(
    bool Available,
    string? Reason,
    string? SidecarPath);
