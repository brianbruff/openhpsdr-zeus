using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Dsp;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// End-to-end endpoint test for <c>POST /api/mic-gain</c>: drives the real
/// endpoint via <see cref="WebApplicationFactory{TEntryPoint}"/>, asserting
/// that a request body of <c>{db}</c> reaches the DspPipelineService's
/// current engine as <c>SetTxPanelGain(10^(db/20))</c>.
///
/// PRD FR-3 (<c>docs/prd/12-tx-feature.md</c>) requires db → linear gain via
/// <c>10^(db/20)</c>; this test exercises that path through Program.cs so
/// the Math.Clamp + Math.Pow inlined on the handler can't drift from the
/// spec without a failing test.
/// </summary>
public class MicGainEndpointTests : IClassFixture<MicGainEndpointTests.Factory>
{
    private readonly Factory _factory;
    public MicGainEndpointTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task Post0db_SetsLinearGainOf1()
    {
        _factory.TestEngine.GainCalls.Clear();
        using var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/mic-gain", new { db = 0 });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var call = Assert.Single(_factory.TestEngine.GainCalls);
        Assert.Equal(1.0, call, precision: 6);
    }

    [Fact]
    public async Task Post20db_SetsLinearGainOf10()
    {
        _factory.TestEngine.GainCalls.Clear();
        using var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/mic-gain", new { db = 20 });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var call = Assert.Single(_factory.TestEngine.GainCalls);
        Assert.Equal(10.0, call, precision: 6);
    }

    [Fact]
    public async Task PostOutOfRange_ClampsTo0And20()
    {
        _factory.TestEngine.GainCalls.Clear();
        using var client = _factory.CreateClient();

        // db=-5 clamps to 0 → gain 1.0
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/mic-gain", new { db = -5 })).StatusCode);
        // db=50 clamps to 20 → gain 10.0
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/mic-gain", new { db = 50 })).StatusCode);

        Assert.Collection(_factory.TestEngine.GainCalls,
            v => Assert.Equal(1.0, v, precision: 6),
            v => Assert.Equal(10.0, v, precision: 6));
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public StubEngine TestEngine { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(services =>
            {
                // Replace every IHostedService registration so the real
                // DspPipelineService, TxMetersService, TxAudioIngestStartup
                // and TxTuneDriver do not spin up — we're only testing
                // the HTTP handler.
                services.RemoveAll<IHostedService>();

                // Swap the DspPipelineService singleton for a stubbed
                // subclass whose CurrentEngine is our recording stub.
                services.RemoveAll<DspPipelineService>();
                services.AddSingleton<DspPipelineService>(sp =>
                    new TestPipeline(
                        sp.GetRequiredService<RadioService>(),
                        sp.GetRequiredService<StreamingHub>(),
                        sp.GetRequiredService<ILoggerFactory>(),
                        TestEngine));
            });
        }
    }

    // Minimal IDspEngine that records SetTxPanelGain calls for assertion.
    // All other members are safe no-ops because the endpoint never calls them.
    public sealed class StubEngine : IDspEngine
    {
        public List<double> GainCalls { get; } = new();

        public void SetTxPanelGain(double linearGain) => GainCalls.Add(linearGain);

        public int TxBlockSamples => 1024;
        public int OpenChannel(int sampleRateHz, int pixelWidth) => 0;
        public void CloseChannel(int channelId) { }
        public void FeedIq(int channelId, ReadOnlySpan<double> interleavedIqSamples) { }
        public void SetMode(int channelId, RxMode mode) { }
        public void SetFilter(int channelId, int lowHz, int highHz) { }
        public void SetVfoHz(int channelId, long vfoHz) { }
        public void SetAgcTop(int channelId, double topDb) { }
        public void SetNoiseReduction(int channelId, NrConfig cfg) { }
        public void SetZoom(int channelId, int level) { }
        public int ReadAudio(int channelId, Span<float> output) => 0;
        public bool TryGetDisplayPixels(int channelId, DisplayPixout which, Span<float> dbOut) => false;
        public int OpenTxChannel() => 0;
        public void SetMox(bool moxOn) { }
        public double GetRxaSignalDbm(int channelId) => -140.0;
        public void SetTxMode(RxMode mode) { }
        public int ProcessTxBlock(ReadOnlySpan<float> micMono, Span<float> iqInterleaved) => 0;
        public void SetTxTune(bool on) { }
        public TxStageMeters GetTxStageMeters() => TxStageMeters.Silent;
        public void Dispose() { }
    }

    // Non-hosted subclass used only in tests. Overrides CurrentEngine so the
    // endpoint sees the StubEngine; leaves the base ExecuteAsync out of the
    // picture (the test factory removes all IHostedService registrations).
    private sealed class TestPipeline(
        RadioService radio,
        StreamingHub hub,
        ILoggerFactory logs,
        StubEngine engine) : DspPipelineService(radio, hub, logs)
    {
        public override IDspEngine CurrentEngine => engine;
    }
}
