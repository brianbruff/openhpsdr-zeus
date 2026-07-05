using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class DspPipelineAudioRoutingTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-audio-routing-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + ".pa")) File.Delete(_dbPath + ".pa"); } catch { }
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void ShouldPublishNormalRxAudio_RequiresMonitorOffAndTxUnsuppressed(
        bool txMonitorOn,
        bool txAudioSuppressed,
        bool expected)
    {
        Assert.Equal(
            expected,
            DspPipelineService.ShouldPublishNormalRxAudio(txMonitorOn, txAudioSuppressed));
    }

    [Fact]
    public void SetMox_ArmsTxSuppressionThenPostTxDrain()
    {
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath);
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        var radio = new RadioService(NullLoggerFactory.Instance, dspStore, paStore);
        var hub = new StreamingHub(new NullLogger<StreamingHub>());
        var pipeline = new DspPipelineService(radio, hub, Array.Empty<IRxAudioSink>(), NullLoggerFactory.Instance);

        pipeline.SetMox(true);
        Assert.True(pipeline.RxAudioSuppressedForTx);
        Assert.Equal(0, pipeline.RxPostTxMuteBlocksRemaining);

        pipeline.SetMox(false);
        Assert.False(pipeline.RxAudioSuppressedForTx);
        Assert.InRange(pipeline.RxPostTxMuteBlocksRemaining, 1, 10);
    }
}
