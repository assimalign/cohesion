using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Web.Proxy.Internal;
using Assimalign.Cohesion.Web.Proxy.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Proxy.Tests;

/// <summary>
/// The addresses a destination allowed only through a wildcard pattern may not reach: loopback,
/// link-local and unspecified, in either address family and behind an IPv4-mapped IPv6 address.
/// </summary>
public class ForwardProxyAddressPolicyTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [Theory(DisplayName = "Cohesion Test [Web.Proxy] - Address policy: Loopback, link-local and unspecified addresses are refused")]
    [InlineData("127.0.0.1")]
    [InlineData("127.10.20.30")]
    [InlineData("::1")]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("::")]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.0.1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:0.0.0.0")]
    public void IsRefused_InternalAddress_ShouldRefuse(string address)
    {
        // Act
        bool refused = ForwardProxyAddressPolicy.IsRefused(IPAddress.Parse(address));

        // Assert
        refused.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Proxy] - Address policy: Other addresses, private ranges included, are allowed")]
    [InlineData("93.184.216.34")]
    [InlineData("10.0.0.1")]
    [InlineData("192.168.1.10")]
    [InlineData("172.16.0.1")]
    [InlineData("128.0.0.1")]
    [InlineData("169.255.0.1")]
    [InlineData("2001:db8::1")]
    [InlineData("fec0::1")]
    [InlineData("::ffff:93.184.216.34")]
    public void IsRefused_OtherAddress_ShouldAllow(string address)
    {
        // Act
        bool refused = ForwardProxyAddressPolicy.IsRefused(IPAddress.Parse(address));

        // Assert
        refused.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Proxy] - Address policy: A destination that reaches only refused addresses is never dialed")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("localhost")]
    public async Task ConnectAsync_OnlyRefusedAddresses_ShouldThrowWithoutConnecting(string host)
    {
        // Arrange — a listener the refused address would reach, so a connection would be observable.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using TcpEchoServer server = TcpEchoServer.Start();

        // Act
        SocketException exception = await Should.ThrowAsync<SocketException>(
            async () => await ForwardProxyAddressPolicy.ConnectAsync(new DnsEndPoint(host, server.Port), cancellation.Token));

        // Assert
        exception.SocketErrorCode.ShouldBe(SocketError.AccessDenied);
        server.Accepted.ShouldBe(0);
    }
}
