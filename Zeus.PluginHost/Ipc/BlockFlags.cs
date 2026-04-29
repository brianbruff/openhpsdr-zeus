// BlockFlags.cs — bit flags for BlockHeader.flags. Wire-shared with the
// C++ sidecar at openhpsdr-zeus-plughost/src/audio/block_format.h. The
// numeric values must NOT change without bumping the protocol version
// over the control-plane Hello message.

using System;

namespace Zeus.PluginHost.Ipc;

[Flags]
public enum BlockFlags : uint
{
    /// <summary>Empty / no flags set.</summary>
    None = 0,

    /// <summary>Host should pass input straight through (no plugin run).</summary>
    Bypass = 1u << 0,

    /// <summary>Payload may be undefined; consumer should treat as zeros.</summary>
    Silence = 1u << 1,

    // bits 2..31 reserved, MUST be zero on write.
}
