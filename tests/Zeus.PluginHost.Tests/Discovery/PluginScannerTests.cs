// PluginScannerTests.cs — synthetic directory tree exercising every scanner
// branch: VST3 bundle (canonical), VST3 bundle (non-canonical), VST3 single
// file, VST2 .dll Windows, MSVC runtime skip, installer skip, AAX skip,
// vendor-redist directory skip, and CLAP detection.
//
// Builds the tree under Path.GetTempPath() with a per-run GUID so the suite
// is parallel-safe and self-cleaning.

using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zeus.PluginHost.Discovery;

namespace Zeus.PluginHost.Tests.Discovery;

public sealed class PluginScannerTests : IDisposable
{
    private readonly string _root;
    private readonly PluginScanner _scanner;

    public PluginScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zeus-scanner-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _scanner = new PluginScanner(new BinaryHeaderSniffer());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch { /* best-effort */ }
    }

    private static byte[] BuildPe(ushort machine)
    {
        const int peOffset = 0x80;
        const int totalSize = 0x100;
        var buf = new byte[totalSize];
        buf[0] = 0x4D; buf[1] = 0x5A;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x3C, 4), peOffset);
        buf[peOffset + 0] = 0x50;
        buf[peOffset + 1] = 0x45;
        buf[peOffset + 2] = 0x00;
        buf[peOffset + 3] = 0x00;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(peOffset + 4, 2), machine);
        return buf;
    }

    private static byte[] BuildElf(byte eiClass, ushort eMachine)
    {
        var buf = new byte[0x80];
        buf[0] = 0x7F; buf[1] = 0x45; buf[2] = 0x4C; buf[3] = 0x46;
        buf[4] = eiClass;
        buf[5] = 1; // little-endian
        buf[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x12, 2), eMachine);
        return buf;
    }

    private void Write(string relPath, byte[] bytes)
    {
        var full = Path.Combine(_root, relPath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(full, bytes);
    }

    private void MkDir(string relPath)
        => Directory.CreateDirectory(Path.Combine(_root, relPath));

    [Fact]
    public async Task ScansSyntheticTreeAndClassifiesEverythingCorrectly()
    {
        // Top-level flat files.
        Write("fakevst.dll", BuildPe(0x8664));            // Vst2 / X64 / Windows
        Write("legacy.dll", BuildPe(0x014C));             // Vst2 / X86 / Windows
        Write("msvcr120.dll", BuildPe(0x8664));           // MUST skip (MSVC runtime)
        Write("vcredist_x64.exe", BuildPe(0x8664));       // MUST skip (.exe + vcredist)
        Write("uninstall.exe", BuildPe(0x8664));          // MUST skip (.exe + Uninstall*)
        Write("flat.vst3", BuildElf(1, 0x03));            // Vst3 / X86 / Linux
        Write("cool.clap", BuildPe(0x8664));              // Clap / X64 / Windows
        Write("Pro Tools Plug.aaxplugin", BuildPe(0x8664)); // SKIP (AAX)
        Write("README.md", new byte[] { 0x23, 0x20, 0x68 });   // SKIP (.md)

        // Canonical VST3 bundle.
        Write(Path.Combine("My VST3.vst3", "Contents", "x86_64-win", "My VST3.vst3"),
            BuildPe(0x8664));
        Write(Path.Combine("My VST3.vst3", "Contents", "x86_64-linux", "My VST3.so"),
            BuildElf(2, 0x3E));

        // Non-canonical bundle: file directly inside the bundle dir, no
        // Contents/<arch>. Mirrors Bertom_DenoiserPro's fallback layout.
        Write(Path.Combine("Bundle.vst3", "Bundle.vst3"), BuildPe(0x8664));

        // Vendor redist tree must be skipped wholesale.
        Write(Path.Combine("redist", "secret.dll"), BuildPe(0x8664));

        var manifests = await _scanner.ScanAsync(new[] { _root }, CancellationToken.None);

        // No skipped names should appear.
        Assert.DoesNotContain(manifests, m => m.FilePath.EndsWith("msvcr120.dll", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(manifests, m => m.FilePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(manifests, m => m.FilePath.EndsWith(".aaxplugin", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(manifests, m => m.FilePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(manifests, m => m.FilePath.Contains(Path.DirectorySeparatorChar + "redist" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(manifests, m => m.Format == PluginFormat.Aax);

        // Expected count: 2 flat .dll + 1 .vst3 single-file + 1 .clap +
        // 2 entries for the canonical VST3 bundle (win + linux) + 1 for
        // the non-canonical Bundle.vst3 = 7.
        Assert.Equal(7, manifests.Count);

        // Spot-check each format/bitness/platform.
        var fakeVst = manifests.Single(m => m.FilePath.EndsWith("fakevst.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(PluginFormat.Vst2, fakeVst.Format);
        Assert.Equal(PluginBitness.X64, fakeVst.Bitness);
        Assert.Equal(PluginPlatform.Windows, fakeVst.Platform);
        Assert.Null(fakeVst.BundlePath);

        var legacy = manifests.Single(m => m.FilePath.EndsWith("legacy.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(PluginFormat.Vst2, legacy.Format);
        Assert.Equal(PluginBitness.X86, legacy.Bitness);
        Assert.Equal(PluginPlatform.Windows, legacy.Platform);

        var flat = manifests.Single(m => m.FilePath.EndsWith("flat.vst3", StringComparison.OrdinalIgnoreCase)
                                         && m.BundlePath is null);
        Assert.Equal(PluginFormat.Vst3, flat.Format);
        Assert.Equal(PluginBitness.X86, flat.Bitness);
        Assert.Equal(PluginPlatform.Linux, flat.Platform);

        var clap = manifests.Single(m => m.FilePath.EndsWith("cool.clap", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(PluginFormat.Clap, clap.Format);
        Assert.Equal(PluginBitness.X64, clap.Bitness);
        Assert.Equal(PluginPlatform.Windows, clap.Platform);

        // Canonical bundle: two entries, both with BundlePath set.
        var canonicalBundle = Path.Combine(_root, "My VST3.vst3");
        var canonicalEntries = manifests.Where(m => m.BundlePath != null
            && string.Equals(m.BundlePath, canonicalBundle, StringComparison.OrdinalIgnoreCase)
            && !m.ScanWarnings.Any(w => w.Contains("non-canonical", StringComparison.OrdinalIgnoreCase))).ToList();
        Assert.Equal(2, canonicalEntries.Count);
        Assert.Contains(canonicalEntries, m => m.Bitness == PluginBitness.X64 && m.Platform == PluginPlatform.Windows);
        Assert.Contains(canonicalEntries, m => m.Bitness == PluginBitness.X64 && m.Platform == PluginPlatform.Linux);
        Assert.All(canonicalEntries, m => Assert.Equal(PluginFormat.Vst3, m.Format));
        Assert.All(canonicalEntries, m => Assert.Equal("My VST3", m.DisplayName));

        // Non-canonical bundle has a ScanWarning explaining the layout.
        var nonCanonical = manifests.Single(m =>
            m.BundlePath != null
            && string.Equals(m.BundlePath, Path.Combine(_root, "Bundle.vst3"), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(PluginFormat.Vst3, nonCanonical.Format);
        Assert.Contains(nonCanonical.ScanWarnings, w => w.Contains("non-canonical", StringComparison.OrdinalIgnoreCase));

        // Output is sorted by FilePath ascending.
        var sorted = manifests.Select(m => m.FilePath).ToList();
        var resorted = sorted.OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert.Equal(resorted, sorted);
    }

    [Fact]
    public async Task EmptyRoot_ReturnsEmptyList()
    {
        var manifests = await _scanner.ScanAsync(new[] { _root }, CancellationToken.None);
        Assert.Empty(manifests);
    }

    [Fact]
    public async Task NonExistentRoot_IsSilentlyIgnored()
    {
        var ghost = Path.Combine(_root, "does-not-exist");
        var manifests = await _scanner.ScanAsync(new[] { ghost }, CancellationToken.None);
        Assert.Empty(manifests);
    }

    [Fact]
    public async Task Cancellation_ThrowsOperationCanceled()
    {
        // Populate enough entries that the scanner has work to do.
        for (int i = 0; i < 50; i++)
            Write($"plugin{i}.dll", BuildPe(0x8664));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => _scanner.ScanAsync(new[] { _root }, cts.Token));
    }
}
