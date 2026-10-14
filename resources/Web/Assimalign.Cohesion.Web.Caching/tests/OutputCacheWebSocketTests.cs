using System;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Testing;

using Shouldly;

using Xunit;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpVersion = System.Net.HttpVersion;

namespace Assimalign.Cohesion.Web.Caching.Tests;

/// <summary>
/// The output cache and WebSockets on one URL, end to end over the in-memory transport: a cached
/// <c>GET</c> never answers a WebSocket handshake (the HTTP/1.1 upgrade or the HTTP/2 extended
/// CONNECT), and an exchange the endpoint took over with the switch is never stored for the URL's
/// later requests.
/// </summary>
public class OutputCacheWebSocketTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _longDuration = TimeSpan.FromMinutes(30);

    [Theory(DisplayName = "Cohesion Test [Web.Caching] - E2E: A cached GET never answers a WebSocket handshake for the same URL")]
    [InlineData(WebApplicationTestProtocol.Http1)]
    [InlineData(WebApplicationTestProtocol.Http2)]
    public async Task UseOutputCache_CachedUrl_ShouldStillOpenWebSockets(WebApplicationTestProtocol protocol)
    {
        // Arrange — one URL serves both: a cacheable page, and a WebSocket echo.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        await using WebApplicationTestFactory factory = new(new WebApplicationTestFactoryOptions { Protocol = protocol });
        int pages = 0;
        ConfigureEchoOrPage(factory, () => Interlocked.Increment(ref pages));
        using HttpClient client = factory.CreateClient();

        // Act — the page is cached, then the handshake for the same URL, then the page again.
        string first = await client.GetStringAsync("/live", cancellationToken);
        string cached = await client.GetStringAsync("/live", cancellationToken);
        string echoed = await EchoAsync(client, protocol, "hello", cancellationToken);
        string afterSocket = await client.GetStringAsync("/live", cancellationToken);

        // Assert — the socket opened and echoed; the page stayed cached throughout.
        echoed.ShouldBe("hello");
        first.ShouldBe("page-1");
        cached.ShouldBe("page-1");
        afterSocket.ShouldBe("page-1");
        pages.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - E2E: A WebSocket exchange is never stored for the URL's later requests")]
    public async Task UseOutputCache_WebSocketFirst_ShouldNotStoreTheTakenOverExchange()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        await using WebApplicationTestFactory factory = new();
        int pages = 0;
        ConfigureEchoOrPage(factory, () => Interlocked.Increment(ref pages));
        using HttpClient client = factory.CreateClient();

        // Act — the handshake reaches the URL first; its exchange is taken over by the 101.
        string echoed = await EchoAsync(client, WebApplicationTestProtocol.Http1, "first", cancellationToken);
        string page = await client.GetStringAsync("/live", cancellationToken);
        string secondEcho = await EchoAsync(client, WebApplicationTestProtocol.Http1, "second", cancellationToken);

        // Assert — the page came from the endpoint, not from an empty 200 stored for the handshake.
        echoed.ShouldBe("first");
        page.ShouldBe("page-1");
        secondEcho.ShouldBe("second");
        pages.ShouldBe(1);
    }

    private static void ConfigureEchoOrPage(WebApplicationTestFactory factory, Func<int> nextPage)
    {
        factory.Application.UseOutputCache(options => options.AddBasePolicy(policy => policy.Duration = _longDuration));
        factory.Application.Use(async (context, next) =>
        {
            IHttpWebSocketFeature webSockets = context.WebSockets;

            if (!webSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"page-{nextPage()}"), context.RequestCancelled);
                return;
            }

            using WebSocket socket = await webSockets.AcceptWebSocketAsync(
                new HttpWebSocketAcceptOptions { KeepAliveInterval = TimeSpan.Zero },
                context.RequestCancelled);
            byte[] buffer = new byte[256];

            while (true)
            {
                WebSocketReceiveResult received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestCancelled);

                if (received.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return;
                }

                await socket.SendAsync(new ArraySegment<byte>(buffer, 0, received.Count), received.MessageType, received.EndOfMessage, context.RequestCancelled);
            }
        });
    }

    private static async Task<string> EchoAsync(HttpClient client, WebApplicationTestProtocol protocol, string message, CancellationToken cancellationToken)
    {
        using ClientWebSocket socket = new();
        socket.Options.KeepAliveInterval = TimeSpan.Zero;

        if (protocol == WebApplicationTestProtocol.Http2)
        {
            socket.Options.HttpVersion = NetHttpVersion.Version20;
            socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }

        await socket.ConnectAsync(new Uri("ws://localhost/live"), client, cancellationToken);
        await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, cancellationToken);

        byte[] buffer = new byte[256];
        WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);

        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }
}
