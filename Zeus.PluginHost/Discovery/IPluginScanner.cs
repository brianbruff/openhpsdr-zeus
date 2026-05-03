// IPluginScanner.cs — discovery surface used by the plugin manager + UI.
//
// Roots come from DefaultPluginPaths.ForCurrentPlatform() or any caller-
// supplied list (LiteDB-persisted user paths in Phase B). Implementations
// must not throw for individual broken files — non-fatal issues are
// expected on collections like KB2UKA's, which mixes installer payloads
// with real plugins. Catalog-shaped output keeps that noise contained.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Zeus.PluginHost.Discovery;

public interface IPluginScanner
{
    Task<IReadOnlyList<PluginManifest>> ScanAsync(
        IEnumerable<string> roots,
        CancellationToken ct = default);
}
