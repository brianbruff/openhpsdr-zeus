using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Dsp;
using Zeus.Protocol1;

namespace Zeus.Server;

/// <summary>
/// Keeps WDSP TXA's sample pump running when TUN is on but the mic uplink
/// isn't. TUN's post-generator tone lives inside the TXA chain
/// (<see cref="Zeus.Dsp.Wdsp.WdspDspEngine.SetTxTune"/>), so it only emits
/// IQ when <c>fexchange2</c> is called at the block rate. During MOX that
/// call is driven by <see cref="TxAudioIngest"/> as mic frames arrive; during
/// TUN we have no mic, so this service synthesises silent mic input at the
/// WDSP block cadence (1024 samples @ 48 kHz ≈ 21 ms).
///
/// Starts and stops via <see cref="TxService.IsTunOn"/> polling; not worth
/// building a subscription pattern for a feature that toggles at click rate.
/// </summary>
internal sealed class TxTuneDriver : BackgroundService
{
    private static readonly TimeSpan PollIdle = TimeSpan.FromMilliseconds(100);
    // 1024 mono samples / 48 kHz ≈ 21.33 ms. Round to 20 ms so we run a little
    // faster than WDSP's block clock and avoid starving the ring — drop-oldest
    // in TxIqRing handles the ~6 % overrun.
    private static readonly TimeSpan TuneTick = TimeSpan.FromMilliseconds(20);

    private readonly TxService _tx;
    private readonly DspPipelineService _pipeline;
    private readonly TxIqRing _ring;
    private readonly ILogger<TxTuneDriver> _log;

    public TxTuneDriver(TxService tx, DspPipelineService pipeline, TxIqRing ring, ILogger<TxTuneDriver> log)
    {
        _tx = tx;
        _pipeline = pipeline;
        _ring = ring;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        float[]? micScratch = null;
        float[]? iqScratch = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_tx.IsTunOn)
                {
                    await Task.Delay(PollIdle, ct).ConfigureAwait(false);
                    continue;
                }

                var engine = _pipeline.CurrentEngine;
                int blockSize = engine?.TxBlockSamples ?? 0;
                if (engine is null || blockSize <= 0)
                {
                    // No TXA yet — retry on the slow cadence.
                    await Task.Delay(PollIdle, ct).ConfigureAwait(false);
                    continue;
                }

                if (micScratch is null || micScratch.Length < blockSize)
                    micScratch = new float[blockSize];
                if (iqScratch is null || iqScratch.Length < 2 * blockSize)
                    iqScratch = new float[2 * blockSize];

                // Silent mic — the post-gen tone gets inserted after the mic
                // processing stage by WDSP, so fexchange2 still produces the
                // carrier even with zero mic input.
                Array.Clear(micScratch, 0, blockSize);
                int produced = engine.ProcessTxBlock(
                    new ReadOnlySpan<float>(micScratch, 0, blockSize),
                    new Span<float>(iqScratch, 0, 2 * blockSize));
                if (produced > 0)
                {
                    _ring.Write(new ReadOnlySpan<float>(iqScratch, 0, 2 * produced));
                }

                await Task.Delay(TuneTick, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "tx.tune driver tick failed");
                try { await Task.Delay(PollIdle, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
