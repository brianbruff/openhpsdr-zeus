using Zeus.Dsp.Wdsp;

namespace Zeus.Server;

/// <summary>
/// Kicks off WDSPwisdom on a worker thread at app start so first-connect
/// isn't blocked for ~2 minutes while FFTW runs FFTW_PATIENT across sizes
/// 64..262144. Returns from StartAsync immediately — Kestrel must not wait
/// on wisdom generation.
/// </summary>
public sealed class WisdomBootstrapService : IHostedService
{
    private readonly WdspWisdomInitializer _initializer;

    public WisdomBootstrapService(WdspWisdomInitializer initializer)
    {
        _initializer = initializer;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = _initializer.EnsureInitializedAsync();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
