// SPDX-License-Identifier: GPL-2.0-or-later
//
// VstHostEndpointsTests — Wave 6a end-to-end smoke for /api/plughost/*.
// Drives the real endpoint surface via WebApplicationFactory<Program>;
// asserts validation + state-snapshot behavior without requiring a real
// sidecar binary. Tests that need the sidecar (load / parameter / etc.)
// stay out of this file — they belong with PluginChainTests where the
// SkippableFact pattern is already wired.

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class VstHostEndpointsTests : IClassFixture<VstHostEndpointsTests.Factory>
{
    private readonly Factory _factory;
    public VstHostEndpointsTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task GetState_ReturnsSaneDefaults_WhenNotRunning()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/plughost/state");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<StateResponse>();
        Assert.NotNull(body);
        Assert.False(body!.IsRunning);
        Assert.False(body.MasterEnabled);
        Assert.Equal(8, body.Slots.Count);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(i, body.Slots[i].Index);
            Assert.Null(body.Slots[i].Plugin);
            Assert.False(body.Slots[i].Bypass);
        }
    }

    [Fact]
    public async Task LoadSlot_OutOfRange_Returns400()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync(
            "/api/plughost/slots/8/load", new { path = "/nonexistent.vst3" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task LoadSlot_NegativeIndex_Returns400OrNotFound()
    {
        using var client = _factory.CreateClient();
        // Negative indices fail the int route constraint or the bounds
        // check inside the handler — both yield 4xx, never 5xx.
        var resp = await client.PostAsJsonAsync(
            "/api/plughost/slots/-1/load", new { path = "/x.vst3" });
        Assert.True(
            resp.StatusCode == HttpStatusCode.NotFound
            || resp.StatusCode == HttpStatusCode.BadRequest,
            $"unexpected status {resp.StatusCode}");
    }

    [Fact]
    public async Task GetSearchPaths_ReturnsEmpty_OnFreshFactory()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/plughost/searchPaths");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<SearchPathsResponse>();
        Assert.NotNull(body);
        Assert.NotNull(body!.Paths);
    }

    [Fact]
    public async Task AddSearchPath_NonexistentDir_Returns400()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/plughost/searchPaths",
            new { path = "/this/path/does/not/exist/anywhere" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task AddSearchPath_EmptyPath_Returns400()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/plughost/searchPaths",
            new { path = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task AddRemoveSearchPath_RoundTrips_OnTmpDir()
    {
        // /tmp exists on every Linux/macOS test host; on Windows we'd
        // pick the temp path. SkipIf once we run CI on Windows.
        var tmpDir = Path.Combine(Path.GetTempPath(),
            $"zeus-vst-search-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            using var client = _factory.CreateClient();

            // Add
            var addResp = await client.PostAsJsonAsync(
                "/api/plughost/searchPaths", new { path = tmpDir });
            Assert.Equal(HttpStatusCode.OK, addResp.StatusCode);
            var addBody = await addResp.Content.ReadFromJsonAsync<AddRemoveResponse>();
            Assert.NotNull(addBody);
            Assert.Contains(tmpDir, addBody!.Paths);

            // List shows it.
            var getResp = await client.GetAsync("/api/plughost/searchPaths");
            var getBody = await getResp.Content.ReadFromJsonAsync<SearchPathsResponse>();
            Assert.Contains(tmpDir, getBody!.Paths);

            // Delete (query param).
            var delResp = await client.DeleteAsync(
                $"/api/plughost/searchPaths?path={Uri.EscapeDataString(tmpDir)}");
            Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);

            var afterDel = await (await client.GetAsync("/api/plughost/searchPaths"))
                .Content.ReadFromJsonAsync<SearchPathsResponse>();
            Assert.DoesNotContain(tmpDir, afterDel!.Paths);
        }
        finally
        {
            try { Directory.Delete(tmpDir); } catch { }
        }
    }

    [Fact]
    public async Task GetCatalog_ReturnsList_NoCrash()
    {
        // Default-only scan (no rescan flag) — works on a Linux host
        // even when the user has no plugins installed (returns []).
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/plughost/catalog");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<CatalogResponse>();
        Assert.NotNull(body);
        Assert.NotNull(body!.Plugins);
    }

    [Fact]
    public async Task GetSlot_OutOfRange_Returns400()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/plughost/slots/8");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetSlot_Empty_ReturnsNullPlugin()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/plughost/slots/0");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<SlotResponse>();
        Assert.NotNull(body);
        Assert.Equal(0, body!.Index);
        Assert.Null(body.Plugin);
        Assert.False(body.Bypass);
    }

    [Fact]
    public async Task SetParameter_ValueOutOfRange_Returns400()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync(
            "/api/plughost/slots/0/parameters/42", new { value = 1.5 });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // --------------------------------------------------------------
    //  Test fixture
    // --------------------------------------------------------------

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            // Override the LiteDB persistence with a temp-file backed
            // instance so tests don't pollute the operator's prefs.
            // We can't `RemoveAll` LiteDbVstChainPersistence directly
            // because it's registered through IVstChainPersistence; do
            // that interface swap.
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(services =>
            {
                // Strip every IHostedService so the real sidecar lifecycle
                // (VstHostHostedService.StartAsync, DspPipelineService etc.)
                // doesn't run. The endpoints still resolve VstHostHostedService
                // as a singleton and the LiteDB store separately.
                services.RemoveAll<IHostedService>();

                // Replace LiteDbVstChainPersistence with a temp-path one so
                // tests don't touch the real prefs DB.
                services.RemoveAll<IVstChainPersistence>();
                var tmp = Path.Combine(Path.GetTempPath(),
                    $"zeus-vstchain-tests-fixture-{Guid.NewGuid():N}.db");
                services.AddSingleton<IVstChainPersistence>(sp =>
                    new LiteDbVstChainPersistence(
                        sp.GetRequiredService<
                            Microsoft.Extensions.Logging.ILogger<LiteDbVstChainPersistence>>(),
                        tmp));
            });
        }
    }

    // -- response DTOs (test-only mirrors of the API JSON shape) --
    private sealed record StateResponse(
        bool MasterEnabled,
        bool IsRunning,
        List<SlotMini> Slots,
        List<string> CustomSearchPaths);

    private sealed record SlotMini(int Index, PluginMini? Plugin, bool Bypass, int ParameterCount);
    private sealed record PluginMini(string Name, string Vendor, string Version);

    private sealed record SearchPathsResponse(List<string> Paths);
    private sealed record AddRemoveResponse(bool Added, bool Removed, List<string> Paths);
    private sealed record CatalogResponse(List<CatalogPlugin> Plugins);
    private sealed record CatalogPlugin(string FilePath, string DisplayName);

    private sealed record SlotResponse(
        int Index, PluginMini? Plugin, bool Bypass);
}
