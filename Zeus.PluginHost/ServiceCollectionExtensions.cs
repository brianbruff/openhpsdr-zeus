// ServiceCollectionExtensions.cs — DI helper for the seam-wiring branch.
//
// Phase 1 deliberately does NOT call AddZeusPluginHost from
// Zeus.Server/Program.cs. The seam-wiring branch is happening separately
// and will land that one-line registration once the audio path is ready.
// Wiring the extension here ahead of time keeps the eventual seam PR a
// minimal, easy-to-review change.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zeus.PluginHost.Discovery;

namespace Zeus.PluginHost;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="IPluginHost"/> backed by
    /// <see cref="PluginHostManager"/> as a singleton, plus the
    /// discovery-side <see cref="IBinaryHeaderSniffer"/> and
    /// <see cref="IPluginScanner"/> services.
    /// </summary>
    /// <remarks>
    /// Do NOT call this from <c>Zeus.Server/Program.cs</c> in Phase 1.
    /// The seam-wiring task is on a separate branch; introducing the
    /// host into DI here would conflict with that work.
    /// </remarks>
    public static IServiceCollection AddZeusPluginHost(this IServiceCollection services)
    {
        services.TryAddSingleton<IPluginHost, PluginHostManager>();
        services.TryAddSingleton<IBinaryHeaderSniffer, BinaryHeaderSniffer>();
        services.TryAddSingleton<IPluginScanner, PluginScanner>();
        return services;
    }
}
