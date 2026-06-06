using System;

using Assimalign.Cohesion.Http.Transports.Tests.TestObjects;
using Assimalign.Cohesion.Transports;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Transports.Tests;

public class HttpConnectionListenerOptionsTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListenerOptions: Should default backlog capacity to 512")]
    public void HttpConnectionListenerOptions_OnCreate_ShouldDefaultBacklogCapacity()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();

        // Assert
        options.BacklogCapacity.ShouldBe(512);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListenerOptions: Should reject backlog capacity less than one")]
    public void HttpConnectionListenerOptions_OnSetBacklogCapacity_ShouldRejectNonPositiveValues()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();

        // Act
        Should.Throw<ArgumentOutOfRangeException>(() => options.BacklogCapacity = 0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListenerOptions: Should aggregate configured protocols")]
    public void HttpConnectionListenerOptions_OnUseTransport_ShouldAggregateConfiguredProtocols()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();
        TestServerTransport http1Transport = new(TransportProtocol.Tcp, Array.Empty<TransportConnection>());
        TestServerTransport http3Transport = new(TransportProtocol.Quic, Array.Empty<TransportConnection>());

        // Act
        options
            .UseHttp(HttpProtocol.Http11, http1Transport)
            .UseHttp(HttpProtocol.Http30, http3Transport, isSecure: true);

        // Assert
        options.Protocols.ShouldBe(HttpProtocol.Http11 | HttpProtocol.Http30);
        options.Transports.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListenerOptions: Should add a TCP transport for HTTP/1.1")]
    public void HttpConnectionListenerOptions_OnUseHttp1_ShouldAddConfiguredTransport()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();

        // Act
        options.UseHttp1(configure => { });

        // Assert
        options.Protocols.ShouldBe(HttpProtocol.Http11);
        options.Transports.Count.ShouldBe(1);
        options.Transports.ShouldContain(transport => transport.Protocol == TransportProtocol.Tcp);
    }
}
