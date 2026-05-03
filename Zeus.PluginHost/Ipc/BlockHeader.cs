// BlockHeader.cs — exact wire-compatible mirror of the C++ struct in
// openhpsdr-zeus-plughost/src/audio/block_format.h.
//
// CRITICAL: this layout is load-bearing. Changing field order, sizes, or
// the reserved-block length will desync the host from the sidecar with no
// runtime warning until the audio path silently corrupts. The
// PluginHostManager startup check throws if sizeof(BlockHeader) drifts
// away from 64 bytes.
//
// Field order, exactly as on the wire:
//   uint64  seq         (8)
//   uint32  frames      (4)
//   uint32  channels    (4)
//   uint32  sampleRate  (4)
//   uint32  flags       (4)
//   byte[40] reserved  (40)
//                     ----
//                       64 bytes
//
// All fields are little-endian on the only supported targets (x86_64 and
// arm64-LE). Pack=8 keeps the uint64 seq naturally aligned without padding.
// Size=64 pins the total — together with the static-asserted layout that
// matches the C++ side this becomes our wire-compatibility guarantee.

using System.Runtime.InteropServices;

namespace Zeus.PluginHost.Ipc;

[StructLayout(LayoutKind.Sequential, Size = 64, Pack = 8)]
public unsafe struct BlockHeader
{
    /// <summary>Monotonic sequence number; writer increments before publishing.</summary>
    public ulong Seq;

    /// <summary>Samples per channel in this block.</summary>
    public uint Frames;

    /// <summary>Channel count. Phase 1 is mono (1).</summary>
    public uint Channels;

    /// <summary>Sample rate in Hz. Phase 1 is fixed at 48000.</summary>
    public uint SampleRate;

    /// <summary>Bit flags, see <see cref="BlockFlags"/>.</summary>
    public uint Flags;

    /// <summary>Reserved tail. Must be zero on write. Fixed buffer keeps
    /// the struct directly memcpy-compatible with the C++ <c>uint8_t
    /// reserved[40]</c>.</summary>
    public fixed byte Reserved[40];
}
