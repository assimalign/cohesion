using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Transports.Tests.TestObjects;
using Assimalign.Cohesion.Transports;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Transports.Tests;

public class HttpConnectionListenerTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListener: Should accept and adapt an HTTP/1.1 transport connection")]
    public async Task HttpConnectionListener_OnAcceptOrListenAsync_ShouldAdaptConfiguredTransportConnection()
    {
        // Arrange
        TestTransportConnectionContext context = new(HttpProtocolPayloadFactory.CreateHttp1Request("GET / HTTP/1.1\r\nHost: localhost\r\n\r\n"));
        TestSingleStreamTransportConnection connection = new(context, TransportProtocol.Tcp);
        TestServerTransport transport = new(TransportProtocol.Tcp, new ITransportConnection[] { connection });
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http11, transport);

        await using HttpConnectionListener listener = new(options);

        // Act
        IHttpConnection httpConnection = await listener.AcceptOrListenAsync();
        IHttpConnectionContext httpConnectionContext = await httpConnection.OpenAsync();

        // Assert
        httpConnection.Protocol.ShouldBe(TransportProtocol.Http);
        httpConnectionContext.LocalEndPoint.ShouldBe(context.LocalEndPoint);
        httpConnectionContext.RemoteEndPoint.ShouldBe(context.RemoteEndPoint);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListener: Should re-arm transport accepts and backlog connections")]
    public async Task HttpConnectionListener_OnSequentialAccepts_ShouldBacklogConnections()
    {
        // Arrange
        TestTransportConnectionContext firstContext = new(HttpProtocolPayloadFactory.CreateHttp1Request("GET /first HTTP/1.1\r\nHost: localhost\r\n\r\n"));
        TestTransportConnectionContext secondContext = new(HttpProtocolPayloadFactory.CreateHttp1Request("GET /second HTTP/1.1\r\nHost: localhost\r\n\r\n"));
        TestSingleStreamTransportConnection firstConnection = new(firstContext, TransportProtocol.Tcp);
        TestSingleStreamTransportConnection secondConnection = new(secondContext, TransportProtocol.Tcp);
        QueuedTestServerTransport transport = new(TransportProtocol.Tcp);
        HttpConnectionListenerOptions options = new();
        options.UseTransport(HttpProtocol.Http11, transport);
        transport.Enqueue(firstConnection);

        await using HttpConnectionListener listener = new(options);

        // Act
        IHttpConnection acceptedFirstConnection = await listener.AcceptOrListenAsync();
        await transport.WaitingForConnection.WaitAsync(TimeSpan.FromSeconds(1));
        transport.Enqueue(secondConnection);
        IHttpConnection acceptedSecondConnection = await listener.AcceptOrListenAsync().WaitAsync(TimeSpan.FromSeconds(1));

        // Assert
        acceptedFirstConnection.Id.ShouldBe(firstConnection.Id);
        acceptedSecondConnection.Id.ShouldBe(secondConnection.Id);
        transport.InitializeAsyncCount.ShouldBeGreaterThanOrEqualTo(2);
    }
}
