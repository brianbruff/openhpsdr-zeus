using System.Net;
using System.Net.Http.Json;
using System.Text;
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
/// End-to-end endpoint test for <c>POST /api/tx/leveler-max-gain</c>: drives
/// the real handler via <see cref="WebApplicationFactory{TEntryPoint}"/>,
/// asserting a JSON <c>{gain}</c> body reaches
/// <see cref="IDspEngine.SetTxLevelerMaxGain"/> on the current engine, and
/// that out-of-range values are rejected with a 400.
///
/// Frontend re-POSTs this on every slider move and on WS reconnect (task
/// #19), so the handler's range check is the only thing between a rogue
/// client and WDSP's Leveler ceiling.
/// </summary>
public class LevelerMaxGainEndpointTests : IClassFixture<LevelerMaxGainEndpointTests.Factory>
{
    private readonly Factory _factory;
    public LevelerMaxGainEndpointTests(Factory factory) => _factory = factory;

    [Theory]
    [InlineData(0.0)]   // band floor
    [InlineData(5.0)]   // W1AEX / softerhardware default
    [InlineData(15.0)]  // band ceiling (Thetis stock)
    public async Task PostInRange_CallsEngineWithSameValue(double gain)
    {
        _factory.TestEngine.LevelerMaxGainCalls.Clear();
        using var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/tx/leveler-max-gain", new { gain });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var call = Assert.Single(_factory.TestEngine.LevelerMaxGainCalls);
        Assert.Equal(gain, call, precision: 6);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(15.1)]
    public async Task PostOutOfRange_Returns400_AndDoesNotCallEngine(double gain)
    {
        _factory.TestEngine.LevelerMaxGainCalls.Clear();
        using var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/tx/leveler-max-gain", new { gain });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty(_factory.TestEngine.LevelerMaxGainCalls);
    }

    [Fact]
    public async Task PostNaN_Returns400_AndDoesNotCallEngine()
    {
        // System.Text.Json refuses to serialize NaN via PostAsJsonAsync, so
        // simulate a rogue client sending the JavaScript literal `NaN` by
        // posting raw string content. The server's guard
        // (double.IsNaN(req.Gain)) is defensive belt-and-braces against a
        // deserializer that later enables AllowNamedFloatingPointLiterals —
        // the test pins the rejection shape in place now.
        _factory.TestEngine.LevelerMaxGainCalls.Clear();
        using var client = _factory.CreateClient();
        using var content = new StringContent(
            "{\"gain\":NaN}", Encoding.UTF8, "application/json");

        var resp = await client.PostAsync("/api/tx/leveler-max-gain", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty(_factory.TestEngine.LevelerMaxGainCalls);
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public MicGainEndpointTests.StubEngine TestEngine { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(services =>
            {
                // Strip every IHostedService so the real DspPipelineService /
                // TxMetersService / TxAudioIngestStartup / TxTuneDriver never
                // spin up — we're only testing the HTTP handler.
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

        // Non-hosted subclass used only in tests. Mirrors
        // MicGainEndpointTests.TestPipeline so the stub drives CurrentEngine
        // while the base class's ExecuteAsync stays dormant.
        private sealed class TestPipeline(
            RadioService radio,
            StreamingHub hub,
            ILoggerFactory logs,
            MicGainEndpointTests.StubEngine engine) : DspPipelineService(radio, hub, logs)
        {
            public override IDspEngine CurrentEngine => engine;
        }
    }
}
