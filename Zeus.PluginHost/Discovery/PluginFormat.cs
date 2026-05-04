// PluginFormat.cs — discriminator for the plugin container/standard.
//
// Discovery scans only assign formats it can identify by directory shape or
// file extension; binary loading lives later in the C++ sidecar.

namespace Zeus.PluginHost.Discovery;

public enum PluginFormat
{
    Unknown,
    Vst2,
    Vst3,
    Clap,
    Aax,
    Lv2,
}
