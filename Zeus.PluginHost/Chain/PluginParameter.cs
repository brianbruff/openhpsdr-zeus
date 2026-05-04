// PluginParameter.cs — host-side snapshot of one VST3 parameter.
//
// Wire-mirrors C++ zeus::plughost::vst3::ParamInfo plus the matching
// SlotParamListResult payload. Values are kept in normalized 0..1 form
// because that is what the VST3 IEditController contract publishes; UI
// layers are free to convert to physical units via per-plugin metadata
// (Phase 3b).

using System;

namespace Zeus.PluginHost.Chain;

/// <summary>
/// Bitmask of VST3 ParameterInfo flags relevant to the host. Maps 1:1 to
/// the bits the sidecar packs into the wire-spec u8 flags field.
/// </summary>
[Flags]
public enum ParameterFlags : byte
{
    None        = 0,
    ReadOnly    = 1,   // bit 0: kIsReadOnly
    Automatable = 2,   // bit 1: kCanAutomate
    Hidden      = 4,   // bit 2: kIsHidden
    List        = 8,   // bit 3: kIsList (enum-style, stepCount values)
}

/// <summary>
/// One parameter exposed by a plugin's IEditController. <see cref="Id"/>
/// is opaque to Zeus — pass it back unchanged to <c>SetSlotParameterAsync</c>.
/// All values are normalized [0,1]; the plugin owns the mapping back to
/// physical units.
/// </summary>
public sealed record PluginParameter(
    uint Id,
    string Name,
    string Units,
    double DefaultValue,
    double CurrentValue,
    int StepCount,
    ParameterFlags Flags);
