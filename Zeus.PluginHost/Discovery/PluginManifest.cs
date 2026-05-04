// PluginManifest.cs — one entry in the discovery catalog.
//
// Phase 1 (this file) intentionally records only what we can determine from
// the filesystem walk + a magic-byte sniff. Vendor and display-name
// extraction from VST3 moduleinfo.json or Info.plist are Phase B and will
// extend the record without breaking callers (record-with-defaults).

using System.Collections.Generic;

namespace Zeus.PluginHost.Discovery;

public sealed record PluginManifest(
    string FilePath,
    string DisplayName,
    PluginFormat Format,
    PluginBitness Bitness,
    PluginPlatform Platform,
    string? BundlePath,
    IReadOnlyList<string> ScanWarnings);
