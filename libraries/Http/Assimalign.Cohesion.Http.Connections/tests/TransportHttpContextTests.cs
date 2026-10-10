using System.Collections.Generic;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Pins the request and response back-reference on every transport (#699): the exchange context
/// constructs its own request and response and passes itself to each, so both resolve the owning
/// context from the moment the exchange is dispatched.
/// </summary>
public class TransportHttpContextTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Exchange: An HTTP/1.1 request and response should resolve their owning context")]
    public async Task ReceiveAsync_Http1Exchange_ShouldResolveOwningContextFromRequestAndResponse()
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request("GET / HTTP/1.1\r\nHost: api.test\r\n\r\n");
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(new TestConnection(payload)), static _ => { });
        HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        // Act
        await using IAsyncEnumerator<IHttpContext> exchanges = connectionContext.ReceiveAsync().GetAsyncEnumerator();
        (await exchanges.MoveNextAsync()).ShouldBeTrue();

        // Assert
        ShouldResolveOwningContext(exchanges.Current);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Exchange: An HTTP/2 request and response should resolve their owning context")]
    public async Task ReceiveAsync_Http2Exchange_ShouldResolveOwningContextFromRequestAndResponse()
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/"));

        // Act
        IHttpContext exchange = await peer.ReceiveContextAsync();

        // Assert
        ShouldResolveOwningContext(exchange);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Exchange: An HTTP/3 request and response should resolve their owning context")]
    public async Task ReceiveAsync_Http3Exchange_ShouldResolveOwningContextFromRequestAndResponse()
    {
        // Arrange
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/", "https", "api.test"));
        request.Output.Complete();

        // Act
        IHttpContext exchange = await peer.NextContextAsync();

        // Assert
        ShouldResolveOwningContext(exchange);
    }

    private static void ShouldResolveOwningContext(IHttpContext exchange)
    {
        exchange.Request.HttpContext.ShouldBeSameAs(exchange);
        exchange.Response.HttpContext.ShouldBeSameAs(exchange);

        // The abstract base view resolves the same instance through the concrete-typed members.
        HttpContext context = exchange.ShouldBeAssignableTo<HttpContext>()!;
        context.Request.HttpContext.ShouldBeSameAs(context);
        context.Response.HttpContext.ShouldBeSameAs(context);
    }
}
