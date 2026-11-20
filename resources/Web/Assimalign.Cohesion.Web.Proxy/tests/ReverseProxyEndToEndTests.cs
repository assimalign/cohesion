using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Proxy.Tests.TestObjects;
using Assimalign.Cohesion.Web.Testing;

using Shouldly;

using Xunit;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpStatusCode = System.Net.HttpStatusCode;
using NetHttpVersion = System.Net.HttpVersion;

namespace Assimalign.Cohesion.Web.Proxy.Tests;

/// <summary>
/// The reverse proxy end to end: clients reach it over the test factory's transport, and it forwards to
/// real Web applications on loopback sockets.
/// </summary>
public class ReverseProxyEndToEndTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - E2E: A matched request is forwarded and the destination's response relayed")]
    public async Task UseReverseProxy_MatchedRoute_ShouldForwardAndRelay()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using ProxyTestServer backend = await ProxyTestServer.StartAsync(context => ProxyTestBackend.DescribeRequestAsync(context, "a"), cancellation.Token);
        await using WebApplicationTestFactory factory = new();
        Compose(factory, proxy => proxy.Options.AddRoute(new ReverseProxyRoute { Name = "api", PathPrefix = "/api", Destinations = [backend.Uri] }));
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/api/users?b=2&a=1&a=3", cancellation.Token);
        IReadOnlyDictionary<string, string> received = ProxyTestBackend.Parse(await response.Content.ReadAsStringAsync(cancellation.Token));

        // Assert — the query text arrives as the client sent it, and the destination's own authority is the Host.
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        response.Headers.GetValues("X-Backend").ShouldBe(["a"]);
        received["method"].ShouldBe("GET");
        received["path"].ShouldBe("/api/users");
        received["query"].ShouldBe("b=2&a=1&a=3");
        received["host"].ShouldBe(backend.Authority);
        received["x-forwarded-host"].ShouldBe("localhost");
        received["x-forwarded-proto"].ShouldBe("http");
        received["via"].ShouldStartWith("1.1 ");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - E2E: A request body is forwarded")]
    public async Task UseReverseProxy_RequestBody_ShouldForwardTheBody()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using ProxyTestServer backend = await ProxyTestServer.StartAsync(context => ProxyTestBackend.DescribeRequestAsync(context, "a"), cancellation.Token);
        await using WebApplicationTestFactory factory = new();
        Compose(factory, proxy => proxy.Options.AddRoute(new ReverseProxyRoute { Name = "api", Destinations = [backend.Uri] }));
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsync("/items", new StringContent("hello, destination"), cancellation.Token);
        IReadOnlyDictionary<string, string> received = ProxyTestBackend.Parse(await response.Content.ReadAsStringAsync(cancellation.Token));

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        received["method"].ShouldBe("POST");
        received["body"].ShouldBe("hello, destination");
    }

    [Theory(DisplayName = "Cohesion Test [Web.Proxy] - E2E: The destination receives the path the route forwards under its own base path")]
    [InlineData(false, "/api/users/7")]
    [InlineData(true, "/users/7")]
    public async Task UseReverseProxy_Transforms_ShouldForwardTheRoutePath(bool stripPrefix, string expectedBelowBase)
    {
        // Arrange — the destination carries a base path the forwarded path goes under.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using ProxyTestServer backend = await ProxyTestServer.StartAsync(context => ProxyTestBackend.DescribeRequestAsync(context, "a"), cancellation.Token);
        await using WebApplicationTestFactory factory = new();
        Compose(factory, proxy => proxy.Options.AddRoute(new ReverseProxyRoute
        {
            Name = "api",
            PathPrefix = "/api",
            StripPrefix = stripPrefix,
            Destinations = [new Uri(backend.Uri, "/base/")],
        }));
        using HttpClient client = factory.CreateClient();

        // Act
        string body = await client.GetStringAsync("/api/users/7", cancellation.Token);

        // Assert
        ProxyTestBackend.Parse(body)["path"].ShouldBe("/base" + expectedBelowBase);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - E2E: A route's destinations take its requests in turn")]
    public async Task UseReverseProxy_SeveralDestinations_ShouldRotate()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using ProxyTestServer a = await ProxyTestServer.StartAsync(context => ProxyTestBackend.DescribeRequestAsync(context, "a"), cancellation.Token);
        await using ProxyTestServer b = await ProxyTestServer.StartAsync(context => ProxyTestBackend.DescribeRequestAsync(context, "b"), cancellation.Token);
        await using WebApplicationTestFactory factory = new();
        Compose(factory, proxy => proxy.Options.AddRoute(new ReverseProxyRoute { Name = "pool", Destinations = [a.Uri, b.Uri] }));
        using HttpClient client = factory.CreateClient();

        // Act
        List<string> served = new();
        for (int i = 0; i < 4; i++)
        {
            using HttpResponseMessage response = await client.GetAsync("/", cancellation.Token);
            served.Add(response.Headers.GetValues("X-Backend").Single());
        }

        // Assert
        served.ShouldBe(["a", "b", "a", "b"]);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Proxy] - E2E: The request's host picks the route")]
    [InlineData("shop.example", "shop")]
    [InlineData("api.shop.example", "shop")]
    [InlineData("localhost", "default")]
    public async Task UseReverseProxy_HostRoutes_ShouldRouteOnTheHost(string host, string expected)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using ProxyTestServer shop = await ProxyTestServer.StartAsync(context => ProxyTestBackend.DescribeRequestAsync(context, "shop"), cancellation.Token);
        await using ProxyTestServer other = await ProxyTestServer.StartAsync(context => ProxyTestBackend.DescribeRequestAsync(context, "default"), cancellation.Token);
        await using WebApplicationTestFactory factory = new();
        Compose(factory, proxy => proxy.Options
            .AddRoute(new ReverseProxyRoute { Name = "default", Destinations = [other.Uri] })
            .AddRoute(new ReverseProxyRoute { Name = "shop", Hosts = ["shop.example", "*.shop.example"], Destinations = [shop.Uri] }));
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(System.Net.Http.HttpMethod.Get, "/cart");
        request.Headers.Host = host;

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert
        response.Headers.GetValues("X-Backend").ShouldBe([expected]);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Proxy] - E2E: PreserveHost decides which Host the destination receives")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UseReverseProxy_PreserveHost_ShouldSendTheChosenHost(bool preserveHost)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using ProxyTestServer backend = await ProxyTestServer.StartAsync(context => ProxyTestBackend.DescribeRequestAsync(context, "a"), cancellation.Token);
        await using WebApplicationTestFactory factory = new();
        Compose(factory, proxy => proxy.Options.AddRoute(new ReverseProxyRoute { Name = "api", PreserveHost = preserveHost, Destinations = [backend.Uri] }));
        using HttpClient client = factory.CreateClient();

        // Act
        string body = await client.GetStringAsync("/", cancellation.Token);

        // Assert
        ProxyTestBackend.Parse(body)["host"].ShouldBe(preserveHost ? "localhost" : backend.Authority);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - E2E: A request no route takes goes to the next middleware")]
    public async Task UseReverseProxy_UnmatchedRequest_ShouldCallTheNextMiddleware()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using ProxyTestServer backend = await ProxyTestServer.StartAsync(context => ProxyTestBackend.DescribeRequestAsync(context, "a"), cancellation.Token);
        await using WebApplicationTestFactory factory = new();
        Compose(factory, proxy => proxy.Options.AddRoute(new ReverseProxyRoute { Name = "api", PathPrefix = "/api", Destinations = [backend.Uri] }));
        using HttpClient client = factory.CreateClient();

        // Act
        string local = await client.GetStringAsync("/apix/users", cancellation.Token);
        string forwarded = await client.GetStringAsync("/api/users", cancellation.Token);

        // Assert
        local.ShouldBe("local");
        ProxyTestBackend.Parse(forwarded)["server"].ShouldBe("a");
    }

    [Theory(DisplayName = "Cohesion Test [Web.Proxy] - E2E: A path that hides a dot segment behind an encoded slash is refused only where a route would take it")]
    [InlineData("/api/..%2Fadmin", 400, null)]
    [InlineData("/other/..%2Fadmin", 200, "local")]
    public async Task UseReverseProxy_HiddenDotSegment_ShouldRefuseOrPassOn(string path, int expectedStatus, string? expectedBody)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        int forwarded = 0;
        await using ProxyTestServer backend = await ProxyTestServer.StartAsync(
            context =>
            {
                Interlocked.Increment(ref forwarded);
                return ProxyTestBackend.DescribeRequestAsync(context, "a");
            },
            cancellation.Token);
        await using WebApplicationTestFactory factory = new();
        Compose(factory, proxy => proxy.Options.AddRoute(new ReverseProxyRoute { Name = "api", PathPrefix = "/api", Destinations = [backend.Uri] }));
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync(path, cancellation.Token);
        string body = await response.Content.ReadAsStringAsync(cancellation.Token);

        // Assert
        ((int)response.StatusCode).ShouldBe(expectedStatus);
        if (expectedBody is not null)
        {
            body.ShouldBe(expectedBody);
        }

        forwarded.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - E2E: A destination that cannot be reached is answered with 502")]
    public async Task UseReverseProxy_UnreachableDestination_ShouldAnswerBadGateway()
    {
        // Arrange — a port nothing listens on.
        using CancellationTokenSource cancellation = new(_testTimeout);
        int port;
        await using (TcpEchoServer probe = TcpEchoServer.Start())
        {
            port = probe.Port;
        }

        await using WebApplicationTestFactory factory = new();
        Compose(factory, proxy => proxy.Options.AddRoute(new ReverseProxyRoute { Name = "api", Destinations = [new Uri($"http://127.0.0.1:{port}/")] }));
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.BadGateway);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - E2E: Routes set while the application runs apply from the next request")]
    public async Task UseReverseProxy_SettingChanged_ShouldApplyTheNewRoutes()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using ProxyTestServer a = await ProxyTestServer.StartAsync(context => ProxyTestBackend.DescribeRequestAsync(context, "a"), cancellation.Token);
        await using ProxyTestServer b = await ProxyTestServer.StartAsync(context => ProxyTestBackend.DescribeRequestAsync(context, "b"), cancellation.Token);
        await using WebApplicationTestFactory factory = new();
        Compose(factory, proxy => proxy.Options.AddRoute(new ReverseProxyRoute { Name = "api", PathPrefix = "/api", Destinations = [a.Uri] }));
        IWebApplicationSetting<IReadOnlyList<ReverseProxyRoute>> setting = factory.Application.Context.Settings
            .OfType<IWebApplicationSetting<IReadOnlyList<ReverseProxyRoute>>>()
            .Single();
        using HttpClient client = factory.CreateClient();

        // Act
        string before = await client.GetStringAsync("/api/users", cancellation.Token);
        setting.Set([new ReverseProxyRoute { Name = "v2", PathPrefix = "/v2", Destinations = [b.Uri] }]);
        string moved = await client.GetStringAsync("/v2/users", cancellation.Token);
        string retired = await client.GetStringAsync("/api/users", cancellation.Token);

        // Assert
        ProxyTestBackend.Parse(before)["server"].ShouldBe("a");
        ProxyTestBackend.Parse(moved)["server"].ShouldBe("b");
        retired.ShouldBe("local");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - E2E: A request that comes back through the proxy is answered with 508")]
    public async Task UseReverseProxy_RouteToItself_ShouldAnswerLoopDetected()
    {
        // Arrange — the proxy's only route points at the proxy itself.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using ProxyTestServer proxy = await ProxyTestServer.StartAsync(
            builder => builder.Services.AddReverseProxy(),
            application => application.UseReverseProxy(),
            cancellation.Token);
        proxy.Application.Context.Settings
            .OfType<IWebApplicationSetting<IReadOnlyList<ReverseProxyRoute>>>()
            .Single()
            .Set([new ReverseProxyRoute { Name = "self", Destinations = [proxy.Uri] }]);
        using HttpClient client = new(new SocketsHttpHandler { UseProxy = false });

        // Act
        using HttpResponseMessage response = await client.GetAsync(new Uri(proxy.Uri, "/loop"), cancellation.Token);

        // Assert
        ((int)response.StatusCode).ShouldBe(508);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Proxy] - E2E: A WebSocket passes through the reverse proxy")]
    [InlineData(WebApplicationTestProtocol.Http1)]
    [InlineData(WebApplicationTestProtocol.Http2)]
    public async Task UseReverseProxy_WebSocket_ShouldRelayMessagesBothWays(WebApplicationTestProtocol protocol)
    {
        // Arrange — the destination speaks HTTP/1.1; over HTTP/2 the client opens the socket with an
        // extended CONNECT, which the proxy bridges to an HTTP/1.1 upgrade.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using ProxyTestServer backend = await ProxyTestServer.StartAsync(ProxyTestBackend.EchoWebSocketAsync, cancellation.Token);
        await using WebApplicationTestFactory factory = new(new WebApplicationTestFactoryOptions { Protocol = protocol });
        Compose(factory, proxy => proxy.Options.AddRoute(new ReverseProxyRoute { Name = "ws", PathPrefix = "/ws", Destinations = [backend.Uri] }));
        using HttpClient client = factory.CreateClient();
        using ClientWebSocket socket = new();
        socket.Options.KeepAliveInterval = TimeSpan.Zero;
        if (protocol == WebApplicationTestProtocol.Http2)
        {
            socket.Options.HttpVersion = NetHttpVersion.Version20;
            socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }

        // Act
        await socket.ConnectAsync(new Uri("ws://localhost/ws/echo"), client, cancellation.Token);
        string first = await RoundTripAsync(socket, "first", cancellation.Token);
        string second = await RoundTripAsync(socket, "second", cancellation.Token);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellation.Token);

        // Assert
        first.ShouldBe("first");
        second.ShouldBe("second");
        socket.CloseStatus.ShouldBe(WebSocketCloseStatus.NormalClosure);
    }

    private static void Compose(WebApplicationTestFactory factory, Action<ReverseProxyBuilder> configure)
    {
        factory.Builder.Services.AddReverseProxy(configure);
        factory.Application.UseReverseProxy();
        factory.Application.Use(async (context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("local"), context.RequestCancelled);
        });
    }

    private static async Task<string> RoundTripAsync(ClientWebSocket socket, string message, CancellationToken cancellationToken)
    {
        await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, cancellationToken);
        byte[] buffer = new byte[256];
        WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }
}
