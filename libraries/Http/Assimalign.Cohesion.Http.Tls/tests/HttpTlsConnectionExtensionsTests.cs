using System;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Assimalign.Cohesion.Http.Tls.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tls.Tests;

/// <summary>
/// Covers <c>context.TlsConnection</c>: it builds the feature from the <c>ITlsConnectionInfo</c> facet
/// the transport publishes on the exchange's connection info, caches it in the exchange's features,
/// and defers to a feature a middleware installed first. The transport's publication of the facet is
/// covered by the Http.Connections suite, and real handshakes by the Web.Hosting suite.
/// </summary>
public class HttpTlsConnectionExtensionsTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Tls] - TlsConnection: Should build the feature from the connection's TLS facet")]
    public void TlsConnection_WhenConnectionInfoCarriesTlsFacet_ShouldBuildFeatureFromIt()
    {
        // Arrange
        using X509Certificate2 clientCertificate = CreateCertificate();
        TestHttpContext context = new(new TestTlsConnectionInfo(
            SslApplicationProtocol.Http2,
            clientCertificate,
            SslProtocols.Tls12,
            TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384));

        // Act
        IHttpTlsConnectionFeature? tls = context.TlsConnection;

        // Assert
        tls.ShouldNotBeNull();
        tls.ClientCertificate.ShouldBeSameAs(clientCertificate);
        tls.Protocol.ShouldBe(SslProtocols.Tls12);
        tls.CipherSuite.ShouldBe(TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384);
        tls.ApplicationProtocol.ShouldBe(SslApplicationProtocol.Http2);
        tls.Name.ShouldBe("Assimalign.Cohesion.Http.TlsConnection");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Tls] - TlsConnection: Should cache the built feature in the exchange's features")]
    public void TlsConnection_OnRepeatedReads_ShouldReturnTheCachedFeature()
    {
        // Arrange
        TestHttpContext context = new(new TestTlsConnectionInfo(SslApplicationProtocol.Http11));

        // Act
        IHttpTlsConnectionFeature? first = context.TlsConnection;
        IHttpTlsConnectionFeature? second = context.TlsConnection;

        // Assert
        first.ShouldNotBeNull();
        second.ShouldBeSameAs(first);
        context.Features.Get<IHttpTlsConnectionFeature>().ShouldBeSameAs(first);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Tls] - TlsConnection: Should return a feature a middleware installed instead of the connection's facet")]
    public void TlsConnection_WhenFeatureInstalled_ShouldReturnItOverTheFacet()
    {
        // Arrange — a middleware supplies its own session (for example behind a TLS-terminating proxy).
        TestHttpContext context = new(new TestTlsConnectionInfo(SslApplicationProtocol.Http2));
        TestTlsConnectionFeature installed = new();
        context.Features.Set(installed);

        // Act
        IHttpTlsConnectionFeature? tls = context.TlsConnection;

        // Assert
        tls.ShouldBeSameAs(installed);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Tls] - TlsConnection: Should return the installed feature on a cleartext exchange")]
    public void TlsConnection_WhenFeatureInstalledOnCleartextExchange_ShouldReturnIt()
    {
        // Arrange
        TestHttpContext context = new(new HttpConnectionInfo());
        TestTlsConnectionFeature installed = new();
        context.Features.Set(installed);

        // Act
        IHttpTlsConnectionFeature? tls = context.TlsConnection;

        // Assert
        tls.ShouldBeSameAs(installed);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Tls] - TlsConnection: Should return null and install nothing for an exchange without a TLS session")]
    public void TlsConnection_OnCleartextExchange_ShouldReturnNullAndInstallNothing()
    {
        // Arrange
        TestHttpContext context = new(new HttpConnectionInfo());

        // Act
        IHttpTlsConnectionFeature? tls = context.TlsConnection;

        // Assert
        tls.ShouldBeNull();
        context.Features.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Tls] - TlsConnection: Should build a feature the exchange's disposal walk leaves alone")]
    public void TlsConnection_BuiltFeature_ShouldNotBeDisposable()
    {
        // Arrange — the certificate belongs to the connection, which outlives every exchange.
        TestHttpContext context = new(new TestTlsConnectionInfo(SslApplicationProtocol.Http11));

        // Act
        IHttpTlsConnectionFeature? tls = context.TlsConnection;

        // Assert
        tls.ShouldNotBeNull();
        tls.ShouldNotBeAssignableTo<IDisposable>();
        tls.ShouldNotBeAssignableTo<IAsyncDisposable>();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Tls] - TlsConnection: Should throw for a null context")]
    public void TlsConnection_WithNullContext_ShouldThrowArgumentNullException()
    {
        // Arrange
        IHttpContext context = null!;

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => context.TlsConnection);
    }

    private static X509Certificate2 CreateCertificate()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=cohesion-client", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
