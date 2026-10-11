using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// The request scheme against the connection's security (RFC 9110 §4.2.2, §7.4). An <c>https</c> target
/// URI names a resource reachable only over a secured connection, so a request for one never reaches the
/// application marked <c>https</c> over cleartext: HTTP/1.1 answers an <c>https</c> absolute-form target
/// with <c>421 Misdirected Request</c>, and HTTP/2 over cleartext (h2c) takes the connection's
/// <c>http</c> instead of an <c>https</c> <c>:scheme</c>. Over a secured connection the target's scheme is
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

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Scheme: Http2 over cleartext (h2c) should take http whatever :scheme says")]
    [InlineData("https")]
    [InlineData("http")]
    public async Task Http2_SchemeOverCleartext_ShouldTakeHttp(string scheme)
    {
        // Arrange — an https :scheme on h2c must not mark the request secure: UseHttpsRedirection, HSTS
        // and Secure-cookie decisions read Request.Scheme when no trusted proxy forwarded one.
        byte[] payload = HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/admin", scheme, "example.com");

        // Act
        IHttpContext context = await ReadHttp2ContextAsync(payload, ConnectionSecurity.None);

        // Assert
        context.Request.Scheme.ShouldBe(HttpScheme.Http);
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

    private static async Task<IHttpContext> ReadHttp2ContextAsync(byte[] payload, ConnectionSecurity security)
    {
        TestConnection connection = new(payload);
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(TestConnection.DefaultCapabilities with { Security = security }, connection));

        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext context = await (await listener.AcceptOrListenAsync()).OpenAsync();

        await using IAsyncEnumerator<IHttpContext> enumerator = context.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return enumerator.Current;
    }

    private sealed class Served
    {
        public List<HttpScheme> Schemes { get; } = new();

        public string Output { get; set; } = string.Empty;
    }
}
