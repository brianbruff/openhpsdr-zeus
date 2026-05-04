// ChainSlot.cs — host-side snapshot of one slot in the plugin chain.
//
// Slots are addressable 0..(MaxChainSlots-1). A slot is empty when
// <see cref="Plugin"/> is null. <see cref="Bypass"/> applies only when a
// plugin is loaded — bypassing an empty slot is a no-op.
// <see cref="Parameters"/> is a cached snapshot of the plugin's parameter
// list at the time of the last ListSlotParametersAsync call; it can lag
// reality if the plugin's controller has notified parameter changes
// without the host re-listing.

using System.Collections.Generic;

namespace Zeus.PluginHost.Chain;

public sealed record ChainSlot(
    int Index,
    LoadedPluginInfo? Plugin,
    bool Bypass,
    IReadOnlyList<PluginParameter> Parameters);

/// <summary>
/// Compile-time chain constants. <see cref="MaxSlots"/> matches the C++
/// PluginChain::kMaxSlots — both must agree.
/// </summary>
public static class ChainConstants
{
    public const int MaxSlots = 8;
}
