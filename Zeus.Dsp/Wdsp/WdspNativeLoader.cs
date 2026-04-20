using System.Reflection;
using System.Runtime.InteropServices;

namespace Nereus.Dsp.Wdsp;

internal static class WdspNativeLoader
{
    private static readonly object Gate = new();
    private static bool _registered;
    private static bool _probedLoadable;
    private static bool _loadable;

    internal static void EnsureResolverRegistered()
    {
        if (_registered) return;
        lock (Gate)
        {
            if (_registered) return;
            NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, Resolve);
            _registered = true;
        }
    }

    internal static bool TryProbe()
    {
        EnsureResolverRegistered();
        if (_probedLoadable) return _loadable;
        lock (Gate)
        {
            if (_probedLoadable) return _loadable;
            if (TryResolve(typeof(NativeMethods).Assembly, out var handle))
            {
                NativeLibrary.Free(handle);
                _loadable = true;
            }
            else
            {
                _loadable = false;
            }
            _probedLoadable = true;
            return _loadable;
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != NativeMethods.LibraryName) return IntPtr.Zero;
        return TryResolve(assembly, out var handle) ? handle : IntPtr.Zero;
    }

    private static bool TryResolve(Assembly assembly, out IntPtr handle)
    {
        foreach (var candidate in CandidatePaths(assembly))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out handle))
                return true;
        }
        return NativeLibrary.TryLoad(NativeMethods.LibraryName, assembly, null, out handle);
    }

    private static IEnumerable<string> CandidatePaths(Assembly assembly)
    {
        string rid = CurrentRid();
        string fileName = NativeFileName();
        string? asmDir = Path.GetDirectoryName(assembly.Location);
        if (!string.IsNullOrEmpty(asmDir))
        {
            yield return Path.Combine(asmDir, "runtimes", rid, "native", fileName);
            yield return Path.Combine(asmDir, fileName);
        }

        string baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "runtimes", rid, "native", fileName);
        yield return Path.Combine(baseDir, fileName);
    }

    private static string CurrentRid()
    {
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64",
        };
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return $"osx-{arch}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return $"linux-{arch}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return $"win-{arch}";
        return $"unknown-{arch}";
    }

    private static string NativeFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "libwdsp.dylib";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "libwdsp.so";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "wdsp.dll";
        return "libwdsp";
    }
}
