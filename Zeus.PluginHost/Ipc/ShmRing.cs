// ShmRing.cs — managed-side mirror of openhpsdr-zeus-plughost/src/ipc/shm_ring.{h,cpp}.
//
// SPSC lock-free ring of fixed-size audio blocks. One producer, one
// consumer. Slot count is a power of two so the modulo reduces to a
// bitwise AND.
//
// Phase 2 backs the mapping with a real POSIX shared-memory region opened
// via libc shm_open + ftruncate, then mmap'd via
// MemoryMappedFile.CreateFromFile($"/dev/shm/<name>"). Both the .NET host
// and the C++ sidecar see the same pages by name.
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
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

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
/// SPSC lock-free shared-memory ring.
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

    /// <summary>Linux page size used to round the mmap size up.</summary>
    public const int PageBytes = 4096;

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly byte* _base;
    private readonly RingControlBlock* _control;
    private readonly byte* _slots;
    private readonly uint _slotMask;
    private readonly string? _shmName;
    private readonly bool _ownsName;

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

    /// <summary>POSIX shm name (with leading '/'), or null for anonymous.</summary>
    public string? ShmName => _shmName;

    /// <summary>True if this instance owns the shm name (and will shm_unlink
    /// at dispose).</summary>
    public bool OwnsName => _ownsName;

    private ShmRing(
        MemoryMappedFile mmf,
        MemoryMappedViewAccessor accessor,
        byte* basePtr,
        RingControlBlock* control,
        byte* slots,
        string? shmName,
        bool ownsName)
    {
        _mmf = mmf;
        _accessor = accessor;
        _base = basePtr;
        _control = control;
        _slots = slots;
        _slotMask = control->SlotCount - 1u;
        _shmName = shmName;
        _ownsName = ownsName;
    }

    /// <summary>
    /// Create an anonymous in-process mapping. Used by Phase 1 / unit tests
    /// only — the sidecar cannot see this mapping.
    /// </summary>
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
        // Round up to page boundary so Phase 2 layout matches.
        totalBytes = RoundUpToPage(totalBytes);

        var mmf = MemoryMappedFile.CreateNew(
            mapName: null,
            capacity: totalBytes,
            access: MemoryMappedFileAccess.ReadWrite);
        var accessor = mmf.CreateViewAccessor(
            0, totalBytes, MemoryMappedFileAccess.ReadWrite);

        return InitNew(mmf, accessor, totalBytes,
            frames, channels, sampleRate, slotCount,
            shmName: null, ownsName: false);
    }

    /// <summary>
    /// Create a POSIX shm region named <paramref name="shmName"/>, size it,
    /// and zero-init the control block. Caller (the host) owns the name and
    /// will shm_unlink on dispose.
    /// </summary>
    /// <param name="shmName">POSIX shm name with leading '/'. No further '/'.</param>
    public static ShmRing CreateNamed(
        string shmName,
        uint frames, uint channels, uint sampleRate, uint slotCount)
    {
        EnsurePosixShmSupported();
        ValidateShmName(shmName);
        if (slotCount == 0 || (slotCount & (slotCount - 1u)) != 0)
        {
            throw new ArgumentException(
                "slotCount must be a power of two", nameof(slotCount));
        }

        var slotBytes = RoundUpToCacheLine(BlockBytes(frames, channels));
        var logicalBytes = (long)Marshal.SizeOf<RingControlBlock>()
            + (long)slotBytes * slotCount;
        var totalBytes = RoundUpToPage(logicalBytes);

        // Recover from any previous crash that left the name in /dev/shm.
        Posix.shm_unlink(shmName);

        var fd = Posix.shm_open(shmName, Posix.O_CREAT | Posix.O_EXCL | Posix.O_RDWR, 0x180);
        if (fd < 0)
        {
            throw new IOException(
                $"shm_open(O_CREAT) failed for {shmName}: errno={Marshal.GetLastWin32Error()}");
        }
        if (Posix.ftruncate(fd, totalBytes) != 0)
        {
            int err = Marshal.GetLastWin32Error();
            Posix.close(fd);
            Posix.shm_unlink(shmName);
            throw new IOException($"ftruncate failed: errno={err}");
        }

        // Wrap the fd in a SafeFileHandle so MemoryMappedFile can mmap it.
        // Ownership transfers to the SafeFileHandle.
        var sfh = new SafeFileHandle(new IntPtr(fd), ownsHandle: true);

        MemoryMappedFile mmf;
        MemoryMappedViewAccessor accessor;
        try
        {
            mmf = MemoryMappedFile.CreateFromFile(
                fileHandle: sfh,
                mapName: null,
                capacity: totalBytes,
                access: MemoryMappedFileAccess.ReadWrite,
                inheritability: HandleInheritability.None,
                leaveOpen: false);
            accessor = mmf.CreateViewAccessor(
                0, totalBytes, MemoryMappedFileAccess.ReadWrite);
        }
        catch
        {
            try { sfh.Dispose(); } catch { /* best-effort */ }
            Posix.shm_unlink(shmName);
            throw;
        }

        return InitNew(mmf, accessor, totalBytes,
            frames, channels, sampleRate, slotCount,
            shmName: shmName, ownsName: true);
    }

    /// <summary>
    /// Open an already-created POSIX shm region named <paramref name="shmName"/>.
    /// The caller (e.g. a test harness emulating the sidecar) does NOT own
    /// the name and will not shm_unlink.
    /// </summary>
    public static ShmRing OpenExisting(
        string shmName,
        uint expectedFrames, uint expectedChannels,
        uint expectedSampleRate, uint expectedSlotCount)
    {
        EnsurePosixShmSupported();
        ValidateShmName(shmName);

        var fd = Posix.shm_open(shmName, Posix.O_RDWR, 0);
        if (fd < 0)
        {
            throw new IOException(
                $"shm_open(existing) failed for {shmName}: errno={Marshal.GetLastWin32Error()}");
        }

        long size = Posix.lseek(fd, 0, Posix.SEEK_END);
        if (size <= 0)
        {
            Posix.close(fd);
            throw new IOException("shm region is empty");
        }
        Posix.lseek(fd, 0, Posix.SEEK_SET);

        var sfh = new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
        var mmf = MemoryMappedFile.CreateFromFile(
            fileHandle: sfh, mapName: null, capacity: size,
            access: MemoryMappedFileAccess.ReadWrite,
            inheritability: HandleInheritability.None,
            leaveOpen: false);
        var accessor = mmf.CreateViewAccessor(
            0, size, MemoryMappedFileAccess.ReadWrite);

        byte* basePtr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);

        var control = (RingControlBlock*)basePtr;
        if (control->SlotCount  != expectedSlotCount  ||
            control->Frames     != expectedFrames     ||
            control->Channels   != expectedChannels   ||
            control->SampleRate != expectedSampleRate)
        {
            try { accessor.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { }
            accessor.Dispose();
            mmf.Dispose();
            throw new IOException("shm geometry mismatch with creator");
        }

        var slots = basePtr + Marshal.SizeOf<RingControlBlock>();
        return new ShmRing(mmf, accessor, basePtr, control, slots,
            shmName: shmName, ownsName: false);
    }

    private static ShmRing InitNew(
        MemoryMappedFile mmf, MemoryMappedViewAccessor accessor,
        long totalBytes,
        uint frames, uint channels, uint sampleRate, uint slotCount,
        string? shmName, bool ownsName)
    {
        var slotBytes = RoundUpToCacheLine(BlockBytes(frames, channels));

        byte* basePtr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);

        // Zero the whole mapping — the C++ side does memset(raw, 0, totalBytes).
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
        return new ShmRing(mmf, accessor, basePtr, control, slots, shmName, ownsName);
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

    private static long RoundUpToPage(long n)
    {
        return (n + (PageBytes - 1)) & ~((long)PageBytes - 1);
    }

    private static void EnsurePosixShmSupported()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            throw new PlatformNotSupportedException(
                "ShmRing.CreateNamed/OpenExisting require Linux or macOS in Phase 2.");
        }
    }

    private static void ValidateShmName(string name)
    {
        if (string.IsNullOrEmpty(name) || name[0] != '/')
        {
            throw new ArgumentException(
                "POSIX shm name must start with '/'", nameof(name));
        }
    }

    /// <summary>Idempotent shm_unlink — safe to call from cleanup paths.</summary>
    public static void Unlink(string shmName)
    {
        if (string.IsNullOrEmpty(shmName)) return;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return;
        Posix.shm_unlink(shmName);
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

        if (_ownsName && !string.IsNullOrEmpty(_shmName))
        {
            try { Posix.shm_unlink(_shmName); } catch { /* best-effort */ }
        }
    }
}

/// <summary>
/// libc P/Invokes for POSIX shared memory. Linux glibc + macOS dylib
/// expose all of these from "libc".
/// </summary>
internal static class Posix
{
    public const int O_RDWR  = 0x002;
    public const int O_CREAT = 0x40;   // Linux glibc
    public const int O_EXCL  = 0x80;   // Linux glibc

    public const int SEEK_SET = 0;
    public const int SEEK_END = 2;

    [DllImport("libc", EntryPoint = "shm_open", SetLastError = true,
               CharSet = CharSet.Ansi, BestFitMapping = false)]
    public static extern int shm_open(
        [MarshalAs(UnmanagedType.LPStr)] string name, int oflag, int mode);

    [DllImport("libc", EntryPoint = "shm_unlink", SetLastError = true,
               CharSet = CharSet.Ansi, BestFitMapping = false)]
    public static extern int shm_unlink(
        [MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport("libc", EntryPoint = "ftruncate", SetLastError = true)]
    public static extern int ftruncate(int fd, long length);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    public static extern int close(int fd);

    [DllImport("libc", EntryPoint = "lseek", SetLastError = true)]
    public static extern long lseek(int fd, long offset, int whence);
}
