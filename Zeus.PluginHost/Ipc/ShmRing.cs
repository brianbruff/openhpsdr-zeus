// ShmRing.cs — managed-side mirror of openhpsdr-zeus-plughost/src/ipc/shm_ring.{h,cpp}.
//
// SPSC lock-free ring of fixed-size audio blocks. One producer, one
// consumer. Slot count is a power of two so the modulo reduces to a
// bitwise AND.
//
// Phase 1 backs the mapping with an anonymous, process-local
// MemoryMappedFile. That gives us the exact same in-memory layout that
// shm_open + mmap will produce in Phase 2 (when the sidecar opens the
// same name and shares the pages); the only thing that changes when we
// upgrade is the mapping creation. The realtime contract — Acquire /
// Publish / Read / Release do NO syscalls, NO mallocs, NO locks — holds
// today and is what the audio thread relies on.
//
// Wire layout (must match C++ RingControlBlock byte-for-byte):
//
//   offset  size  field
//   ------  ----  ------------------------------------
//        0     8  head (u64, atomic)
//        8    56  pad0 (head's cache-line filler)
//       64     8  tail (u64, atomic)
//       72    56  pad1 (tail's cache-line filler)
//      128     4  slotCount     (power of two)
//      132     4  slotBytes     (header + planar payload, cache-line rounded)
//      136     4  frames
//      140     4  channels
//      144     4  sampleRate
//      148    12  reserved[3]   (zero on write)
//      ----  ----
//      total: 160 bytes (cache-line multiple — C++ static_asserts this)
//
// After the control block come slotCount * slotBytes contiguous bytes of
// slots. Each slot starts with a BlockHeader and is followed by its
// planar float32 payload, padded out to a cache line.

using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Zeus.PluginHost.Ipc;

/// <summary>
/// Wire-compatible mirror of the C++ <c>RingControlBlock</c>. The two atomic
/// indices live on their own cache lines to avoid false sharing between the
/// producer and consumer threads.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 160, Pack = 8)]
internal unsafe struct RingControlBlock
{
    public ulong Head;            // offset 0   (atomic)
    public fixed byte Pad0[56];   // offset 8   (cache-line fill to 64)

    public ulong Tail;            // offset 64  (atomic)
    public fixed byte Pad1[56];   // offset 72  (cache-line fill to 128)

    public uint SlotCount;        // offset 128
    public uint SlotBytes;        // offset 132
    public uint Frames;           // offset 136
    public uint Channels;         // offset 140
    public uint SampleRate;       // offset 144
    public fixed uint Reserved[3];// offset 148 (12 bytes -> total 160)
}

/// <summary>
/// SPSC lock-free shared-memory ring. Phase 1 owns the backing memory via
/// <see cref="MemoryMappedFile"/>; Phase 2 will open a named shm region
/// shared with the sidecar process.
/// </summary>
public sealed unsafe class ShmRing : IDisposable
{
    /// <summary>
    /// Cache-line size used to round slot bytes up. Matches the constant in
    /// the C++ <c>shm_ring.cpp</c>; both ends MUST use the same value or the
    /// slot stride will diverge.
    /// </summary>
    public const int CacheLineBytes = 64;

    /// <summary>BlockHeader is one cache line by spec.</summary>
    public const int BlockHeaderBytes = 64;

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly byte* _base;
    private readonly RingControlBlock* _control;
    private readonly byte* _slots;
    private readonly uint _slotMask;

    private bool _disposed;

    /// <summary>Total number of slots in the ring (power of two).</summary>
    public uint SlotCount => _control->SlotCount;

    /// <summary>Bytes per slot (header + planar payload + cache-line pad).</summary>
    public uint SlotBytes => _control->SlotBytes;

    /// <summary>Frames per block.</summary>
    public uint Frames => _control->Frames;

    /// <summary>Channels per block.</summary>
    public uint Channels => _control->Channels;

    /// <summary>Sample rate of the block payload in Hz.</summary>
    public uint SampleRate => _control->SampleRate;

    private ShmRing(
        MemoryMappedFile mmf,
        MemoryMappedViewAccessor accessor,
        byte* basePtr,
        RingControlBlock* control,
        byte* slots)
    {
        _mmf = mmf;
        _accessor = accessor;
        _base = basePtr;
        _control = control;
        _slots = slots;
        _slotMask = control->SlotCount - 1u;
    }

    /// <summary>
    /// Create an anonymous Phase 1 mapping that the host owns end-to-end.
    /// In Phase 2 this becomes a named shm region opened from both the
    /// host and the sidecar.
    /// </summary>
    /// <param name="frames">Samples per channel in each slot. Phase 1 = 256.</param>
    /// <param name="channels">Channel count per slot. Phase 1 = 1.</param>
    /// <param name="sampleRate">Sample rate stamped into each slot. Phase 1 = 48000.</param>
    /// <param name="slotCount">Number of slots; must be a power of two. Phase 1 = 8.</param>
    public static ShmRing CreateAnonymous(
        uint frames, uint channels, uint sampleRate, uint slotCount)
    {
        if (slotCount == 0 || (slotCount & (slotCount - 1u)) != 0)
        {
            throw new ArgumentException(
                "slotCount must be a power of two", nameof(slotCount));
        }

        var slotBytes = RoundUpToCacheLine(BlockBytes(frames, channels));
        var totalBytes = (long)Marshal.SizeOf<RingControlBlock>()
            + (long)slotBytes * slotCount;

        // Anonymous (null name) mapping. Suitable for Phase 1 in-process
        // testing; Phase 2 names the mapping so the sidecar can open it.
        var mmf = MemoryMappedFile.CreateNew(
            mapName: null,
            capacity: totalBytes,
            access: MemoryMappedFileAccess.ReadWrite);
        var accessor = mmf.CreateViewAccessor(
            0, totalBytes, MemoryMappedFileAccess.ReadWrite);

        byte* basePtr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);

        // Zero the whole mapping — the C++ side does memset(raw, 0, totalBytes)
        // immediately after allocation, and we must match.
        new Span<byte>(basePtr, (int)totalBytes).Clear();

        var control = (RingControlBlock*)basePtr;
        control->Head = 0;
        control->Tail = 0;
        control->SlotCount = slotCount;
        control->SlotBytes = slotBytes;
        control->Frames = frames;
        control->Channels = channels;
        control->SampleRate = sampleRate;

        var slots = basePtr + Marshal.SizeOf<RingControlBlock>();
        return new ShmRing(mmf, accessor, basePtr, control, slots);
    }

    // ---- producer side --------------------------------------------------

    /// <summary>
    /// Acquire a writeable slot, or <c>null</c> if the ring is full. Caller
    /// fills the BlockHeader and payload, then calls <see cref="Publish"/>.
    /// Realtime safe: relaxed load + acquire load only.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BlockHeader* Acquire()
    {
        var head = Volatile.Read(ref _control->Head);
        var tail = Volatile.Read(ref _control->Tail);
        if (head - tail >= _control->SlotCount)
        {
            return null;
        }
        return SlotAt(head);
    }

    /// <summary>
    /// Publish the slot last returned by <see cref="Acquire"/>. The header
    /// pointer is implicit (current head index) — the parameter exists so
    /// callers don't lose track of which slot they were filling.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Publish(BlockHeader* header)
    {
        _ = header;
        var head = _control->Head;
        Volatile.Write(ref _control->Head, head + 1);
    }

    // ---- consumer side --------------------------------------------------

    /// <summary>
    /// Peek the next readable slot, or <c>null</c> if the ring is empty.
    /// Caller consumes, then calls <see cref="Release"/> to advance the tail.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BlockHeader* Read()
    {
        var tail = Volatile.Read(ref _control->Tail);
        var head = Volatile.Read(ref _control->Head);
        if (head == tail)
        {
            return null;
        }
        return SlotAt(tail);
    }

    /// <summary>Advance the tail. Must be paired with a successful <see cref="Read"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Release(BlockHeader* header)
    {
        _ = header;
        var tail = _control->Tail;
        Volatile.Write(ref _control->Tail, tail + 1);
    }

    // ---- payload helpers ------------------------------------------------

    /// <summary>Pointer to the planar float32 payload immediately after a header.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float* PayloadOf(BlockHeader* header)
    {
        return (float*)(header + 1);
    }

    /// <summary>Bytes occupied by header + planar payload (no cache-line padding).</summary>
    public static uint BlockBytes(uint frames, uint channels)
    {
        return (uint)BlockHeaderBytes + frames * channels * sizeof(float);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private BlockHeader* SlotAt(ulong index)
    {
        var i = index & _slotMask;
        return (BlockHeader*)(_slots + i * _control->SlotBytes);
    }

    private static uint RoundUpToCacheLine(uint n)
    {
        return (n + (uint)(CacheLineBytes - 1)) & ~(uint)(CacheLineBytes - 1);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
        catch
        {
            // Already released or never acquired — Dispose() must not throw.
        }
        _accessor.Dispose();
        _mmf.Dispose();
    }
}
