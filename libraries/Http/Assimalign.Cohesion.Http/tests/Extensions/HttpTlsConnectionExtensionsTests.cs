using System;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

using Assimalign.Cohesion.Http.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpTlsConnectionExtensionsTests
{
    [Fact(DisplayName = "Cohesion Test [Http] - TlsConnection: Should return the TLS connection feature attached to the exchange")]
    public void TlsConnection_WhenFeatureAttached_ShouldReturnIt()
    {
        // Arrange
        TestHttpContext context = new(HttpVersion.Http20);
        TestTlsConnectionFeature feature = new();
        context.Features.Set(feature);

        // Act
        IHttpTlsConnectionFeature? tls = context.TlsConnection;

        // Assert
        tls.ShouldBeSameAs(feature);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - TlsConnection: Should return null for an exchange without a TLS session")]
    public void TlsConnection_WhenNoFeature_ShouldReturnNull()
    {
        // Arrange
        TestHttpContext context = new(HttpVersion.Http11);

        // Act
        IHttpTlsConnectionFeature? tls = context.TlsConnection;

        // Assert
        tls.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - TlsConnection: Should throw for a null context")]
    public void TlsConnection_WithNullContext_ShouldThrowArgumentNullException()
    {
        // Arrange
        IHttpContext context = null!;

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => context.TlsConnection);
    }

    private sealed class TestTlsConnectionFeature : IHttpTlsConnectionFeature
    {
        public string Name => "Assimalign.Cohesion.Http.TlsConnection";

        public X509Certificate2? ClientCertificate => null;

        public SslProtocols Protocol => SslProtocols.Tls13;

        public TlsCipherSuite CipherSuite => TlsCipherSuite.TLS_AES_128_GCM_SHA256;

        public SslApplicationProtocol ApplicationProtocol => SslApplicationProtocol.Http2;
    }
}
