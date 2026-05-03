// RealWorldScanTests.cs — sanity check against KB2UKA's actual ~/Desktop/
// Plugins/VST PLUGINS/ tree. Skipped on any host that doesn't have it,
// so CI without the folder still goes green.
//
// Assertions are intentionally rough — exact counts shift as the user
// installs/removes plugins. We're guarding the categories the scanner
// MUST get right (no .exe leaks, no AAX, separation of x86/x64 PE) rather
// than pinning specific names.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using Zeus.PluginHost.Discovery;

namespace Zeus.PluginHost.Tests.Discovery;

public sealed class RealWorldScanTests
{
    private readonly ITestOutputHelper _out;

    public RealWorldScanTests(ITestOutputHelper output)
    {
        _out = output;
    }

    private static string? LocatePluginRoot()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home)) return null;
        var path = Path.Combine(home, "Desktop", "Plugins", "VST PLUGINS");
        return Directory.Exists(path) ? path : null;
    }

    [SkippableFact]
    public async Task ScansKb2ukaCollectionWithoutThrowing()
    {
        var root = LocatePluginRoot();
        Skip.If(root is null, "~/Desktop/Plugins/VST PLUGINS/ not present on this host");

        var scanner = new PluginScanner(new BinaryHeaderSniffer());
        var manifests = await scanner.ScanAsync(new[] { root! }, CancellationToken.None);

        // Print breakdown so a failure has context.
        var byFormat = manifests
            .GroupBy(m => m.Format)
            .Select(g => $"{g.Key}={g.Count()}")
            .ToArray();
        _out.WriteLine($"total manifests: {manifests.Count}");
        _out.WriteLine($"by format: {string.Join(", ", byFormat)}");

        var grouped = manifests
            .GroupBy(m => (m.Format, m.Platform, m.Bitness))
            .OrderBy(g => g.Key.Format).ThenBy(g => g.Key.Platform).ThenBy(g => g.Key.Bitness)
            .Select(g => $"{g.Key.Format}/{g.Key.Platform}/{g.Key.Bitness}={g.Count()}")
            .ToArray();
        _out.WriteLine("by (format,platform,bitness):");
        foreach (var line in grouped) _out.WriteLine("  " + line);

        _out.WriteLine("sample manifests:");
        foreach (var m in manifests.Take(5))
        {
            _out.WriteLine(
                $"  {m.Format,-7} {m.Platform,-7} {m.Bitness,-6} bundle={(m.BundlePath != null ? "yes" : "no ")} \"{m.DisplayName}\"  -> {m.FilePath}");
        }
        if (manifests.Count > 5)
        {
            _out.WriteLine($"  (+ {manifests.Count - 5} more)");
        }

        // Sanity rules — none of these should ever fail on a real plugin tree.
        Assert.DoesNotContain(manifests, m => m.Format == PluginFormat.Aax);
        Assert.DoesNotContain(manifests,
            m => m.FilePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(manifests,
            m => m.FilePath.Contains("vcredist", StringComparison.OrdinalIgnoreCase));
        Assert.All(manifests, m => Assert.NotNull(m.ScanWarnings));

        // KB2UKA's tree has the Modern* / Bitsonic / Ambience etc — at least
        // a handful of 32-bit Windows VST2 entries.
        var winX86Vst2 = manifests
            .Where(m => m.Format == PluginFormat.Vst2 && m.Platform == PluginPlatform.Windows && m.Bitness == PluginBitness.X86)
            .ToList();
        Assert.True(winX86Vst2.Count >= 5,
            $"expected >= 5 Vst2/Windows/X86 plugins, got {winX86Vst2.Count}");

        // And at least a handful of 64-bit Windows VST2 (Clear, ReaPlugs, etc).
        var winX64Vst2 = manifests
            .Where(m => m.Format == PluginFormat.Vst2 && m.Platform == PluginPlatform.Windows && m.Bitness == PluginBitness.X64)
            .ToList();
        Assert.True(winX64Vst2.Count >= 5,
            $"expected >= 5 Vst2/Windows/X64 plugins, got {winX64Vst2.Count}");

        // At least one VST3 must have made it through.
        Assert.Contains(manifests, m => m.Format == PluginFormat.Vst3);
    }
}
