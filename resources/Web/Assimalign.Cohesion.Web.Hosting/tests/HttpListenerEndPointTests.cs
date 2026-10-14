using System;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.Quic;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

public class HttpListenerEndPointTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - HTTP listener: Port-zero TCP bind publishes the assigned endpoint")]
    public async Task BindAsync_OnPortZeroTcp_ShouldExposeBoundEndPoint()
    {
        // Arrange
        TcpConnectionListener transport = TcpConnectionListener.Create(
            options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
        await using HttpConnectionListener listener = HttpConnectionListener.Create(options => options.UseHttp1(() => transport));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        listener.EndPoints.ShouldHaveSingleItem().ShouldBeOfType<IPEndPoint>().Port.ShouldBe(0);

        // Act
        await listener.BindAsync(timeout.Token);

        // Assert
        EndPoint endPoint = listener.EndPoints.ShouldHaveSingleItem();
        endPoint.ShouldBe(transport.EndPoint);
        endPoint.ShouldBeOfType<IPEndPoint>().Port.ShouldBeGreaterThan(0);
        listener.Connections.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - HTTP listener: Port-zero QUIC bind publishes the assigned endpoint")]
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task BindAsync_OnPortZeroQuic_ShouldExposeBoundEndPoint()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using X509Certificate2 certificate = SelfSignedCertificateFactory.Create("localhost");
        QuicConnectionListenerOptions transportOptions = new()
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        };
        transportOptions.ServerAuthenticationOptions.ServerCertificate = certificate;
        transportOptions.ServerAuthenticationOptions.ApplicationProtocols = [SslApplicationProtocol.Http3];
        QuicConnectionListener transport = new(transportOptions);
        await using HttpConnectionListener listener = HttpConnectionListener.Create(options => options.UseHttp3(() => transport));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        // Act
        await listener.BindAsync(timeout.Token);

        // Assert
        EndPoint endPoint = listener.EndPoints.ShouldHaveSingleItem();
        endPoint.ShouldBe(transport.EndPoint);
        endPoint.ShouldBeOfType<IPEndPoint>().Port.ShouldBeGreaterThan(0);
        listener.Connections.ShouldBeEmpty();
    }
}
