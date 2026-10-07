using System;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Authentication;
using Assimalign.Cohesion.Web.Authorization;
using Assimalign.Cohesion.Web.Hosting;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.WebSockets.Tests.TestObjects;

using Shouldly;

using Xunit;

using NetHttpMethod = System.Net.Http.HttpMethod;
using NetHttpStatusCode = System.Net.HttpStatusCode;
using NetHttpVersion = System.Net.HttpVersion;

namespace Assimalign.Cohesion.Web.WebSockets.Tests;

/// <summary>
/// <c>MapWebSocket</c> end to end: one routed endpoint serves the HTTP/1.1 upgrade, the HTTP/2 extended
/// CONNECT and the HTTP/3 extended CONNECT; a request that is not a handshake never reaches the
/// handler; route-group policies apply to the socket endpoint; and the endpoint keeps the origin
/// policy when <c>UseWebSockets</c> is not registered.
/// </summary>
public class WebSocketEndpointTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [Theory(DisplayName = "Cohesion Test [Web.WebSockets] - MapWebSocket: The same endpoint serves the handshake over HTTP/1.1, HTTP/2 and HTTP/3")]
    [InlineData(WebSocketTestProtocol.Http1)]
    [InlineData(WebSocketTestProtocol.Http2)]
    [InlineData(WebSocketTestProtocol.Http3)]
    public async Task MapWebSocket_SameEndpoint_ServesEveryProtocol(WebSocketTestProtocol protocol)
    {
        // Arrange — a routed endpoint with a route value, behind the policy.
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        StrongBox<int> invocations = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(
            protocol,
            builder => builder.AddRouting(),
            application =>
            {
                application.UseRouting();
                application.UseWebSockets();
                application.MapWebSocket("/chat/{room}", GreetAndEcho(invocations));
            },
            cancellationToken);

        // Act
        string[] received = await ExchangeAsync(server, protocol, "/chat/lobby", "hello", cancellationToken);

        // Assert — the handler ran once with the route value, and the socket echoed.
        received.ShouldBe(new[] { "lobby:", "hello" });
        invocations.Value.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Web.WebSockets] - MapWebSocket: A request that is not a handshake gets 400 and another method 405, without running the handler")]
    [InlineData(WebSocketTestProtocol.Http1)]
    [InlineData(WebSocketTestProtocol.Http2)]
    public async Task MapWebSocket_PlainRequest_Answers400WithoutRunningTheHandler(WebSocketTestProtocol protocol)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        StrongBox<int> invocations = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(
            protocol,
            builder => builder.AddRouting(),
            application =>
            {
                application.UseRouting();
                application.UseWebSockets();
                application.MapWebSocket("/chat/{room}", GreetAndEcho(invocations));
            },
            cancellationToken);
        using HttpClient client = CreateHttpClient(protocol);

        // Act
        using HttpResponseMessage get = await client.SendAsync(CreateRequest(protocol, NetHttpMethod.Get, server, "/chat/lobby"), cancellationToken);
        using HttpResponseMessage post = await client.SendAsync(CreateRequest(protocol, NetHttpMethod.Post, server, "/chat/lobby"), cancellationToken);

        // Assert — the route takes GET and CONNECT; the router refuses the rest.
        get.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
        post.StatusCode.ShouldBe(NetHttpStatusCode.MethodNotAllowed);
        post.Content.Headers.Allow.ShouldContain("GET");
        post.Content.Headers.Allow.ShouldContain("CONNECT");
        invocations.Value.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.WebSockets] - MapWebSocket: A route group's authorization policy guards the socket endpoint on every handshake shape")]
    [InlineData(WebSocketTestProtocol.Http1)]
    [InlineData(WebSocketTestProtocol.Http2)]
    public async Task MapWebSocket_GroupWithAuthorizationPolicy_GuardsTheHandshake(WebSocketTestProtocol protocol)
    {
        // Arrange — the group requires the "chat" role; its endpoint is mapped through the group.
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        StrongBox<int> invocations = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(
            protocol,
            builder =>
            {
                builder.AddRouting();
                builder.AddAuthentication(HeaderAuthenticationHandler.Scheme).AddScheme(HeaderAuthenticationHandler.CreateScheme());
                builder.AddAuthorization();
            },
            application =>
            {
                IRouterBuilder routes = application.UseRouting();
                application.UseAuthentication();
                application.UseAuthorization();
                application.UseWebSockets();

                IRouterGroupBuilder secure = routes.MapGroup("/secure/{room}").RequireAuthorization(policy => policy.RequireRole("chat"));
                secure.MapWebSocket("", GreetAndEcho(invocations));
            },
            cancellationToken);

        // Act
        (WebSocketException? anonymous, NetHttpStatusCode? anonymousStatus) = await TryConnectAsync(server, protocol, "/secure/ops", user: null, cancellationToken);
        (WebSocketException? reader, NetHttpStatusCode? readerStatus) = await TryConnectAsync(server, protocol, "/secure/ops", user: "bob;role=reader", cancellationToken);
        string[] admitted = await ExchangeAsync(server, protocol, "/secure/ops", "hi", cancellationToken, user: "alice;role=chat");

        // Assert — refused before the handler with the scheme's 401 and 403; admitted as alice.
        anonymous.ShouldNotBeNull();
        anonymousStatus.ShouldBe(NetHttpStatusCode.Unauthorized);
        reader.ShouldNotBeNull();
        readerStatus.ShouldBe(NetHttpStatusCode.Forbidden);
        admitted.ShouldBe(new[] { "ops:alice", "hi" });
        invocations.Value.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - MapWebSocket: Without UseWebSockets the endpoint keeps the default origin policy")]
    public async Task MapWebSocket_WithoutUseWebSockets_RefusesCrossSiteHandshake()
    {
        // Arrange — no UseWebSockets in the pipeline.
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        StrongBox<int> invocations = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(
            WebSocketTestProtocol.Http1,
            builder => builder.AddRouting(),
            application =>
            {
                application.UseRouting();
                application.MapWebSocket("/chat/{room}", GreetAndEcho(invocations));
            },
            cancellationToken);
        using ClientWebSocket crossSite = new();
        crossSite.Options.SetRequestHeader("Origin", "https://attacker.example");
        crossSite.Options.CollectHttpResponseDetails = true;

        // Act
        await Should.ThrowAsync<WebSocketException>(() => crossSite.ConnectAsync(server.WebSocketUri("/chat/lobby"), cancellationToken));
        string[] sameSite = await ExchangeAsync(server, WebSocketTestProtocol.Http1, "/chat/lobby", "ok", cancellationToken, origin: server.Origin);

        // Assert
        crossSite.HttpStatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
        sameSite.ShouldBe(new[] { "lobby:", "ok" });
        invocations.Value.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - MapWebSocket: The accept options are chosen per handshake from the request surface")]
    public async Task MapWebSocket_AcceptOptions_SelectAnOfferedSubprotocol()
    {
        // Arrange — the endpoint speaks chat.v1 only, and picks it when the client offers it.
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        StrongBox<int> invocations = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(
            WebSocketTestProtocol.Http2,
            builder => builder.AddRouting(),
            application =>
            {
                application.UseRouting();
                application.UseWebSockets();
                application.MapWebSocket(
                    "/chat/{room}",
                    GreetAndEcho(invocations),
                    context => new HttpWebSocketAcceptOptions
                    {
                        SubProtocol = context.WebSockets.RequestedProtocols.Contains("chat.v1") ? "chat.v1" : null,
                    });
            },
            cancellationToken);
        using HttpMessageInvoker invoker = new(new SocketsHttpHandler());
        using ClientWebSocket client = CreateClient(WebSocketTestProtocol.Http2);
        client.Options.AddSubProtocol("chat.v2");
        client.Options.AddSubProtocol("chat.v1");

        // Act
        await client.ConnectAsync(server.WebSocketUri("/chat/lobby"), invoker, cancellationToken);
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);

        // Assert
        client.SubProtocol.ShouldBe("chat.v1");
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - MapWebSocket: Mapping without AddRouting throws with the fix")]
    public void MapWebSocket_WithoutRouting_ThrowsInvalidOperationException()
    {
        // Arrange
        WebApplication application = WebApplication.CreateBuilder().Build();

        // Act / Assert
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => application.MapWebSocket("/ws", (context, socket) => Task.CompletedTask));
        exception.Message.ShouldContain("AddRouting()");
    }

    /// <summary>
    /// A handler that sends <c>{room}:{user}</c>, then echoes every message until the peer closes.
    /// </summary>
    private static Func<IHttpContext, WebSocket, Task> GreetAndEcho(StrongBox<int> invocations) => async (context, socket) =>
    {
        Interlocked.Increment(ref invocations.Value);

        string room = context.GetRouteMatch()?.Values?["room"] as string ?? string.Empty;
        string user = context.User.Identity?.Name ?? string.Empty;
        await socket.SendAsync(Encoding.UTF8.GetBytes($"{room}:{user}"), WebSocketMessageType.Text, true, context.RequestCancelled);

        byte[] buffer = new byte[256];

        while (true)
        {
            WebSocketReceiveResult received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestCancelled);

            if (received.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(received.CloseStatus ?? WebSocketCloseStatus.NormalClosure, received.CloseStatusDescription, CancellationToken.None);
                return;
            }

            await socket.SendAsync(new ArraySegment<byte>(buffer, 0, received.Count), received.MessageType, received.EndOfMessage, context.RequestCancelled);
        }
    };

    /// <summary>
    /// Opens a socket to <paramref name="path"/>, reads the greeting, sends <paramref name="message"/>,
    /// reads its echo, and closes; returns the two messages received.
    /// </summary>
    private static async Task<string[]> ExchangeAsync(
        WebSocketTestServer server,
        WebSocketTestProtocol protocol,
        string path,
        string message,
        CancellationToken cancellationToken,
        string? user = null,
        string? origin = null)
    {
        if (protocol == WebSocketTestProtocol.Http3)
        {
            await using Http3TestClient http3 = await Http3TestClient.ConnectAsync(server.Http3Listener!, cancellationToken);
            (Http3RequestStream stream, Http3ResponseHead head) = await http3.ConnectWebSocketAsync(
                new[] { ("sec-websocket-version", "13") },
                cancellationToken,
                path);
            head.StatusCode.ShouldBe(200);

            using WebSocket socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = false, KeepAliveInterval = TimeSpan.Zero });
            return await ConverseAsync(socket, message, cancellationToken);
        }

        using HttpMessageInvoker invoker = new(new SocketsHttpHandler());
        using ClientWebSocket client = CreateClient(protocol);

        if (user is not null)
        {
            client.Options.SetRequestHeader(HeaderAuthenticationHandler.UserHeader, user);
        }

        if (origin is not null)
        {
            client.Options.SetRequestHeader("Origin", origin);
        }

        await client.ConnectAsync(server.WebSocketUri(path), invoker, cancellationToken);
        return await ConverseAsync(client, message, cancellationToken);
    }

    private static async Task<string[]> ConverseAsync(WebSocket socket, string message, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[256];
        WebSocketReceiveResult greeting = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
        string first = Encoding.UTF8.GetString(buffer, 0, greeting.Count);

        await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, cancellationToken);
        WebSocketReceiveResult echo = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
        string second = Encoding.UTF8.GetString(buffer, 0, echo.Count);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);
        return new[] { first, second };
    }

    private static async Task<(WebSocketException? Failure, NetHttpStatusCode? Status)> TryConnectAsync(
        WebSocketTestServer server,
        WebSocketTestProtocol protocol,
        string path,
        string? user,
        CancellationToken cancellationToken)
    {
        using HttpMessageInvoker invoker = new(new SocketsHttpHandler());
        using ClientWebSocket client = CreateClient(protocol);

        if (user is not null)
        {
            client.Options.SetRequestHeader(HeaderAuthenticationHandler.UserHeader, user);
        }

        try
        {
            await client.ConnectAsync(server.WebSocketUri(path), invoker, cancellationToken);
            await client.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);
            return (null, client.HttpStatusCode);
        }
        catch (WebSocketException exception)
        {
            return (exception, client.HttpStatusCode);
        }
    }

    private static ClientWebSocket CreateClient(WebSocketTestProtocol protocol)
    {
        ClientWebSocket client = new();
        client.Options.KeepAliveInterval = TimeSpan.Zero;
        client.Options.CollectHttpResponseDetails = true;

        if (protocol == WebSocketTestProtocol.Http2)
        {
            client.Options.HttpVersion = NetHttpVersion.Version20;
            client.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }

        return client;
    }

    private static HttpClient CreateHttpClient(WebSocketTestProtocol protocol)
    {
        HttpClient client = new(new SocketsHttpHandler());

        if (protocol == WebSocketTestProtocol.Http2)
        {
            client.DefaultRequestVersion = NetHttpVersion.Version20;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }

        return client;
    }

    private static HttpRequestMessage CreateRequest(WebSocketTestProtocol protocol, NetHttpMethod method, WebSocketTestServer server, string path)
    {
        HttpRequestMessage request = new(method, $"http://127.0.0.1:{server.Port}{path}");

        if (protocol == WebSocketTestProtocol.Http2)
        {
            request.Version = NetHttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }

        if (method == NetHttpMethod.Post)
        {
            request.Content = new StringContent("not a socket");
        }

        return request;
    }
}
