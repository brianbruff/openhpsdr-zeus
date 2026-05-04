// Wakeup.cs — P/Invoke wrapper around POSIX named semaphores.
//
// Mirrors the C++ Wakeup class in openhpsdr-zeus-plughost. The host
// (this side) creates each named semaphore with O_CREAT; the sidecar
// opens by name without O_CREAT. Both sides Post + TimedWait. Host is
// the sole owner — it is the only side that ever calls sem_unlink.
//
// On Linux glibc, sem_open / sem_post / sem_timedwait / sem_close /
// sem_unlink live directly in libc.so.6. On macOS they live in
// libc.dylib. Windows is intentionally unsupported in Phase 2 — see
// docs/proposals/vst-host-phase2-wire.md, Section 8.

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Zeus.PluginHost.Ipc;

/// <summary>
/// Cross-process wakeup primitive backed by a POSIX named semaphore.
/// </summary>
public sealed class Wakeup : IDisposable
{
    private const int OCreat = 0x40;             // O_CREAT (Linux glibc)
    private static readonly IntPtr SemFailed = new(-1);

    // Linux glibc errno codes used by sem_timedwait.
    private const int EINTR = 4;

    private IntPtr _sem;
    private readonly string _name;
    private readonly bool _ownsName;
    private bool _disposed;

    /// <summary>Name passed to sem_open (with leading '/').</summary>
    public string Name => _name;

    /// <summary>True if this Wakeup created the named semaphore (and is
    /// therefore responsible for sem_unlink at dispose).</summary>
    public bool OwnsName => _ownsName;

    private Wakeup(IntPtr sem, string name, bool ownsName)
    {
        _sem = sem;
        _name = name;
        _ownsName = ownsName;
    }

    /// <summary>Create a new named semaphore (initial count 0). Used by the host.</summary>
    public static Wakeup Create(string name)
    {
        EnsureSupported();
        ValidateName(name);
        // Recover from any prior crash that left the name in /dev/shm/sem.*.
        sem_unlink(name);

        var sem = sem_open(name, OCreat, 0x180 /* 0600 */, 0);
        if (sem == SemFailed)
        {
            throw new IOException(
                $"sem_open(O_CREAT) failed for {name}: errno={Marshal.GetLastWin32Error()}");
        }
        return new Wakeup(sem, name, ownsName: true);
    }

    /// <summary>Open an already-created named semaphore. Used by the sidecar
    /// in tests; production sidecar uses the C++ side.</summary>
    public static Wakeup OpenExisting(string name)
    {
        EnsureSupported();
        ValidateName(name);
        var sem = sem_open(name, 0);
        if (sem == SemFailed)
        {
            throw new IOException(
                $"sem_open(existing) failed for {name}: errno={Marshal.GetLastWin32Error()}");
        }
        return new Wakeup(sem, name, ownsName: false);
    }

    /// <summary>
    /// Post (signal) the semaphore. The peer will wake from
    /// <see cref="TimedWait"/> with success.
    /// </summary>
    public void Post()
    {
        if (_disposed) return;
        if (sem_post(_sem) != 0)
        {
            // Realtime-safe: don't throw; just record. Phase 3 may surface
            // this through a logger, but Phase 2 treats post failure the
            // same as a missed wake — the timeout path will recover.
        }
    }

    /// <summary>
    /// Block on the semaphore until it is posted or <paramref name="timeout"/>
    /// elapses. Returns true on signal, false on timeout / error.
    /// </summary>
    public bool TimedWait(TimeSpan timeout)
    {
        if (_disposed) return false;

        if (timeout < TimeSpan.Zero) timeout = TimeSpan.Zero;

        // Build absolute deadline in CLOCK_REALTIME for sem_timedwait.
        // Linux glibc uses CLOCK_REALTIME; macOS lacks sem_timedwait
        // entirely (Phase 3 will deal with that — Linux is the Phase 2
        // ship target).
        var deadline = DateTimeOffset.UtcNow + timeout;
        long deadlineSec = deadline.ToUnixTimeSeconds();
        long fracTicks = deadline.UtcTicks
            - DateTimeOffset.FromUnixTimeSeconds(deadlineSec).UtcTicks;
        // 1 tick = 100 ns
        long deadlineNs = fracTicks * 100;
        var ts = new Timespec
        {
            tv_sec = deadlineSec,
            tv_nsec = deadlineNs,
        };

        while (true)
        {
            int rc = sem_timedwait(_sem, ref ts);
            if (rc == 0) return true;
            int err = Marshal.GetLastWin32Error();
            if (err == EINTR) continue;
            return false;  // ETIMEDOUT or other
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_sem != IntPtr.Zero && _sem != SemFailed)
        {
            sem_close(_sem);
            _sem = IntPtr.Zero;
        }
        if (_ownsName && !string.IsNullOrEmpty(_name))
        {
            sem_unlink(_name);
        }
    }

    /// <summary>Idempotent unlink — safe to call when the host wants to
    /// reclaim the name without disposing the handle yet.</summary>
    public static void Unlink(string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        if (!IsSupported) return;
        sem_unlink(name);
    }

    private static void EnsureSupported()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException(
                "Zeus.PluginHost.Ipc.Wakeup requires Linux or macOS in Phase 2. " +
                "Windows support lands in Phase 3 (named events).");
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name[0] != '/')
        {
            throw new ArgumentException(
                "POSIX semaphore name must start with '/'", nameof(name));
        }
    }

    /// <summary>True on Linux/macOS where libc has the POSIX symbols.</summary>
    public static bool IsSupported =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long tv_sec;
        public long tv_nsec;
    }

    // ---- libc P/Invokes -------------------------------------------------
    //
    // Both Linux glibc (libc.so.6) and macOS (libc.dylib) export sem_*
    // symbols. .NET's default name resolution finds the right shared
    // object on each platform via "libc"; we override only when we need
    // to. EntryPoint matches the C symbol exactly.

    [DllImport("libc", EntryPoint = "sem_open", SetLastError = true,
               CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern IntPtr sem_open(
        [MarshalAs(UnmanagedType.LPStr)] string name, int oflag, int mode, uint value);

    [DllImport("libc", EntryPoint = "sem_open", SetLastError = true,
               CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern IntPtr sem_open(
        [MarshalAs(UnmanagedType.LPStr)] string name, int oflag);

    [DllImport("libc", EntryPoint = "sem_post", SetLastError = true)]
    private static extern int sem_post(IntPtr sem);

    [DllImport("libc", EntryPoint = "sem_timedwait", SetLastError = true)]
    private static extern int sem_timedwait(IntPtr sem, ref Timespec abs_timeout);

    [DllImport("libc", EntryPoint = "sem_close", SetLastError = true)]
    private static extern int sem_close(IntPtr sem);

    [DllImport("libc", EntryPoint = "sem_unlink", SetLastError = true,
               CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern int sem_unlink(
        [MarshalAs(UnmanagedType.LPStr)] string name);
}
