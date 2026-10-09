using System;
using System.Collections.Generic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Covers how the transport publishes a connection's TLS handshake: every exchange that arrived over a
/// TLS-terminating connection has an <see cref="IHttpContext.ConnectionInfo"/> that also implements
/// <see cref="ITlsConnectionInfo"/> (client certificate, TLS protocol, cipher suite, negotiated
/// application protocol), on HTTP/1.1, HTTP/2, and HTTP/3, from the first request-parse hook onward;
/// a cleartext exchange's connection info carries no facet; and the transport installs no HTTP TLS
/// feature. The connections are doubles that report a handshake through <see cref="ITlsConnectionInfo"/>;
/// the real handshakes are covered by the Connections.Security, Connections.Quic, and Web.Hosting
/// suites, and the <c>context.TlsConnection</c> accessor over the facet by the Http.Tls suite.
/// </summary>
public class HttpTlsConnectionInfoTests
{
    // The name the Http.Tls accessor's feature registers under; the transport must never install it.
    private const string tlsConnectionFeatureName = "Assimalign.Cohesion.Http.TlsConnection";

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - ConnectionInfo/Http1: Should carry the connection's TLS handshake")]
    public async Task ConnectionInfo_OnHttp11OverTls_ShouldCarryTheConnectionHandshake()
    {
        // Arrange
        using X509Certificate2 clientCertificate = CreateCertificate();
        TestTlsConnection connection = new(
            HttpProtocolPayloadFactory.CreateHttp1Request("GET /tls HTTP/1.1\r\nHost: api.test\r\nConnection: close\r\n\r\n"),
            SslApplicationProtocol.Http11,
            clientCertificate,
            SslProtocols.Tls12,
            TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(TestTlsConnection.TlsCapabilities, connection));
        await using HttpConnectionListener listener = new(options);

        // Act
        IHttpContext context = await ReceiveFirstExchangeAsync(listener);
        ITlsConnectionInfo? tls = context.ConnectionInfo as ITlsConnectionInfo;

        // Assert
        tls.ShouldNotBeNull();
        tls.RemoteCertificate.ShouldBeSameAs(clientCertificate);
        tls.TlsProtocol.ShouldBe(SslProtocols.Tls12);
        tls.CipherSuite.ShouldBe(TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384);
        tls.ApplicationProtocol.ShouldBe(SslApplicationProtocol.Http11);
        context.Features.Get(tlsConnectionFeatureName).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - ConnectionInfo/Http2: Should carry the connection's TLS handshake")]
    public async Task ConnectionInfo_OnHttp2OverTls_ShouldCarryTheConnectionHandshake()
    {
        // Arrange
        using X509Certificate2 clientCertificate = CreateCertificate();
        TestTlsConnection connection = new(
            HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/tls", "https", "api.test"),
            SslApplicationProtocol.Http2,
            clientCertificate);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1AndHttp2(new TestConnectionListener(TestTlsConnection.TlsCapabilities, connection));
        await using HttpConnectionListener listener = new(options);

        // Act
        IHttpContext context = await ReceiveFirstExchangeAsync(listener);
        ITlsConnectionInfo? tls = context.ConnectionInfo as ITlsConnectionInfo;

        // Assert
        context.Version.ShouldBe(HttpVersion.Http20);
        tls.ShouldNotBeNull();
        tls.RemoteCertificate.ShouldBeSameAs(clientCertificate);
        tls.TlsProtocol.ShouldBe(SslProtocols.Tls13);
        tls.ApplicationProtocol.ShouldBe(SslApplicationProtocol.Http2);
        context.Features.Get(tlsConnectionFeatureName).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - ConnectionInfo/Http3: Should carry the QUIC connection's TLS handshake")]
    public async Task ConnectionInfo_OnHttp3_ShouldCarryTheQuicHandshake()
    {
        // Arrange — QUIC carries TLS 1.3 itself (RFC 9001); the handshake belongs to the multiplexed
        // connection, not to the request stream.
        using X509Certificate2 clientCertificate = CreateCertificate();
        TestConnection stream = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/tls", "https", "api.test"));
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(new TestTlsMultiplexedConnection(clientCertificate, stream)));
        await using HttpConnectionListener listener = new(options);

        // Act
        IHttpContext context = await ReceiveFirstExchangeAsync(listener);
        ITlsConnectionInfo? tls = context.ConnectionInfo as ITlsConnectionInfo;

        // Assert
        context.Version.ShouldBe(HttpVersion.Http30);
        tls.ShouldNotBeNull();
        tls.RemoteCertificate.ShouldBeSameAs(clientCertificate);
        tls.TlsProtocol.ShouldBe(SslProtocols.Tls13);
        tls.CipherSuite.ShouldBe(TlsCipherSuite.TLS_AES_128_GCM_SHA256);
        tls.ApplicationProtocol.ShouldBe(SslApplicationProtocol.Http3);
        context.Features.Get(tlsConnectionFeatureName).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - ConnectionInfo: Should carry no TLS facet on a cleartext exchange")]
    public async Task ConnectionInfo_OnCleartextConnection_ShouldCarryNoTlsFacet()
    {
        // Arrange
        TestConnection connection = new(
            HttpProtocolPayloadFactory.CreateHttp1Request("GET /plain HTTP/1.1\r\nHost: api.test\r\nConnection: close\r\n\r\n"));
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(connection));
        await using HttpConnectionListener listener = new(options);

        // Act
        IHttpContext context = await ReceiveFirstExchangeAsync(listener);

        // Assert
        context.ConnectionInfo.ShouldNotBeAssignableTo<ITlsConnectionInfo>();
        context.Features.Get(tlsConnectionFeatureName).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - ConnectionInfo: Should snapshot the handshake rather than expose the stream connection")]
    public async Task ConnectionInfo_OnTlsStreamConnection_ShouldNotExposeTheConnection()
    {
        // Arrange
        TestTlsConnection connection = new(
            HttpProtocolPayloadFactory.CreateHttp1Request("GET /tls HTTP/1.1\r\nHost: api.test\r\nConnection: close\r\n\r\n"),
            SslApplicationProtocol.Http11);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(TestTlsConnection.TlsCapabilities, connection));
        await using HttpConnectionListener listener = new(options);

        // Act
        IHttpContext context = await ReceiveFirstExchangeAsync(listener);

        // Assert — the facet is a copy, so a handler cannot cast its way to the connection.
        context.ConnectionInfo.ShouldBeAssignableTo<ITlsConnectionInfo>();
        context.ConnectionInfo.ShouldNotBeSameAs(connection);
        context.ConnectionInfo.ShouldNotBeAssignableTo<IConnection>();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - ConnectionInfo/Http3: Should snapshot the handshake rather than expose the QUIC connection")]
    public async Task ConnectionInfo_OnHttp3_ShouldNotExposeTheMultiplexedConnection()
    {
        // Arrange — a QUIC connection can open streams, which only the transport may do.
        TestTlsMultiplexedConnection multiplexed = new(
            null,
            new TestConnection(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/tls", "https", "api.test")));
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(multiplexed));
        await using HttpConnectionListener listener = new(options);

        // Act
        IHttpContext context = await ReceiveFirstExchangeAsync(listener);

        // Assert
        context.ConnectionInfo.ShouldBeAssignableTo<ITlsConnectionInfo>();
        context.ConnectionInfo.ShouldNotBeSameAs(multiplexed);
        context.ConnectionInfo.ShouldNotBeAssignableTo<IMultiplexedConnection>();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - ConnectionInfo: Should share one connection info across a connection's exchanges and outlive each exchange")]
    public async Task ConnectionInfo_OnKeepAliveExchanges_ShouldShareOneInstanceAndSurviveExchangeDisposal()
    {
        // Arrange — two requests on one keep-alive connection.
        using X509Certificate2 clientCertificate = CreateCertificate();
        TestTlsConnection connection = new(
            HttpProtocolPayloadFactory.CreateHttp1Request(
                "GET /first HTTP/1.1\r\nHost: api.test\r\n\r\n" +
                "GET /second HTTP/1.1\r\nHost: api.test\r\nConnection: close\r\n\r\n"),
            SslApplicationProtocol.Http11,
            clientCertificate);
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(TestTlsConnection.TlsCapabilities, connection));
        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();

        // Act — serve and dispose each exchange, as a host does.
        List<IHttpConnectionInfo> connectionInfos = new();
        await foreach (IHttpContext context in connectionContext.ReceiveAsync())
        {
            connectionInfos.Add(context.ConnectionInfo);
            context.Response.StatusCode = HttpStatusCode.Ok;
            await connectionContext.SendAsync(context);
            await context.DisposeAsync();
        }

        // Assert — one connection-scoped instance, and the exchange disposal walk left the certificate
        // (owned by the connection) alone.
        connectionInfos.Count.ShouldBe(2);
        connectionInfos[0].ShouldBeAssignableTo<ITlsConnectionInfo>();
        connectionInfos[1].ShouldBeSameAs(connectionInfos[0]);
        clientCertificate.Handle.ShouldNotBe(IntPtr.Zero);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - ConnectionInfo/Http1: Should show the TLS facet to request-parse and response hooks")]
    public async Task ConnectionInfo_OnHttp11Interceptors_ShouldShowTheTlsFacetFromTheRequestHead()
    {
        // Arrange
        TestTlsConnection connection = new(
            HttpProtocolPayloadFactory.CreateHttp1Request("GET /tls HTTP/1.1\r\nHost: api.test\r\nConnection: close\r\n\r\n"),
            SslApplicationProtocol.Http11);
        TlsObservingInterceptor interceptor = new();
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(new TestConnectionListener(TestTlsConnection.TlsCapabilities, connection));
        options.Interceptors.Add(interceptor);
        await using HttpConnectionListener listener = new(options);

        // Act
        IHttpContext context = await ReceiveFirstExchangeAsync(listener);

        // Assert
        AssertHooksObservedTheFacet(interceptor, context, SslApplicationProtocol.Http11);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - ConnectionInfo/Http2: Should show the TLS facet to request-parse and response hooks")]
    public async Task ConnectionInfo_OnHttp2Interceptors_ShouldShowTheTlsFacetFromTheRequestHead()
    {
        // Arrange
        TestTlsConnection connection = new(
            HttpProtocolPayloadFactory.CreateHttp2Request(1, "GET", "/tls", "https", "api.test"),
            SslApplicationProtocol.Http2);
        TlsObservingInterceptor interceptor = new();
        HttpConnectionListenerOptions options = new();
        options.UseHttp1AndHttp2(new TestConnectionListener(TestTlsConnection.TlsCapabilities, connection));
        options.Interceptors.Add(interceptor);
        await using HttpConnectionListener listener = new(options);

        // Act
        IHttpContext context = await ReceiveFirstExchangeAsync(listener);

        // Assert
        context.Version.ShouldBe(HttpVersion.Http20);
        AssertHooksObservedTheFacet(interceptor, context, SslApplicationProtocol.Http2);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - ConnectionInfo/Http3: Should show the TLS facet to request-parse and response hooks")]
    public async Task ConnectionInfo_OnHttp3Interceptors_ShouldShowTheTlsFacetFromTheRequestHead()
    {
        // Arrange
        TestConnection stream = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/tls", "https", "api.test"));
        TlsObservingInterceptor interceptor = new();
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(new TestTlsMultiplexedConnection(null, stream)));
        options.Interceptors.Add(interceptor);
        await using HttpConnectionListener listener = new(options);

        // Act
        IHttpContext context = await ReceiveFirstExchangeAsync(listener);

        // Assert
        context.Version.ShouldBe(HttpVersion.Http30);
        AssertHooksObservedTheFacet(interceptor, context, SslApplicationProtocol.Http3);
    }

    private static void AssertHooksObservedTheFacet(
        TlsObservingInterceptor interceptor,
        IHttpContext context,
        SslApplicationProtocol expectedProtocol)
    {
        // The request-parse hook runs before the exchange exists, so it sees the facet only because the
        // transport publishes it on the connection info it hands the hook; the response hook and the
        // exchange see that same instance.
        interceptor.TlsAtRequestHead.ShouldNotBeNull();
        interceptor.TlsAtRequestHead.ApplicationProtocol.ShouldBe(expectedProtocol);
        interceptor.ConnectionInfoAtRequestHead.ShouldBeSameAs(context.ConnectionInfo);
        interceptor.TlsBeforeResponse.ShouldNotBeNull();
        interceptor.ConnectionInfoBeforeResponse.ShouldBeSameAs(context.ConnectionInfo);
    }

    private static async Task<IHttpContext> ReceiveFirstExchangeAsync(HttpConnectionListener listener)
    {
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        await using IAsyncEnumerator<IHttpContext> enumerator = connectionContext.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return enumerator.Current;
    }

    private static X509Certificate2 CreateCertificate()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=cohesion-client", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
