// PluginPlatform.cs — host OS the plugin binary was built for.
//
// The scanner is cross-platform and may run on a Linux dev box even when
// most of the plugin collection is Windows binaries (KB2UKA's setup), so
// this is decoded purely from the file header, not Environment.OSVersion.

namespace Zeus.PluginHost.Discovery;

public enum PluginPlatform
{
    Unknown,
    Windows,
    Linux,
    MacOS,
}
