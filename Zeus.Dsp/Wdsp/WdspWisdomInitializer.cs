using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;

namespace Zeus.Dsp.Wdsp;

/// <summary>
/// Owns the one-shot WDSPwisdom FFTW plan-cache initialisation. Singleton —
/// the WDSP wisdom file at $LOCALAPPDATA/Zeus/wdspWisdom00 is process-global
/// state, so there's nothing to parallelise.
/// </summary>
public sealed class WdspWisdomInitializer
{
    private readonly ILogger _log;
    private readonly object _gate = new();
    private Task? _task;
    private int _phase = (int)WisdomPhase.Idle;

    public WdspWisdomInitializer(ILogger<WdspWisdomInitializer>? logger = null)
    {
        _log = logger ?? NullLogger<WdspWisdomInitializer>.Instance;
    }

    public WisdomPhase Phase => (WisdomPhase)Volatile.Read(ref _phase);

    public event Action<WisdomPhase>? PhaseChanged;

    /// <summary>
    /// Idempotent. First call kicks off the WDSPwisdom P/Invoke on a worker
    /// thread and returns a Task tracking it. Subsequent calls (including
    /// re-entrance from WdspDspEngine) return the same Task.
    /// </summary>
    public Task EnsureInitializedAsync()
    {
        lock (_gate)
        {
            if (_task is not null) return _task;
            SetPhase(WisdomPhase.Building);
            WdspNativeLoader.EnsureResolverRegistered();
            _task = Task.Run(RunWisdom);
            return _task;
        }
    }

    private void RunWisdom()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Zeus");
            Directory.CreateDirectory(dir);
            _log.LogInformation("wdsp.wisdom initialising dir={Dir}", dir);
            int result = NativeMethods.WDSPwisdom(dir);
            var status = Marshal.PtrToStringUTF8(NativeMethods.wisdom_get_status()) ?? string.Empty;
            _log.LogInformation(
                "wdsp.wisdom ready result={Result} ({Source}) status={Status}",
                result, result == 0 ? "loaded" : "built", status);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "wdsp.wisdom failed — subsequent FFT planning will take the slow path");
        }
        finally
        {
            SetPhase(WisdomPhase.Ready);
        }
    }

    private void SetPhase(WisdomPhase next)
    {
        var prev = (WisdomPhase)Interlocked.Exchange(ref _phase, (int)next);
        if (prev != next) PhaseChanged?.Invoke(next);
    }
}
