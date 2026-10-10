using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net.Security;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Covers <see cref="HttpConnectionListenerOptions.UseHttp1AndHttp2(IConnectionListener)"/>: one TLS
/// listener serving HTTP/1.1 and HTTP/2, each connection dispatched by the application protocol its
/// handshake negotiated through ALPN (RFC 7301, RFC 9113 §3.2). The connections are doubles that report
/// a negotiated protocol through <see cref="ITlsConnectionInfo"/>; the real TLS handshake is covered by
/// the Connections.Security and Web.Hosting suites.
/// </summary>
public class HttpAlpnDispatchTests
{
    // TestMultiplexedConnectionListener binds its endpoint to loopback:16000.
    private const int Http3Port = 16000;

    private const string Http1Request = "GET /alpn HTTP/1.1\r\nHost: api.test\r\nConnection: close\r\n\r\n";

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - UseHttp1AndHttp2: Should serve HTTP/2 to a connection that negotiated h2")]
    public async Task UseHttp1AndHttp2_WhenH2Negotiated_ShouldServeHttp2()
    {
        // Arrange
        TestTlsConnection connection = new(
            HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/alpn", "https", "api.test"),
            SslApplicationProtocol.Http2);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1AndHttp2(new TestConnectionListener(TestTlsConnection.TlsCapabilities, connection));
        await using HttpConnectionListener listener = new(options);

        // Act
        IHttpContext context = await ReceiveFirstExchangeAsync(listener);

        // Assert
        context.Version.ShouldBe(HttpVersion.Http20);
        context.Request.Scheme.ShouldBe(HttpScheme.Https);
        context.Request.Path.ToString().ShouldBe("/alpn");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - UseHttp1AndHttp2: Should serve HTTP/1.1 to a connection that negotiated http/1.1")]
    public async Task UseHttp1AndHttp2_WhenHttp11Negotiated_ShouldServeHttp11()
    {
        // Arrange
        TestTlsConnection connection = new(
            HttpProtocolPayloadFactory.CreateHttp1Request(Http1Request),
            SslApplicationProtocol.Http11);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1AndHttp2(new TestConnectionListener(TestTlsConnection.TlsCapabilities, connection));
        await using HttpConnectionListener listener = new(options);

        // Act
        IHttpContext context = await ReceiveFirstExchangeAsync(listener);

        // Assert
        context.Version.ShouldBe(HttpVersion.Http11);
        context.Request.Scheme.ShouldBe(HttpScheme.Https);
        context.Request.Path.ToString().ShouldBe("/alpn");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - UseHttp1AndHttp2: Should serve HTTP/1.1 to a connection that negotiated no protocol")]
    public async Task UseHttp1AndHttp2_WhenNoProtocolNegotiated_ShouldServeHttp11()
    {
        // Arrange — a client that sent no ALPN extension (RFC 7301 §3.1) completes the handshake with
        // no protocol selected; an https origin serves it HTTP/1.1.
        TestTlsConnection connection = new(
            HttpProtocolPayloadFactory.CreateHttp1Request(Http1Request),
            default);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1AndHttp2(new TestConnectionListener(TestTlsConnection.TlsCapabilities, connection));
        await using HttpConnectionListener listener = new(options);

        // Act
        IHttpContext context = await ReceiveFirstExchangeAsync(listener);

        // Assert
        context.Version.ShouldBe(HttpVersion.Http11);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - UseHttp1AndHttp2: Should serve HTTP/1.1 to a connection that does not report its handshake")]
    public async Task UseHttp1AndHttp2_WhenConnectionDoesNotReportHandshake_ShouldServeHttp11()
    {
        // Arrange — a TLS layer that does not implement ITlsConnectionInfo reports no protocol.
        TestConnection connection = new(
            HttpProtocolPayloadFactory.CreateHttp1Request(Http1Request),
            capabilities: TestTlsConnection.TlsCapabilities);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1AndHttp2(new TestConnectionListener(TestTlsConnection.TlsCapabilities, connection));
        await using HttpConnectionListener listener = new(options);

        // Act
        IHttpContext context = await ReceiveFirstExchangeAsync(listener);

        // Assert
        context.Version.ShouldBe(HttpVersion.Http11);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - UseHttp1AndHttp2: Should close a connection that negotiated another protocol and keep accepting")]
    public async Task UseHttp1AndHttp2_WhenUnservedProtocolNegotiated_ShouldCloseConnectionAndKeepAccepting()
    {
        // Arrange — the handshake bound the first connection to a protocol neither HTTP version speaks
        // (RFC 7301 §3.2), as an application-supplied ALPN list can; the second one is ordinary.
        TestTlsConnection unserved = new(
            HttpProtocolPayloadFactory.CreateHttp1Request(Http1Request),
            new SslApplicationProtocol("acme-tls/1"));
        TestTlsConnection served = new(
            HttpProtocolPayloadFactory.CreateHttp1Request(Http1Request),
            SslApplicationProtocol.Http11);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1AndHttp2(new TestConnectionListener(TestTlsConnection.TlsCapabilities, unserved, served));
        await using HttpConnectionListener listener = new(options);

        // Act
        IHttpContext context = await ReceiveFirstExchangeAsync(listener);

        // Assert — the unserved connection was closed without a byte written; the next one was served.
        unserved.Inner.IsDisposed.ShouldBeTrue();
        served.Inner.IsDisposed.ShouldBeFalse();
        context.Version.ShouldBe(HttpVersion.Http11);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - UseHttp1AndHttp2: Should report both stream protocols")]
    public async Task UseHttp1AndHttp2_OnRegistration_ShouldReportHttp11AndHttp20()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();
        options.UseHttp1AndHttp2(new TestConnectionListener(TestTlsConnection.TlsCapabilities));

        // Act
        await using HttpConnectionListener listener = new(options);

        // Assert
        listener.Protocols.ShouldBe(HttpProtocol.Http11 | HttpProtocol.Http20);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - UseHttp1AndHttp2: Should reject a listener without TLS at registration")]
    public void UseHttp1AndHttp2_OnListenerWithoutTls_ShouldThrowArgumentException()
    {
        // Arrange — ALPN is a TLS extension; a cleartext listener could only ever serve HTTP/1.1.
        HttpConnectionListenerOptions options = new();
        TestConnectionListener cleartext = new(TestConnection.DefaultCapabilities);

        // Act / Assert
        ArgumentException exception = Should.Throw<ArgumentException>(() => options.UseHttp1AndHttp2(cleartext));
        exception.ParamName.ShouldBe("listener");
        exception.Message.ShouldContain("Security=Tls", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - UseHttp1AndHttp2: Should reject a factory-produced listener without TLS when the listener is constructed")]
    public void UseHttp1AndHttp2_OnFactoryListenerWithoutTls_ShouldThrowAtConstruction()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();
        options.UseHttp1AndHttp2(() => new TestConnectionListener(TestConnection.DefaultCapabilities));

        // Act / Assert
        Should.Throw<ArgumentException>(() => new HttpConnectionListener(options));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - UseHttp1AndHttp2: Should reject null configure callbacks")]
    public void UseHttp1AndHttp2_WithNullConfigure_ShouldThrowArgumentNullException()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();
        TestConnectionListener listener = new(TestTlsConnection.TlsCapabilities);

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => options.UseHttp1AndHttp2(listener, null!, static _ => { }));
        Should.Throw<ArgumentNullException>(() => options.UseHttp1AndHttp2(listener, static _ => { }, null!));
        Should.Throw<ArgumentNullException>(() => options.UseHttp1AndHttp2((IConnectionListener)null!));
        Should.Throw<ArgumentNullException>(() => options.UseHttp1AndHttp2((Func<IConnectionListener>)null!));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - UseHttp1AndHttp2: Should apply the HTTP/1.1 options to the connections served HTTP/1.1")]
    public async Task UseHttp1AndHttp2_WithHttp1Options_ShouldEnforceHttp1LimitsOnHttp11Connections()
    {
        // Arrange — RFC 9110 §15.5.15: an over-long request line is 414 under the registration's limit.
        TestTlsConnection connection = new(
            HttpProtocolPayloadFactory.CreateHttp1Request(
                "GET /an-intentionally-very-long-request-target-that-blows-the-cap HTTP/1.1\r\nHost: api.test\r\n\r\n"),
            SslApplicationProtocol.Http11);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1AndHttp2(
            new TestConnectionListener(TestTlsConnection.TlsCapabilities, connection),
            http1 => http1.Limits.MaxRequestLineSize = 24,
            static _ => { });
        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        // Act
        bool yielded = false;
        await foreach (IHttpContext _ in connectionContext.ReceiveAsync())
        {
            yielded = true;
        }

        string response = Encoding.ASCII.GetString(await connection.Inner.ReadOutputAsync());

        // Assert
        yielded.ShouldBeFalse();
        response.ShouldContain("414", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - UseHttp1AndHttp2: Should apply the HTTP/2 options to the connections served HTTP/2")]
    public async Task UseHttp1AndHttp2_WithHttp2Options_ShouldAdvertiseHttp2LimitsOnHttp2Connections()
    {
        // Arrange — RFC 9113 §6.5.2: the server advertises its stream cap as SETTINGS_MAX_CONCURRENT_STREAMS.
        TestTlsConnection connection = new(
            HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/alpn", "https", "api.test"),
            SslApplicationProtocol.Http2);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1AndHttp2(
            new TestConnectionListener(TestTlsConnection.TlsCapabilities, connection),
            static _ => { },
            http2 => http2.Limits.MaxStreamsPerConnection = 7);
        await using HttpConnectionListener listener = new(options);

        // Act
        IHttpContext context = await ReceiveFirstExchangeAsync(listener);
        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp2Frames(await connection.Inner.ReadOutputAsync());

        // Assert
        context.Version.ShouldBe(HttpVersion.Http20);
        ReadSetting(frames, settingId: 0x3).ShouldBe(7u);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - UseHttp1AndHttp2: Should advertise the h3 endpoint on an HTTP/1.1 response")]
    public async Task UseHttp1AndHttp2_OnHttp11WithHttp3Bound_ShouldEmitAltSvcHeader()
    {
        // Arrange
        TestTlsConnection connection = new(
            HttpProtocolPayloadFactory.CreateHttp1Request(Http1Request),
            SslApplicationProtocol.Http11);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1AndHttp2(new TestConnectionListener(TestTlsConnection.TlsCapabilities, connection));
        options.UseHttp3(new TestMultiplexedConnectionListener());
        options.AdvertiseAltService(advertisement => advertisement.MaxAge = TimeSpan.FromHours(24));
        await using HttpConnectionListener listener = new(options);
        (IHttpConnectionContext connectionContext, IHttpContext context) = await ReceiveFirstExchangeWithContextAsync(listener);

        // Act
        context.Response.StatusCode = HttpStatusCode.Ok;
        await connectionContext.SendAsync(context);
        string response = Encoding.ASCII.GetString(await connection.Inner.ReadOutputAsync());

        // Assert
        response.ShouldContain($"Alt-Svc: h3=\":{Http3Port}\"; ma=86400", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - UseHttp1AndHttp2: Should advertise the h3 endpoint on an HTTP/2 response")]
    public async Task UseHttp1AndHttp2_OnHttp2WithHttp3Bound_ShouldEmitAltSvcHeader()
    {
        // Arrange
        TestTlsConnection connection = new(
            HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/alpn", "https", "api.test"),
            SslApplicationProtocol.Http2);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1AndHttp2(new TestConnectionListener(TestTlsConnection.TlsCapabilities, connection));
        options.UseHttp3(new TestMultiplexedConnectionListener());
        options.AdvertiseAltService(advertisement => advertisement.MaxAge = TimeSpan.FromHours(24));
        await using HttpConnectionListener listener = new(options);
        (IHttpConnectionContext connectionContext, IHttpContext context) = await ReceiveFirstExchangeWithContextAsync(listener);

        // Act
        context.Response.StatusCode = HttpStatusCode.Ok;
        await connectionContext.SendAsync(context);
        Dictionary<string, string>? headers = null;
        foreach ((long frameType, byte[] payload) in HttpProtocolPayloadFactory.ParseHttp2Frames(await connection.Inner.ReadOutputAsync()))
        {
            if (frameType == 1) // HEADERS
            {
                headers = HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(payload);
                break;
            }
        }

        // Assert
        headers.ShouldNotBeNull();
        headers.ShouldContainKey("alt-svc");
        headers["alt-svc"].ShouldBe($"h3=\":{Http3Port}\"; ma=86400");
    }

    private static async Task<IHttpContext> ReceiveFirstExchangeAsync(HttpConnectionListener listener)
    {
        (IHttpConnectionContext _, IHttpContext context) = await ReceiveFirstExchangeWithContextAsync(listener);
        return context;
    }

    private static async Task<(IHttpConnectionContext ConnectionContext, IHttpContext Context)> ReceiveFirstExchangeWithContextAsync(HttpConnectionListener listener)
    {
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return (connectionContext, enumerator.Current);
    }

    private static uint? ReadSetting(IReadOnlyList<(long FrameType, byte[] Payload)> frames, ushort settingId)
    {
        foreach ((long frameType, byte[] payload) in frames)
        {
            if (frameType != 0x4)
            {
                continue;
            }

            for (int offset = 0; offset + 6 <= payload.Length; offset += 6)
            {
                if (BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(offset, 2)) == settingId)
                {
                    return BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(offset + 2, 4));
                }
            }
        }

        return null;
    }
}
