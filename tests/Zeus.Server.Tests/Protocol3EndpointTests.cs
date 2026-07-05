using System.Net;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class Protocol3EndpointTests
{
    [Fact]
    public void SameIpEndpoint_MatchesAddressAndPort()
    {
        var requested = new IPEndPoint(IPAddress.Parse("192.168.1.25"), 1030);

        Assert.True(ZeusEndpoints.SameIpEndpoint("192.168.1.25:1030", requested));
    }

    [Fact]
    public void SameIpEndpoint_RejectsDifferentPort()
    {
        var requested = new IPEndPoint(IPAddress.Parse("192.168.1.25"), 1030);

        Assert.False(ZeusEndpoints.SameIpEndpoint("192.168.1.25:1024", requested));
    }

    [Fact]
    public void SameIpAddress_MatchesDifferentPort()
    {
        var requested = IPAddress.Parse("192.168.1.25");

        Assert.True(ZeusEndpoints.SameIpAddress("192.168.1.25:1024", requested));
    }

    [Fact]
    public void SameIpAddress_RejectsDifferentAddress()
    {
        var requested = IPAddress.Parse("192.168.1.25");

        Assert.False(ZeusEndpoints.SameIpAddress("192.168.1.26:1030", requested));
    }

    [Fact]
    public void SameIpEndpoint_RejectsInvalidEndpoint()
    {
        var requested = new IPEndPoint(IPAddress.Parse("192.168.1.25"), 1030);

        Assert.False(ZeusEndpoints.SameIpEndpoint("not-an-endpoint", requested));
    }
}
