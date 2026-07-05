using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class Protocol3SidecarControlForwarderTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-p3-controlfwd-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + ".pa")) File.Delete(_dbPath + ".pa"); } catch { }
    }

    [Fact]
    public void ReceiverSettingsDebounceMs_MatchesP3DisplayCadence()
    {
        Assert.Equal(20, Protocol3SidecarControlForwarder.ReceiverSettingsDebounceMs);
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("network not expected in these tests");
    }

    private sealed class Rig : IDisposable
    {
        public RadioService Radio { get; }
        public TxService Tx { get; }
        public Protocol3SidecarBridge Sidecar { get; }
        public Protocol3SidecarControlForwarder Forwarder { get; }
        private readonly DspSettingsStore _dspStore;
        private readonly PaSettingsStore _paStore;

        public Rig(string dbPath)
        {
            var lf = NullLoggerFactory.Instance;
            _dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, dbPath);
            _paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, dbPath + ".pa");
            Radio = new RadioService(lf, _dspStore, _paStore);
            var hub = new StreamingHub(NullLogger<StreamingHub>.Instance);
            var pipeline = new DspPipelineService(Radio, hub, Array.Empty<IRxAudioSink>(), lf);
            Tx = new TxService(Radio, pipeline, hub, NullBandPlanService.Instance, NullLogger<TxService>.Instance);
            // No diagnostics URL is ever configured on this bridge (no live
            // ConnectAsync), so every HTTP post the forwarder triggers
            // through it short-circuits to a no-op — see
            // Protocol3SidecarBridge.PostControlAsync. The ThrowingHttpClientFactory
            // documents that assumption: if a code path here ever DID try to
            // create an HttpClient, the test would fail loudly instead of
            // silently succeeding against real sockets.
            Sidecar = new Protocol3SidecarBridge(
                new ThrowingHttpClientFactory(),
                new ConfigurationBuilder().Build(),
                NullLogger<Protocol3SidecarBridge>.Instance);
            Forwarder = new Protocol3SidecarControlForwarder(
                Radio, Tx, Sidecar, NullLogger<Protocol3SidecarControlForwarder>.Instance);
        }

        public void Dispose()
        {
            try { Forwarder.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
            Forwarder.Dispose();
            Sidecar.Dispose();
            Radio.Dispose();
            _paStore.Dispose();
            _dspStore.Dispose();
        }
    }

    private dynamic Status(Rig rig) => rig.Forwarder.Status;

    [Fact]
    public async Task MoxChanged_IsIgnoredWhileNotOnProtocol3()
    {
        using var rig = new Rig(_dbPath);
        await rig.Forwarder.StartAsync(CancellationToken.None);

        // No MarkProtocol3Connected — the forwarder's OnRadioMoxChanged guards
        // on _radio.IsProtocol3Active and must not latch MOX for P1/P2/disconnected.
        rig.Radio.SetMox(true);

        Assert.False((bool)Status(rig).latchedMox);
        Assert.False((bool)Status(rig).active);
    }

    [Fact]
    public async Task MoxChanged_LatchesActiveWhileOnProtocol3()
    {
        using var rig = new Rig(_dbPath);
        rig.Radio.MarkProtocol3Connected("127.0.0.1:1024", 1_536_000, maxReceivers: 1);
        await rig.Forwarder.StartAsync(CancellationToken.None);

        rig.Radio.SetMox(true);

        Assert.True((bool)Status(rig).latchedMox);
        Assert.True((bool)Status(rig).active);
    }

    [Fact]
    public async Task MoxChanged_UnkeyEdgeAlwaysClearsTheLatch_NeverDedupedAway()
    {
        using var rig = new Rig(_dbPath);
        rig.Radio.MarkProtocol3Connected("127.0.0.1:1024", 1_536_000, maxReceivers: 1);
        await rig.Forwarder.StartAsync(CancellationToken.None);

        // Key down, then straight back up — the T/R relay's drop on unkey
        // must never be skipped, unlike steady-state RF-path pushes which
        // dedupe against the last-pushed Alex image while tuning within one
        // filter segment. Repeating the same edge twice in a row is the
        // shape a dedupe-by-value bug would silently swallow.
        rig.Radio.SetMox(true);
        Assert.True((bool)Status(rig).latchedMox);

        rig.Radio.SetMox(false);
        Assert.False((bool)Status(rig).latchedMox);
        Assert.False((bool)Status(rig).active);

        rig.Radio.SetMox(true);
        Assert.True((bool)Status(rig).latchedMox);
        Assert.True((bool)Status(rig).active);
    }

    [Fact]
    public async Task TunActiveChanged_LatchesTuneIndependentlyOfMox()
    {
        using var rig = new Rig(_dbPath);
        rig.Radio.MarkProtocol3Connected("127.0.0.1:1024", 1_536_000, maxReceivers: 1);
        await rig.Forwarder.StartAsync(CancellationToken.None);

        rig.Radio.NotifyTunActive(true);

        Assert.True((bool)Status(rig).latchedTune);
        Assert.False((bool)Status(rig).latchedMox);
        Assert.True((bool)Status(rig).tune);
        Assert.True((bool)Status(rig).active);

        rig.Radio.NotifyTunActive(false);

        Assert.False((bool)Status(rig).latchedTune);
        Assert.False((bool)Status(rig).active);
    }

    [Fact]
    public async Task TxActiveChanged_FromTxServiceLatchesIndependentlyOfHardwareMox()
    {
        using var rig = new Rig(_dbPath);
        rig.Radio.MarkProtocol3Connected("127.0.0.1:1024", 1_536_000, maxReceivers: 1);
        await rig.Forwarder.StartAsync(CancellationToken.None);

        // TxService.TxActiveChanged (via TrySetMox, the real production
        // trigger — see TxActiveChangedTests for the same pattern) is the
        // software-driven "is TX really active" signal, distinct from the
        // hardware MOX line RadioService.SetMox drives directly. It must
        // latch and clear through OnTxActiveChanged the same way the
        // hardware edges do.
        Assert.True(rig.Tx.TrySetMox(true, out var error1), error1);
        Assert.True((bool)Status(rig).latchedTxActive);
        Assert.True((bool)Status(rig).active);

        Assert.True(rig.Tx.TrySetMox(false, out var error2), error2);
        Assert.False((bool)Status(rig).latchedTxActive);
        Assert.False((bool)Status(rig).active);
    }

    [Fact]
    public void ShouldRefreshPaSnapshotForTxControl_RefreshesActiveMoxWhenDriveByteIsStaleZero()
    {
        Assert.True(Protocol3SidecarControlForwarder.ShouldRefreshPaSnapshotForTxControl(
            active: true,
            tune: false,
            drive: 0,
            drivePct: 43,
            tunePct: 1));
    }

    [Fact]
    public void ShouldRefreshPaSnapshotForTxControl_RefreshesActiveTuneWhenTuneDriveIsNonZero()
    {
        Assert.True(Protocol3SidecarControlForwarder.ShouldRefreshPaSnapshotForTxControl(
            active: true,
            tune: true,
            drive: 0,
            drivePct: 0,
            tunePct: 10));
    }

    [Theory]
    [InlineData(false, false, 0, 43, 1)]
    [InlineData(true, false, 43, 43, 1)]
    [InlineData(true, false, 0, 0, 1)]
    [InlineData(true, true, 0, 43, 0)]
    public void ShouldRefreshPaSnapshotForTxControl_DoesNotRefreshWhenZeroDriveIsIntentional(
        bool active,
        bool tune,
        byte drive,
        int drivePct,
        int tunePct)
    {
        Assert.False(Protocol3SidecarControlForwarder.ShouldRefreshPaSnapshotForTxControl(
            active,
            tune,
            drive,
            drivePct,
            tunePct));
    }
}
