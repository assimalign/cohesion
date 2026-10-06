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
/// Covers the transport's <see cref="IHttpTlsConnectionFeature"/>: every exchange that arrived over a
/// TLS-terminating connection carries the connection's session (client certificate, TLS protocol,
/// cipher suite, negotiated application protocol), on HTTP/1.1, HTTP/2, and HTTP/3, and a cleartext
/// exchange carries none. The connections are doubles that report a handshake through
/// <see cref="ITlsConnectionInfo"/>; the real handshakes are covered by the Connections.Security,
/// Connections.Quic, and Web.Hosting suites.
/// </summary>
public class HttpTlsConnectionFeatureTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Connections] - TlsConnection/Http1: Should expose the connection's TLS session")]
    public async Task TlsConnection_OnHttp11OverTls_ShouldExposeTheConnectionSession()
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
        IHttpTlsConnectionFeature? tls = context.TlsConnection;

        // Assert
        tls.ShouldNotBeNull();
        tls.ClientCertificate.ShouldBeSameAs(clientCertificate);
        tls.Protocol.ShouldBe(SslProtocols.Tls12);
        tls.CipherSuite.ShouldBe(TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384);
        tls.ApplicationProtocol.ShouldBe(SslApplicationProtocol.Http11);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - TlsConnection/Http2: Should expose the connection's TLS session")]
    public async Task TlsConnection_OnHttp2OverTls_ShouldExposeTheConnectionSession()
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
        IHttpTlsConnectionFeature? tls = context.TlsConnection;

        // Assert
        context.Version.ShouldBe(HttpVersion.Http20);
        tls.ShouldNotBeNull();
        tls.ClientCertificate.ShouldBeSameAs(clientCertificate);
        tls.Protocol.ShouldBe(SslProtocols.Tls13);
        tls.ApplicationProtocol.ShouldBe(SslApplicationProtocol.Http2);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - TlsConnection/Http3: Should expose the QUIC connection's TLS session")]
    public async Task TlsConnection_OnHttp3_ShouldExposeTheQuicSession()
    {
        // Arrange — QUIC carries TLS 1.3 itself (RFC 9001); the session lives on the multiplexed
        // connection, not on the request stream.
        using X509Certificate2 clientCertificate = CreateCertificate();
        TestConnection stream = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/tls", "https", "api.test"));
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(new TestTlsMultiplexedConnection(clientCertificate, stream)));
        await using HttpConnectionListener listener = new(options);

        // Act
        IHttpContext context = await ReceiveFirstExchangeAsync(listener);
        IHttpTlsConnectionFeature? tls = context.TlsConnection;

        // Assert
        context.Version.ShouldBe(HttpVersion.Http30);
        tls.ShouldNotBeNull();
        tls.ClientCertificate.ShouldBeSameAs(clientCertificate);
        tls.Protocol.ShouldBe(SslProtocols.Tls13);
        tls.CipherSuite.ShouldBe(TlsCipherSuite.TLS_AES_128_GCM_SHA256);
        tls.ApplicationProtocol.ShouldBe(SslApplicationProtocol.Http3);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - TlsConnection: Should be absent on a cleartext exchange")]
    public async Task TlsConnection_OnCleartextConnection_ShouldBeNull()
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
        context.TlsConnection.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - TlsConnection: Should share one session across a connection's exchanges and outlive each exchange")]
    public async Task TlsConnection_OnKeepAliveExchanges_ShouldShareOneSessionAndSurviveExchangeDisposal()
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
        List<IHttpTlsConnectionFeature?> sessions = new();
        await foreach (IHttpContext context in connectionContext.ReceiveAsync())
        {
            sessions.Add(context.TlsConnection);
            context.Response.StatusCode = HttpStatusCode.Ok;
            await connectionContext.SendAsync(context);
            await context.DisposeAsync();
        }

        // Assert — one connection-scoped instance, and the exchange disposal walk left the certificate
        // (owned by the connection) alone.
        sessions.Count.ShouldBe(2);
        sessions[0].ShouldNotBeNull();
        sessions[1].ShouldBeSameAs(sessions[0]);
        clientCertificate.Handle.ShouldNotBe(IntPtr.Zero);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - TlsConnection: Should be visible to response interceptors")]
    public async Task TlsConnection_OnResponseInterceptor_ShouldBeVisibleBeforeResponse()
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
        interceptor.Observed.ShouldNotBeNull();
        interceptor.Observed.ShouldBeSameAs(context.TlsConnection);
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
