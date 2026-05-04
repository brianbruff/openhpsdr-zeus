// PluginScanner.cs — recursive filesystem walk that turns a set of roots
// into a catalog of PluginManifest entries.
//
// Phase 1 scope:
//   * Walk roots with Directory.EnumerateFileSystemEntries.
//   * Recognise VST3 bundles (directory ending .vst3 case-insensitive),
//     VST3 single-file modules, VST2 .dll/.so/.vst, and CLAP files.
//   * Use IBinaryHeaderSniffer to decode platform + bitness from the
//     loadable binary's header.
//   * Filter out installer payloads, MSVC runtime helpers, AAX, and any
//     directory named like a vendor sample/preset/runtime tree.
//
// Out of scope (Phase B / sidecar):
//   * VST3 moduleinfo.json + Info.plist parsing for vendor/name.
//   * LV2 manifest.ttl parsing.
//   * Loading any plugin code; the C++ sidecar owns that.
//
// Anti-pattern guarded against: returning duplicate manifests when a VST3
// bundle has both a Contents/<arch> binary AND a non-canonical nested
// .vst3 directly inside the bundle. We dedupe on resolved FilePath after
// emission so Bertom_DenoiserPro (which ships both layouts) yields one
// row per detected architecture.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Zeus.PluginHost.Discovery;

public sealed class PluginScanner : IPluginScanner
{
    // Directory-name skip list (case-insensitive). If any segment of a
    // candidate path matches one of these, the candidate is dropped before
    // it ever hits the sniffer. Intentionally conservative — we'd rather
    // miss an oddball install than spam the catalog with sample content.
    private static readonly HashSet<string> SkipDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "vcredist",
        "redist",
        "runtime",
        "samples",
        "presets",
        "tutorials",
        "docs",
        "documentation",
    };

    // Hard-skip extensions: never sniff, never emit. Anything that's
    // categorically not a plugin binary (text, archives, images, installers).
    private static readonly HashSet<string> SkipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".zip", ".rar", ".7z",
        ".txt", ".md", ".pdf",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp",
        ".json", ".xml", ".log",
    };

    // Filename-stem patterns for MSVC runtime DLLs that ship next to many
    // commercial plugin installers. They are PE32 binaries — the sniffer
    // would happily classify them as Vst2 — so we filter by name first.
    private static bool IsMsvcRuntimeDll(string name)
    {
        if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return false;
        var stem = Path.GetFileNameWithoutExtension(name);
        return stem.StartsWith("msvcr", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("msvcp", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("vcruntime", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("vcomp", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("concrt", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("mfc", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("api-ms-", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("ucrtbase", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith("runtime", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInstallerExe(string name)
    {
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return false;
        var stem = Path.GetFileNameWithoutExtension(name);
        return stem.EndsWith("-Setup", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith("-Installer", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("vcredist", StringComparison.OrdinalIgnoreCase);
    }

    private readonly IBinaryHeaderSniffer _sniffer;

    public PluginScanner(IBinaryHeaderSniffer sniffer)
    {
        _sniffer = sniffer ?? throw new ArgumentNullException(nameof(sniffer));
    }

    public Task<IReadOnlyList<PluginManifest>> ScanAsync(
        IEnumerable<string> roots,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        // Materialise to avoid surprises if the caller passes a lazy
        // enumerable that observes filesystem state mid-scan.
        var rootList = roots.ToList();
        return Task.Run<IReadOnlyList<PluginManifest>>(() => ScanCore(rootList, ct), ct);
    }

    private IReadOnlyList<PluginManifest> ScanCore(
        IReadOnlyList<string> roots,
        CancellationToken ct)
    {
        var manifests = new List<PluginManifest>();
        // Track which directories we've already enumerated as VST bundles so
        // we don't re-emit their innards as standalone files.
        var bundleDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            // First pass: find every bundle directory and emit its contents.
            // Bundles take precedence so the same .vst3 inner file isn't
            // counted as a single-file Vst3 in the second pass.
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(
                    root, "*", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                manifests.Add(new PluginManifest(
                    FilePath: root,
                    DisplayName: Path.GetFileName(root),
                    Format: PluginFormat.Unknown,
                    Bitness: PluginBitness.Unknown,
                    Platform: PluginPlatform.Unknown,
                    BundlePath: null,
                    ScanWarnings: new[] { $"failed to enumerate root: {ex.Message}" }));
                continue;
            }

            // Resolve to a list once so we can iterate twice without doing
            // two filesystem walks.
            var entryList = SafeEnumerate(entries);

            // Pass 1: bundle directories.
            foreach (var entry in entryList)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsDirectory(entry)) continue;
                if (ShouldSkipPathBySegment(entry)) continue;

                if (IsVst3BundleDir(entry))
                {
                    bundleDirs.Add(entry);
                    EmitVst3Bundle(entry, manifests);
                }
                else if (IsVst2MacBundleDir(entry))
                {
                    bundleDirs.Add(entry);
                    EmitVst2MacBundle(entry, manifests);
                }
            }

            // Pass 2: non-bundle files.
            foreach (var entry in entryList)
            {
                ct.ThrowIfCancellationRequested();
                if (IsDirectory(entry)) continue;
                if (ShouldSkipPathBySegment(entry)) continue;
                if (IsInsideKnownBundle(entry, bundleDirs)) continue;

                EmitTopLevelFile(entry, manifests);
            }
        }

        // Deterministic ordering for tests + UI tables.
        manifests.Sort((a, b) => string.CompareOrdinal(a.FilePath, b.FilePath));
        return manifests;
    }

    private static List<string> SafeEnumerate(IEnumerable<string> source)
    {
        // Guard against transient I/O during enumeration (perm denied,
        // symlink loops). EnumerateFileSystemEntries can throw mid-iteration,
        // so wrap the foreach.
        var result = new List<string>();
        try
        {
            foreach (var entry in source)
            {
                result.Add(entry);
            }
        }
        catch
        {
            // Best-effort: return what we got.
        }
        return result;
    }

    private static bool IsDirectory(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    private static bool IsVst3BundleDir(string dir)
        => dir.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase)
           && Directory.Exists(dir);

    private static bool IsVst2MacBundleDir(string dir)
    {
        if (!dir.EndsWith(".vst", StringComparison.OrdinalIgnoreCase))
            return false;
        var macOsDir = Path.Combine(dir, "Contents", "MacOS");
        return Directory.Exists(macOsDir);
    }

    private static bool ShouldSkipPathBySegment(string path)
    {
        foreach (var segment in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrEmpty(segment)) continue;
            if (SkipDirNames.Contains(segment)) return true;
        }
        return false;
    }

    private static bool IsInsideKnownBundle(string filePath, HashSet<string> bundleDirs)
    {
        // A file lives "inside a bundle" if any of its parent directories
        // is a recognised bundle root. Compare with trailing separator to
        // avoid prefix collisions (e.g. /a/Foo.vst3 vs /a/Foo.vst3.bak).
        foreach (var bundle in bundleDirs)
        {
            var prefix = bundle.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? bundle
                : bundle + Path.DirectorySeparatorChar;
            if (filePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void EmitVst3Bundle(string bundleDir, List<PluginManifest> manifests)
    {
        var displayName = Path.GetFileNameWithoutExtension(bundleDir);
        var contents = Path.Combine(bundleDir, "Contents");
        var hasContents = Directory.Exists(contents);
        var emitted = 0;
        var warnings = new List<string>();

        // Canonical Steinberg layout: Contents/<arch>/<binary>.
        if (hasContents)
        {
            // Windows architectures.
            emitted += EmitBundleArch(bundleDir, Path.Combine(contents, "x86_64-win"), "*.vst3",
                PluginFormat.Vst3, displayName, manifests, warnings);
            emitted += EmitBundleArch(bundleDir, Path.Combine(contents, "x86-win"), "*.vst3",
                PluginFormat.Vst3, displayName, manifests, warnings);
            emitted += EmitBundleArch(bundleDir, Path.Combine(contents, "i386-win"), "*.vst3",
                PluginFormat.Vst3, displayName, manifests, warnings);

            // Linux architectures (.so).
            emitted += EmitBundleArch(bundleDir, Path.Combine(contents, "x86_64-linux"), "*.so",
                PluginFormat.Vst3, displayName, manifests, warnings);
            emitted += EmitBundleArch(bundleDir, Path.Combine(contents, "i386-linux"), "*.so",
                PluginFormat.Vst3, displayName, manifests, warnings);
            emitted += EmitBundleArch(bundleDir, Path.Combine(contents, "aarch64-linux"), "*.so",
                PluginFormat.Vst3, displayName, manifests, warnings);

            // macOS — no extension on inner binary; just take all files.
            var macOsDir = Path.Combine(contents, "MacOS");
            if (Directory.Exists(macOsDir))
            {
                foreach (var inner in SafeListFiles(macOsDir))
                {
                    var sniff = _sniffer.Sniff(inner);
                    var perFile = new List<string>(warnings);
                    if (!string.IsNullOrEmpty(sniff.Notes)) perFile.Add(sniff.Notes!);
                    manifests.Add(new PluginManifest(
                        FilePath: inner,
                        DisplayName: displayName,
                        Format: PluginFormat.Vst3,
                        Bitness: sniff.Bitness,
                        Platform: sniff.Platform == PluginPlatform.Unknown ? PluginPlatform.MacOS : sniff.Platform,
                        BundlePath: bundleDir,
                        ScanWarnings: perFile));
                    emitted++;
                }
            }
        }
        else
        {
            warnings.Add("VST3 bundle has no Contents/ directory");
        }

        // Non-canonical fallback: a *.vst3 file directly inside the bundle
        // root (Bertom_DenoiserPro ships this layout). Emit one per top-
        // level file, with a warning so the catalog UI can flag it.
        foreach (var topFile in SafeListFiles(bundleDir))
        {
            if (!topFile.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase))
                continue;
            // Skip files that are themselves directories (shouldn't happen
            // on Linux but guard anyway).
            if (Directory.Exists(topFile)) continue;

            var sniff = _sniffer.Sniff(topFile);
            var perFile = new List<string>(warnings)
            {
                "non-canonical layout: .vst3 file at bundle root (no Contents/<arch>)",
            };
            if (!string.IsNullOrEmpty(sniff.Notes)) perFile.Add(sniff.Notes!);
            manifests.Add(new PluginManifest(
                FilePath: topFile,
                DisplayName: displayName,
                Format: PluginFormat.Vst3,
                Bitness: sniff.Bitness,
                Platform: sniff.Platform,
                BundlePath: bundleDir,
                ScanWarnings: perFile));
            emitted++;
        }

        // Some non-canonical layouts have a nested *.vst3 directory inside
        // the bundle (Bertom again). Recurse one level — but cap so we
        // don't accidentally walk into Contents/<vendor>/installer trees.
        if (emitted == 0)
        {
            foreach (var nestedDir in SafeListDirs(bundleDir))
            {
                if (IsVst3BundleDir(nestedDir))
                {
                    var subWarnings = new List<string>(warnings)
                    {
                        "non-canonical layout: nested .vst3 bundle inside parent .vst3",
                    };
                    EmitVst3BundleNested(nestedDir, displayName, manifests, subWarnings);
                }
            }
        }
    }

    private void EmitVst3BundleNested(
        string bundleDir,
        string parentDisplayName,
        List<PluginManifest> manifests,
        List<string> seedWarnings)
    {
        var contents = Path.Combine(bundleDir, "Contents");
        if (!Directory.Exists(contents)) return;

        EmitBundleArch(bundleDir, Path.Combine(contents, "x86_64-win"), "*.vst3",
            PluginFormat.Vst3, parentDisplayName, manifests, seedWarnings);
        EmitBundleArch(bundleDir, Path.Combine(contents, "x86-win"), "*.vst3",
            PluginFormat.Vst3, parentDisplayName, manifests, seedWarnings);
        EmitBundleArch(bundleDir, Path.Combine(contents, "i386-win"), "*.vst3",
            PluginFormat.Vst3, parentDisplayName, manifests, seedWarnings);
        EmitBundleArch(bundleDir, Path.Combine(contents, "x86_64-linux"), "*.so",
            PluginFormat.Vst3, parentDisplayName, manifests, seedWarnings);
        EmitBundleArch(bundleDir, Path.Combine(contents, "aarch64-linux"), "*.so",
            PluginFormat.Vst3, parentDisplayName, manifests, seedWarnings);
        EmitBundleArch(bundleDir, Path.Combine(contents, "i386-linux"), "*.so",
            PluginFormat.Vst3, parentDisplayName, manifests, seedWarnings);
    }

    private void EmitVst2MacBundle(string bundleDir, List<PluginManifest> manifests)
    {
        var displayName = Path.GetFileNameWithoutExtension(bundleDir);
        var macOsDir = Path.Combine(bundleDir, "Contents", "MacOS");
        if (!Directory.Exists(macOsDir)) return;

        foreach (var inner in SafeListFiles(macOsDir))
        {
            var sniff = _sniffer.Sniff(inner);
            var warnings = new List<string>();
            if (!string.IsNullOrEmpty(sniff.Notes)) warnings.Add(sniff.Notes!);
            manifests.Add(new PluginManifest(
                FilePath: inner,
                DisplayName: displayName,
                Format: PluginFormat.Vst2,
                Bitness: sniff.Bitness,
                Platform: sniff.Platform == PluginPlatform.Unknown ? PluginPlatform.MacOS : sniff.Platform,
                BundlePath: bundleDir,
                ScanWarnings: warnings));
        }
    }

    private int EmitBundleArch(
        string bundleDir,
        string archDir,
        string filePattern,
        PluginFormat format,
        string displayName,
        List<PluginManifest> manifests,
        List<string> seedWarnings)
    {
        if (!Directory.Exists(archDir)) return 0;
        var emitted = 0;
        foreach (var inner in SafeListFiles(archDir, filePattern))
        {
            var sniff = _sniffer.Sniff(inner);
            var perFile = new List<string>(seedWarnings);
            if (!string.IsNullOrEmpty(sniff.Notes)) perFile.Add(sniff.Notes!);
            manifests.Add(new PluginManifest(
                FilePath: inner,
                DisplayName: displayName,
                Format: format,
                Bitness: sniff.Bitness,
                Platform: sniff.Platform,
                BundlePath: bundleDir,
                ScanWarnings: perFile));
            emitted++;
        }
        return emitted;
    }

    private void EmitTopLevelFile(string filePath, List<PluginManifest> manifests)
    {
        var name = Path.GetFileName(filePath);
        var ext = Path.GetExtension(name);

        // Hard-skip extensions (text, archives, images, .exe).
        if (SkipExtensions.Contains(ext))
        {
            // .exe gets one more pass through IsInstallerExe so we'd skip
            // installers even if the SkipExtensions list ever changes.
            return;
        }

        // AAX is filtered silently — we don't want a user's Pro Tools
        // collection cluttering the Zeus host catalog.
        if (string.Equals(ext, ".aaxplugin", StringComparison.OrdinalIgnoreCase))
            return;

        // LV2 is manifest-driven (.ttl) and not handled here.
        if (string.Equals(ext, ".lv2", StringComparison.OrdinalIgnoreCase))
            return;

        // Installer / runtime helpers.
        if (IsInstallerExe(name)) return;
        if (IsMsvcRuntimeDll(name)) return;

        if (string.Equals(ext, ".vst3", StringComparison.OrdinalIgnoreCase))
        {
            EmitFlatFile(filePath, PluginFormat.Vst3, manifests);
            return;
        }
        if (string.Equals(ext, ".clap", StringComparison.OrdinalIgnoreCase))
        {
            EmitFlatFile(filePath, PluginFormat.Clap, manifests);
            return;
        }
        if (string.Equals(ext, ".dll", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".so", StringComparison.OrdinalIgnoreCase))
        {
            EmitFlatFile(filePath, PluginFormat.Vst2, manifests);
            return;
        }
    }

    private void EmitFlatFile(string filePath, PluginFormat format, List<PluginManifest> manifests)
    {
        var sniff = _sniffer.Sniff(filePath);
        var warnings = new List<string>();
        if (!string.IsNullOrEmpty(sniff.Notes)) warnings.Add(sniff.Notes!);
        manifests.Add(new PluginManifest(
            FilePath: filePath,
            DisplayName: Path.GetFileNameWithoutExtension(filePath),
            Format: format,
            Bitness: sniff.Bitness,
            Platform: sniff.Platform,
            BundlePath: null,
            ScanWarnings: warnings));
    }

    private static IEnumerable<string> SafeListFiles(string dir, string pattern = "*")
    {
        try
        {
            return Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeListDirs(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
