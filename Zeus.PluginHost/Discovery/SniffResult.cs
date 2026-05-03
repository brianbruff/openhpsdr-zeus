// SniffResult.cs — output of IBinaryHeaderSniffer.Sniff(filePath).
//
// Notes is a free-form field used to surface non-fatal parsing issues
// (truncated PE, unknown e_machine, fat universal Mach-O). The scanner
// promotes Notes into PluginManifest.ScanWarnings.

namespace Zeus.PluginHost.Discovery;

public sealed record SniffResult(
    PluginPlatform Platform,
    PluginBitness Bitness,
    string? Notes);
