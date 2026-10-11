using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// The request scheme against the connection's security (RFC 9110 §4.2.2, §7.4). An <c>https</c> target
/// URI names a resource reachable only over a secured connection, so a request for one never reaches the
/// application marked <c>https</c> over cleartext: HTTP/1.1 answers an <c>https</c> absolute-form target
/// with <c>421 Misdirected Request</c>, as does HTTP/2 unless a trusted gateway opt-out is enabled. Over a secured connection the target's scheme is
/// the request's, as RFC 9112 §3.3 builds the target URI.
/// </summary>
public class HttpRequestSchemeTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Scheme: Http1 should answer an https absolute-form target on a cleartext connection with 421 and close")]
    public async Task Http1_HttpsAbsoluteFormTargetOverCleartext_ShouldAnswerMisdirectedRequestAndClose()
    {
        // Arrange — a second request is pipelined behind it, which must never be served.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET https://example.com/admin HTTP/1.1\r\nHost: example.com\r\n\r\n"
            + "GET /next HTTP/1.1\r\nHost: example.com\r\n\r\n");

        // Act
        Served result = await ServeHttp1Async(payload, ConnectionSecurity.None);

        // Assert — no request reaches the application.
        result.Schemes.ShouldBeEmpty();
        result.Output.ShouldStartWith("HTTP/1.1 421");
        result.Output.ShouldContain("Connection: close");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Scheme: Http1 should take an absolute-form target's scheme on a secured connection")]
    [InlineData("https://example.com/admin", HttpScheme.Https)]
    [InlineData("http://example.com/admin", HttpScheme.Http)]
    public async Task Http1_AbsoluteFormTargetOverTls_ShouldTakeTheTargetScheme(string target, HttpScheme expectedScheme)
    {
        // Arrange — RFC 9112 §3.3: the target URI of an absolute-form request is the request-target. An
        // http target over TLS claims less than the connection gives, so it is kept.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            $"GET {target} HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n");

        // Act
        Served result = await ServeHttp1Async(payload, ConnectionSecurity.Tls);

        // Assert
        result.Schemes.ShouldBe([expectedScheme]);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Scheme: Http1 should take the connection's scheme for an http absolute-form target on cleartext")]
    public async Task Http1_HttpAbsoluteFormTargetOverCleartext_ShouldTakeHttp()
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp1Request(
            "GET http://example.com/admin HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n");

        // Act
        Served result = await ServeHttp1Async(payload, ConnectionSecurity.None);

        // Assert
        result.Schemes.ShouldBe([HttpScheme.Http]);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Scheme: h2c https should answer 421 before hooks and keep serving other streams")]
    [InlineData(true, "https")]
    [InlineData(false, "https")]
    [InlineData(true, "HTTPS")]
    public async Task Http2_HttpsOverCleartext_ShouldRejectBeforeDispatch(bool endStream, string scheme)
    {
        // Arrange
        SchemeProbe probe = new();
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(probe);
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(options);

        // Act
        await peer.SendHeadersAsync(1, endStream, (":method", "GET"), (":scheme", scheme), (":path", "/rejected"), (":authority", "example.com"));
        await peer.SendHeadersAsync(3, true, Http2TestPeer.Get("/next"));
        IHttpContext next = await peer.ReceiveContextAsync();
        await peer.SyncAsync();

        // Assert
        next.Request.Path.Value.ShouldBe("/next");
        next.Request.Scheme.ShouldBe(HttpScheme.Http);
        probe.Paths.ShouldBe(["/next"]);
        Http2WireFrame head = peer.Output.ForStream(1).Single(frame => frame.IsHeaders);
        head.EndStream.ShouldBeTrue();
        Dictionary<string, string> headers = HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(head.Payload);
        headers[":status"].ShouldBe("421");
        headers["content-length"].ShouldBe("0");
        peer.Output.ForStream(1).ShouldNotContain(frame => frame.IsData);
        if (endStream)
        {
            peer.Output.ForStream(1).ShouldNotContain(frame => frame.IsRstStream);
        }
        else
        {
            peer.Output.ForStream(1).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.NoError);
        }
        peer.Output.Frames.ShouldNotContain(frame => frame.IsGoAway);
        await peer.ConnectionContext.SendAsync(next);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Scheme: trusted h2c gateway should retain the forwarded scheme")]
    [InlineData("https", HttpScheme.Https)]
    [InlineData("http", HttpScheme.Http)]
    public async Task Http2_AlternateSchemesAllowed_ShouldRetainGatewayScheme(string scheme, HttpScheme expectedScheme)
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/admin", scheme, "example.com");

        // Act
        IHttpContext context = await ReadHttp2ContextAsync(payload, ConnectionSecurity.None, allowAlternateSchemes: true);

        // Assert
        context.Request.Scheme.ShouldBe(expectedScheme);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Scheme: Http2 over TLS should take :scheme")]
    [InlineData("https", HttpScheme.Https)]
    [InlineData("http", HttpScheme.Http)]
    public async Task Http2_SchemeOverTls_ShouldTakeTheScheme(string scheme, HttpScheme expectedScheme)
    {
        // Arrange
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/admin", scheme, "example.com");

        // Act
        IHttpContext context = await ReadHttp2ContextAsync(payload, ConnectionSecurity.Tls);

        // Assert
        context.Request.Scheme.ShouldBe(expectedScheme);
    }

    private static async Task<Served> ServeHttp1Async(byte[] payload, ConnectionSecurity security)
    {
        TestConnection transport = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(TestConnection.DefaultCapabilities with { Security = security }, transport));
        Served served = new();

        await using (HttpConnectionListener listener = new(options))
        {
            await using IHttpConnection connection = await listener.AcceptOrListenAsync();
            IHttpConnectionContext connectionContext = await connection.OpenAsync();

            await foreach (IHttpContext exchange in connectionContext.ReceiveAsync())
            {
                served.Schemes.Add(exchange.Request.Scheme);
                await connectionContext.SendAsync(exchange);
            }
        }

        // The connection is disposed, so its output is complete: everything the server wrote is here.
        served.Output = Encoding.ASCII.GetString(await transport.ReadOutputAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        return served;
    }

    private static async Task<IHttpContext> ReadHttp2ContextAsync(byte[] payload, ConnectionSecurity security, bool allowAlternateSchemes = false)
    {
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new() { AllowAlternateSchemes = allowAlternateSchemes };
        options.UseHttp2(new TestConnectionListener(TestConnection.DefaultCapabilities with { Security = security }, connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext context = await (await listener.AcceptOrListenAsync()).OpenAsync();

        await using IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return enumerator.Current;
    }

    private sealed class SchemeProbe : HttpExchangeInterceptor
    {
        public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Request;
        public List<string> Paths { get; } = new();
        public override void AfterRequestHead(HttpExchangeInterceptorRequestContext context) => Paths.Add(context.Path.Value);
    }

    private sealed class Served
    {
        public List<HttpScheme> Schemes { get; } = new();

        public string Output { get; set; } = string.Empty;
    }
}
