using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Web.Proxy.Tests.TestObjects;

using Shouldly;

using Xunit;

using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Proxy.Tests;

/// <summary>
/// The forward proxy end to end over loopback sockets: clients send it absolute-form requests and
/// <c>CONNECT</c>s, and it reaches real destinations the rules allow.
/// </summary>
public class ForwardProxyEndToEndTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - E2E: An absolute-form request to an allowed target is forwarded without forwarding headers")]
    public async Task UseForwardProxy_AllowedTarget_ShouldForward()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using ProxyTestServer backend = await ProxyTestServer.StartAsync(context => ProxyTestBackend.DescribeRequestAsync(context, "a"), cancellation.Token);
        await using ProxyTestServer proxy = await StartProxyAsync(new ForwardProxyRule { Hosts = ["127.0.0.1"], Ports = [backend.Port] }, cancellation.Token);
        using HttpClient client = CreateClient(proxy);

        // Act
        using HttpResponseMessage response = await client.GetAsync(new Uri(backend.Uri, "/hello?x=1&y=2"), cancellation.Token);
        IReadOnlyDictionary<string, string> received = ProxyTestBackend.Parse(await response.Content.ReadAsStringAsync(cancellation.Token));

        // Assert — a forward proxy does not disclose its client to the destination, and says it relayed.
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        received["path"].ShouldBe("/hello");
        received["query"].ShouldBe("x=1&y=2");
        received["host"].ShouldBe(backend.Authority);
        received["x-forwarded-for"].ShouldBeEmpty();
        received["forwarded"].ShouldBeEmpty();
        received["proxy-connection"].ShouldBeEmpty();
        received["via"].ShouldStartWith("1.1 ");
        response.Headers.Via.ShouldNotBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - E2E: A request body is forwarded to an allowed target")]
    public async Task UseForwardProxy_RequestBody_ShouldForwardTheBody()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using ProxyTestServer backend = await ProxyTestServer.StartAsync(context => ProxyTestBackend.DescribeRequestAsync(context, "a"), cancellation.Token);
        await using ProxyTestServer proxy = await StartProxyAsync(new ForwardProxyRule { Hosts = ["127.0.0.1"], Ports = [backend.Port] }, cancellation.Token);
        using HttpClient client = CreateClient(proxy);

        // Act
        using HttpResponseMessage response = await client.PostAsync(new Uri(backend.Uri, "/items"), new StringContent("payload"), cancellation.Token);
        IReadOnlyDictionary<string, string> received = ProxyTestBackend.Parse(await response.Content.ReadAsStringAsync(cancellation.Token));

        // Assert
        received["method"].ShouldBe("POST");
        received["body"].ShouldBe("payload");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - E2E: A target no rule allows is answered with 403 and never contacted")]
    public async Task UseForwardProxy_HostNotAllowed_ShouldAnswerForbidden()
    {
        // Arrange — the rule names the loopback literal; the request names the host by another spelling.
        using CancellationTokenSource cancellation = new(_testTimeout);
        int contacted = 0;
        await using ProxyTestServer backend = await ProxyTestServer.StartAsync(
            context =>
            {
                Interlocked.Increment(ref contacted);
                return ProxyTestBackend.DescribeRequestAsync(context, "a");
            },
            cancellation.Token);
        await using ProxyTestServer proxy = await StartProxyAsync(new ForwardProxyRule { Hosts = ["127.0.0.1"], Ports = [backend.Port] }, cancellation.Token);
        using HttpClient client = CreateClient(proxy);

        // Act
        using HttpResponseMessage response = await client.GetAsync($"http://localhost:{backend.Port}/", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
        contacted.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - E2E: A port no rule allows is answered with 403")]
    public async Task UseForwardProxy_PortNotAllowed_ShouldAnswerForbidden()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using ProxyTestServer allowed = await ProxyTestServer.StartAsync(context => ProxyTestBackend.DescribeRequestAsync(context, "allowed"), cancellation.Token);
        await using ProxyTestServer other = await ProxyTestServer.StartAsync(context => ProxyTestBackend.DescribeRequestAsync(context, "other"), cancellation.Token);
        await using ProxyTestServer proxy = await StartProxyAsync(new ForwardProxyRule { Hosts = ["127.0.0.1"], Ports = [allowed.Port] }, cancellation.Token);
        using HttpClient client = CreateClient(proxy);

        // Act
        using HttpResponseMessage permitted = await client.GetAsync(allowed.Uri, cancellation.Token);
        using HttpResponseMessage refused = await client.GetAsync(other.Uri, cancellation.Token);

        // Assert
        permitted.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        refused.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - E2E: A loopback literal allowed only by a wildcard is answered with 403")]
    public async Task UseForwardProxy_WildcardToLoopbackLiteral_ShouldAnswerForbidden()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        int contacted = 0;
        await using ProxyTestServer backend = await ProxyTestServer.StartAsync(
            context =>
            {
                Interlocked.Increment(ref contacted);
                return ProxyTestBackend.DescribeRequestAsync(context, "a");
            },
            cancellation.Token);
        await using ProxyTestServer proxy = await StartProxyAsync(new ForwardProxyRule { Hosts = ["*"], Ports = [backend.Port] }, cancellation.Token);
        using HttpClient client = CreateClient(proxy);

        // Act
        using HttpResponseMessage response = await client.GetAsync(backend.Uri, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
        contacted.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - E2E: A name allowed only by a wildcard that resolves to loopback is answered with 502")]
    public async Task UseForwardProxy_WildcardToLoopbackName_ShouldAnswerBadGateway()
    {
        // Arrange — the proxy resolves the name and connects to none of its addresses.
        using CancellationTokenSource cancellation = new(_testTimeout);
        int contacted = 0;
        await using ProxyTestServer backend = await ProxyTestServer.StartAsync(
            context =>
            {
                Interlocked.Increment(ref contacted);
                return ProxyTestBackend.DescribeRequestAsync(context, "a");
            },
            cancellation.Token);
        await using ProxyTestServer proxy = await StartProxyAsync(new ForwardProxyRule { Hosts = ["*"], Ports = [backend.Port] }, cancellation.Token);
        using HttpClient client = CreateClient(proxy);

        // Act
        using HttpResponseMessage response = await client.GetAsync($"http://localhost:{backend.Port}/", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.BadGateway);
        contacted.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - E2E: Rules set while the application runs apply from the next request")]
    public async Task UseForwardProxy_SettingChanged_ShouldApplyTheNewRules()
    {
        // Arrange — the proxy starts with no rule, so nothing is allowed.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using ProxyTestServer backend = await ProxyTestServer.StartAsync(context => ProxyTestBackend.DescribeRequestAsync(context, "a"), cancellation.Token);
        await using ProxyTestServer proxy = await ProxyTestServer.StartAsync(
            builder => builder.Services.AddForwardProxy(),
            application => application.UseForwardProxy(),
            cancellation.Token);
        IWebApplicationSetting<IReadOnlyList<ForwardProxyRule>> setting = proxy.Application.Context.Settings
            .OfType<IWebApplicationSetting<IReadOnlyList<ForwardProxyRule>>>()
            .Single();
        using HttpClient client = CreateClient(proxy);

        // Act
        using HttpResponseMessage before = await client.GetAsync(backend.Uri, cancellation.Token);
        setting.Set([new ForwardProxyRule { Hosts = ["127.0.0.1"], Ports = [backend.Port] }]);
        using HttpResponseMessage after = await client.GetAsync(backend.Uri, cancellation.Token);

        // Assert
        before.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
        after.StatusCode.ShouldBe(NetHttpStatusCode.OK);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - E2E: A CONNECT to an allowed target opens a tunnel that relays bytes both ways")]
    public async Task UseForwardProxy_ConnectAllowed_ShouldTunnel()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using TcpEchoServer echo = TcpEchoServer.Start();
        await using ProxyTestServer proxy = await StartProxyAsync(new ForwardProxyRule { Hosts = ["127.0.0.1"], Ports = [echo.Port] }, cancellation.Token);
        await using RawProxyClient client = await RawProxyClient.ConnectAsync(proxy.Port, cancellation.Token);

        // Act
        (int status, _) = await client.SendAsync($"CONNECT 127.0.0.1:{echo.Port} HTTP/1.1\r\nHost: 127.0.0.1:{echo.Port}\r\n\r\n", cancellation.Token);
        string first = await client.RoundTripAsync("not even http", cancellation.Token);
        string second = await client.RoundTripAsync("\u0016\u0003\u0001 tls-looking bytes", cancellation.Token);

        // Assert
        status.ShouldBe(200);
        first.ShouldBe("not even http");
        second.ShouldBe("\u0016\u0003\u0001 tls-looking bytes");
        echo.Accepted.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Proxy] - E2E: A CONNECT the rules or the address policy refuse is answered with 403")]
    [InlineData("exact-other-port")]
    [InlineData("default-port-only")]
    [InlineData("wildcard-loopback-literal")]
    public async Task UseForwardProxy_ConnectRefused_ShouldAnswerForbidden(string scenario)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using TcpEchoServer echo = TcpEchoServer.Start();
        ForwardProxyRule rule = scenario switch
        {
            "exact-other-port" => new ForwardProxyRule { Hosts = ["127.0.0.1"], Ports = [echo.Port == 1 ? 2 : echo.Port - 1] },
            // A rule with no ports allows a CONNECT to 443 only.
            "default-port-only" => new ForwardProxyRule { Hosts = ["127.0.0.1"] },
            _ => new ForwardProxyRule { Hosts = ["*"], Ports = [echo.Port] },
        };
        await using ProxyTestServer proxy = await StartProxyAsync(rule, cancellation.Token);
        await using RawProxyClient client = await RawProxyClient.ConnectAsync(proxy.Port, cancellation.Token);

        // Act
        (int status, _) = await client.SendAsync($"CONNECT 127.0.0.1:{echo.Port} HTTP/1.1\r\nHost: 127.0.0.1:{echo.Port}\r\n\r\n", cancellation.Token);

        // Assert
        status.ShouldBe(403);
        echo.Accepted.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - E2E: A CONNECT to a name allowed only by a wildcard that resolves to loopback is answered with 502")]
    public async Task UseForwardProxy_ConnectWildcardToLoopbackName_ShouldAnswerBadGateway()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using TcpEchoServer echo = TcpEchoServer.Start();
        await using ProxyTestServer proxy = await StartProxyAsync(new ForwardProxyRule { Hosts = ["*"], Ports = [echo.Port] }, cancellation.Token);
        await using RawProxyClient client = await RawProxyClient.ConnectAsync(proxy.Port, cancellation.Token);

        // Act
        (int status, _) = await client.SendAsync($"CONNECT localhost:{echo.Port} HTTP/1.1\r\nHost: localhost:{echo.Port}\r\n\r\n", cancellation.Token);

        // Assert
        status.ShouldBe(502);
        echo.Accepted.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - E2E: A CONNECT without a port never opens a tunnel")]
    public async Task UseForwardProxy_ConnectWithoutPort_ShouldNotOpenATunnel()
    {
        // Arrange — RFC 9112 §3.2.3: an authority-form target is a host and a port, so the HTTP/1.1 transport
        // refuses this one before the proxy runs. It closes the connection without a response today, where
        // RFC 9112 §3 says it SHOULD answer 400 (#1926); ForwardProxyMiddlewareTests covers the proxy's own 400.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using ProxyTestServer proxy = await StartProxyAsync(new ForwardProxyRule { Hosts = ["*"] }, cancellation.Token);
        await using RawProxyClient client = await RawProxyClient.ConnectAsync(proxy.Port, cancellation.Token);

        // Act
        (int status, _) = await client.SendAsync("CONNECT example.com HTTP/1.1\r\nHost: example.com\r\n\r\n", cancellation.Token);

        // Assert — 0 is a connection closed without a response head.
        status.ShouldBeOneOf(0, 400);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - E2E: A WebSocket passes through the forward proxy's tunnel")]
    public async Task UseForwardProxy_WebSocket_ShouldTunnelTheSocket()
    {
        // Arrange — a client sends a ws:// request through an HTTP proxy as a CONNECT, then its handshake
        // inside the tunnel.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using ProxyTestServer backend = await ProxyTestServer.StartAsync(ProxyTestBackend.EchoWebSocketAsync, cancellation.Token);
        await using ProxyTestServer proxy = await StartProxyAsync(new ForwardProxyRule { Hosts = ["127.0.0.1"], Ports = [backend.Port] }, cancellation.Token);
        using ClientWebSocket socket = new();
        socket.Options.KeepAliveInterval = TimeSpan.Zero;
        socket.Options.Proxy = new WebProxy(proxy.Uri);

        // Act
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{backend.Port}/echo"), cancellation.Token);
        await socket.SendAsync(Encoding.UTF8.GetBytes("through the tunnel"), WebSocketMessageType.Text, true, cancellation.Token);
        byte[] buffer = new byte[256];
        WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation.Token);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellation.Token);

        // Assert
        Encoding.UTF8.GetString(buffer, 0, result.Count).ShouldBe("through the tunnel");
        socket.CloseStatus.ShouldBe(WebSocketCloseStatus.NormalClosure);
    }

    private static Task<ProxyTestServer> StartProxyAsync(ForwardProxyRule rule, CancellationToken cancellationToken)
    {
        return ProxyTestServer.StartAsync(
            builder => builder.Services.AddForwardProxy(proxy => proxy.Options.AddRule(rule)),
            application => application.UseForwardProxy(),
            cancellationToken);
    }

    private static HttpClient CreateClient(ProxyTestServer proxy)
    {
        return new HttpClient(new SocketsHttpHandler
        {
            Proxy = new WebProxy(proxy.Uri),
            UseProxy = true,
        });
    }
}
