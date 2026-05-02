// SidecarLocator.cs — find the zeus-plughost binary.
//
// Resolution order, first hit wins:
//   1. ZEUS_PLUGHOST_BIN env var (absolute path or PATH-resolvable name).
//   2. Walk up from the entry assembly directory looking for the
//      sibling `openhpsdr-zeus-plughost/build/zeus-plughost` checkout.
//      This is what every developer running locally will hit during
//      Phase 1 — the C++ repo is a sibling of the Zeus repo on KB2UKA's
//      box and Brian's.
//   3. Plain `zeus-plughost` on PATH.
//
// The locator never spawns the binary — it just resolves a path. The
// PluginHostManager owns the launch.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Zeus.PluginHost.Native;

public static class SidecarLocator
{
    /// <summary>
    /// Environment variable that, if set, short-circuits the search.
    /// The value is interpreted first as an absolute / relative path,
    /// then as a PATH-resolvable bare name.
    /// </summary>
    public const string EnvVarName = "ZEUS_PLUGHOST_BIN";

    /// <summary>Bare binary name (with the platform extension applied).</summary>
    public static string BinaryName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "zeus-plughost.exe"
            : "zeus-plughost";

    /// <summary>
    /// Result of a non-spawning probe for the sidecar binary.
    /// </summary>
    /// <param name="Path">Resolved binary path, or null if not found.</param>
    /// <param name="MissingReason">
    /// Operator-facing explanation when <paramref name="Path"/> is null
    /// (e.g. "sidecar binary 'zeus-plughost' was not found"). Null on success.
    /// </param>
    public readonly record struct ProbeResult(string? Path, string? MissingReason);

    /// <summary>
    /// Like <see cref="Locate"/>, but never logs a warning on a miss and
    /// returns a structured reason for the capabilities endpoint to expose.
    /// Safe to call at startup before the sidecar has ever been launched.
    /// </summary>
    public static ProbeResult Probe()
    {
        var path = Locate(NullPluginHostLog.Instance);
        if (path != null) return new ProbeResult(path, null);
        return new ProbeResult(
            null,
            $"Sidecar binary '{BinaryName}' was not found via {EnvVarName}, " +
            "sibling checkout, or PATH.");
    }

    /// <summary>
    /// Resolve the sidecar path, or return <c>null</c> if no candidate is
    /// found. Callers (PluginHostManager) decide how to surface the
    /// missing-binary case to the operator.
    /// </summary>
    public static string? Locate(IPluginHostLog? log = null)
    {
        log ??= NullPluginHostLog.Instance;

        var fromEnv = TryEnvVar(log);
        if (fromEnv != null) return fromEnv;

        var fromSibling = TrySiblingCheckout(log);
        if (fromSibling != null) return fromSibling;

        var fromPath = TryFromPath(log);
        if (fromPath != null) return fromPath;

        log.LogWarning(
            $"SidecarLocator: no zeus-plughost binary found via env var, " +
            $"sibling checkout, or PATH.");
        return null;
    }

    private static string? TryEnvVar(IPluginHostLog log)
    {
        var v = Environment.GetEnvironmentVariable(EnvVarName);
        if (string.IsNullOrWhiteSpace(v)) return null;

        // Accept absolute / relative paths verbatim.
        if (File.Exists(v))
        {
            log.LogInformation($"SidecarLocator: resolved via {EnvVarName}={v}");
            return Path.GetFullPath(v);
        }

        // Otherwise try resolving on PATH.
        var resolved = ResolveOnPath(v);
        if (resolved != null)
        {
            log.LogInformation(
                $"SidecarLocator: resolved {EnvVarName}={v} via PATH -> {resolved}");
            return resolved;
        }

        log.LogWarning(
            $"SidecarLocator: {EnvVarName}={v} did not resolve to an existing file.");
        return null;
    }

    private static string? TrySiblingCheckout(IPluginHostLog log)
    {
        // Start from the entry assembly's directory, walk up looking for
        // a sibling `openhpsdr-zeus-plughost/build/<binary>`. This is the
        // Phase 1 default for developer workstations that have the C++
        // repo cloned next to the Zeus checkout.
        var start = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(start))
        {
            start = Path.GetDirectoryName(
                Assembly.GetEntryAssembly()?.Location ?? string.Empty)
                ?? string.Empty;
        }

        if (string.IsNullOrEmpty(start)) return null;

        var binary = BinaryName;
        var dir = new DirectoryInfo(start);

        // Walk upward up to 8 levels — generous enough to cover bin/Debug/net8.0
        // nesting plus a worktree wrapper; defensive against an unbounded loop.
        for (var depth = 0; depth < 8 && dir != null; depth++)
        {
            var candidate = Path.Combine(
                dir.FullName, "openhpsdr-zeus-plughost", "build", binary);
            if (File.Exists(candidate))
            {
                log.LogInformation(
                    $"SidecarLocator: resolved via sibling checkout -> {candidate}");
                return candidate;
            }

            // Also check the parent directory's siblings, since dotnet
            // run drops the binary inside `bin/Debug/net8.0/...`. The
            // sibling repo is much higher up.
            dir = dir.Parent;
        }

        // Final fallback: explicit ~/Projects layout used on KB2UKA's box.
        var home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrEmpty(home))
        {
            var direct = Path.Combine(
                home, "Projects", "openhpsdr-zeus-plughost", "build", binary);
            if (File.Exists(direct))
            {
                log.LogInformation(
                    $"SidecarLocator: resolved via $HOME/Projects -> {direct}");
                return direct;
            }
        }

        return null;
    }

    private static string? TryFromPath(IPluginHostLog log)
    {
        var resolved = ResolveOnPath(BinaryName);
        if (resolved != null)
        {
            log.LogInformation($"SidecarLocator: resolved on PATH -> {resolved}");
        }
        return resolved;
    }

    private static string? ResolveOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        var separator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ';' : ':';
        var entries = path.Split(separator, StringSplitOptions.RemoveEmptyEntries);
        var candidates = new List<string>(entries.Length);
        foreach (var dir in entries)
        {
            try
            {
                candidates.Add(Path.Combine(dir, name));
            }
            catch
            {
                // Path.Combine can throw on malformed PATH entries (rare on
                // Windows with embedded quotes); skip them silently rather
                // than abort the whole search.
            }
        }

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }
}
