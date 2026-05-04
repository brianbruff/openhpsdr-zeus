// SPDX-License-Identifier: GPL-2.0-or-later
//
// CapabilitiesEndpointTests — smoke for /api/capabilities. Exercises the
// real endpoint via WebApplicationFactory; the inner platform / sidecar
// gates are pure functions and don't need the test factory to fake them.

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class CapabilitiesEndpointTests : IClassFixture<CapabilitiesEndpointTests.Factory>
{
    private readonly Factory _factory;
    public CapabilitiesEndpointTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task Get_ReturnsExpectedShape()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/capabilities");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<CapabilitiesResponse>();
        Assert.NotNull(body);
        // Default test factory leaves HostMode at the Server default.
        Assert.Equal("server", body!.Host);
        // Platform should be one of the known three (CI runs Linux; dev
        // boxes are macOS / Windows). Anything else means the detector is
        // broken.
        Assert.Contains(body.Platform, new[] { "linux", "darwin", "windows" });
        // Architecture is a runtime-info string lower-cased; assert it's
        // populated rather than pinning to a specific value.
        Assert.False(string.IsNullOrWhiteSpace(body.Architecture));
        Assert.NotNull(body.Features);
        Assert.NotNull(body.Features!.VstHost);

        // Off-Linux: VST host must be unavailable with a reason. On Linux
        // it depends on whether the sidecar binary resolves on the test
        // host — assert the structural invariant only (reason set iff
        // unavailable).
        if (body.Platform != "linux")
        {
            Assert.False(body.Features.VstHost!.Available);
            Assert.False(string.IsNullOrWhiteSpace(body.Features.VstHost.Reason));
        }
        else
        {
            var gate = body.Features.VstHost!;
            if (gate.Available)
            {
                Assert.Null(gate.Reason);
                Assert.False(string.IsNullOrWhiteSpace(gate.SidecarPath));
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(gate.Reason));
            }
        }
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(services =>
            {
                // Keep the test surface tight: real CapabilitiesService
                // resolves fine on its own (pure config + locator probe).
                // Strip hosted services so the sidecar lifecycle / DSP
                // pipeline don't run.
                services.RemoveAll<IHostedService>();
            });
        }
    }

    private sealed record CapabilitiesResponse(
        string Host,
        string Platform,
        string Architecture,
        string Version,
        FeaturesResponse Features);

    private sealed record FeaturesResponse(VstHostResponse VstHost);

    private sealed record VstHostResponse(
        bool Available,
        string? Reason,
        string? SidecarPath);
}
