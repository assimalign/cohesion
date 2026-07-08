using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Security;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Http.Connections;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>
/// Covers the TLS convenience surface (<c>UseHttp1s</c> / <c>UseHttp2s</c>) added to
/// <see cref="HttpConnectionListenerOptions"/> for issue #763. These tests pin the deterministic
/// behaviour: argument validation, ALPN application-protocol defaulting (and preservation), and that
/// the secured listener registers under the correct HTTP protocol with deferred construction. The
/// end-to-end TLS handshake is exercised separately in <see cref="WebTlsHostingIntegrationTests"/>.
/// </summary>
public class WebTlsHostingExtensionsTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp1s: Should throw when the configure callback is null")]
    public void UseHttp1s_WithNullConfigure_ShouldThrowArgumentNullException()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => options.UseHttp1s(null!, new TlsServerOptions()));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp2s: Should throw when the configure callback is null")]
    public void UseHttp2s_WithNullConfigure_ShouldThrowArgumentNullException()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => options.UseHttp2s(null!, new TlsServerOptions()));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp1s: Should throw when the TLS options are null")]
    public void UseHttp1s_WithNullTlsOptions_ShouldThrowArgumentNullException()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => options.UseHttp1s(tcp => { }, null!));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp2s: Should throw when the TLS options are null")]
    public void UseHttp2s_WithNullTlsOptions_ShouldThrowArgumentNullException()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => options.UseHttp2s(tcp => { }, null!));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp2s: Should default ALPN to h2 when application protocols are unset")]
    public void UseHttp2s_WithUnsetApplicationProtocols_ShouldDefaultToH2()
    {
        // Arrange — a fresh TlsServerOptions leaves ApplicationProtocols null.
        HttpConnectionListenerOptions options = new();
        TlsServerOptions tlsOptions = new();
        tlsOptions.AuthenticationOptions.ApplicationProtocols.ShouldBeNull();

        // Act
        options.UseHttp2s(tcp => { }, tlsOptions);

        // Assert — h2 is installed so the secured listener is reachable as HTTP/2 via ALPN (RFC 7301).
        tlsOptions.AuthenticationOptions.ApplicationProtocols.ShouldNotBeNull();
        tlsOptions.AuthenticationOptions.ApplicationProtocols.ShouldBe(new List<SslApplicationProtocol>
        {
            SslApplicationProtocol.Http2
        });
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp1s: Should default ALPN to http/1.1 when application protocols are unset")]
    public void UseHttp1s_WithUnsetApplicationProtocols_ShouldDefaultToHttp11()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();
        TlsServerOptions tlsOptions = new();

        // Act
        options.UseHttp1s(tcp => { }, tlsOptions);

        // Assert
        tlsOptions.AuthenticationOptions.ApplicationProtocols.ShouldNotBeNull();
        tlsOptions.AuthenticationOptions.ApplicationProtocols.ShouldBe(new List<SslApplicationProtocol>
        {
            SslApplicationProtocol.Http11
        });
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp2s: Should default ALPN when application protocols are an empty list")]
    public void UseHttp2s_WithEmptyApplicationProtocols_ShouldDefaultToH2()
    {
        // Arrange — an empty (but non-null) list is treated as "unset".
        HttpConnectionListenerOptions options = new();
        TlsServerOptions tlsOptions = new()
        {
            AuthenticationOptions = new SslServerAuthenticationOptions
            {
                ApplicationProtocols = new List<SslApplicationProtocol>()
            }
        };

        // Act
        options.UseHttp2s(tcp => { }, tlsOptions);

        // Assert
        tlsOptions.AuthenticationOptions.ApplicationProtocols.ShouldBe(new List<SslApplicationProtocol>
        {
            SslApplicationProtocol.Http2
        });
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp2s: Should preserve caller-supplied application protocols")]
    public void UseHttp2s_WithExplicitApplicationProtocols_ShouldPreserveThem()
    {
        // Arrange — a caller offering both h2 and http/1.1 on one endpoint must not be overridden.
        HttpConnectionListenerOptions options = new();
        List<SslApplicationProtocol> explicitProtocols = new()
        {
            SslApplicationProtocol.Http2,
            SslApplicationProtocol.Http11
        };
        TlsServerOptions tlsOptions = new()
        {
            AuthenticationOptions = new SslServerAuthenticationOptions
            {
                ApplicationProtocols = explicitProtocols
            }
        };

        // Act
        options.UseHttp2s(tcp => { }, tlsOptions);

        // Assert — same instance, unmodified.
        tlsOptions.AuthenticationOptions.ApplicationProtocols.ShouldBeSameAs(explicitProtocols);
        tlsOptions.AuthenticationOptions.ApplicationProtocols.ShouldBe(new List<SslApplicationProtocol>
        {
            SslApplicationProtocol.Http2,
            SslApplicationProtocol.Http11
        });
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp1s: Should preserve caller-supplied application protocols")]
    public void UseHttp1s_WithExplicitApplicationProtocols_ShouldPreserveThem()
    {
        // Arrange — an explicit non-default choice (h2 only) must survive UseHttp1s untouched.
        HttpConnectionListenerOptions options = new();
        List<SslApplicationProtocol> explicitProtocols = new()
        {
            SslApplicationProtocol.Http2
        };
        TlsServerOptions tlsOptions = new()
        {
            AuthenticationOptions = new SslServerAuthenticationOptions
            {
                ApplicationProtocols = explicitProtocols
            }
        };

        // Act
        options.UseHttp1s(tcp => { }, tlsOptions);

        // Assert
        tlsOptions.AuthenticationOptions.ApplicationProtocols.ShouldBeSameAs(explicitProtocols);
        tlsOptions.AuthenticationOptions.ApplicationProtocols.ShouldBe(new List<SslApplicationProtocol>
        {
            SslApplicationProtocol.Http2
        });
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp1s: Should defer TCP listener creation and register HTTP/1.1")]
    public async Task UseHttp1s_WithConfiguredOptions_ShouldDeferCreationAndRegisterHttp11()
    {
        // Arrange
        bool configured = false;
        HttpConnectionListenerOptions options = new();
        using System.Security.Cryptography.X509Certificates.X509Certificate2 certificate =
            TestObjects.SelfSignedCertificateFactory.Create("localhost");

        // Act
        HttpConnectionListenerOptions result = options.UseHttp1s(
            tcp =>
            {
                configured = true;
                tcp.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
            },
            new TlsServerOptions
            {
                AuthenticationOptions = { ServerCertificate = certificate }
            });

        // Assert — the wrapper returns the same options for chaining and defers construction: the
        // TCP listener (and its TLS layer) are not built until the HttpConnectionListener materializes.
        result.ShouldBeSameAs(options);
        configured.ShouldBeFalse();

        await using HttpConnectionListener listener = new(options);

        configured.ShouldBeTrue();
        listener.Protocols.ShouldBe(HttpProtocol.Http11);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp2s: Should defer TCP listener creation and register HTTP/2")]
    public async Task UseHttp2s_WithConfiguredOptions_ShouldDeferCreationAndRegisterHttp20()
    {
        // Arrange
        bool configured = false;
        HttpConnectionListenerOptions options = new();
        using System.Security.Cryptography.X509Certificates.X509Certificate2 certificate =
            TestObjects.SelfSignedCertificateFactory.Create("localhost");

        // Act
        HttpConnectionListenerOptions result = options.UseHttp2s(
            tcp =>
            {
                configured = true;
                tcp.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
            },
            new TlsServerOptions
            {
                AuthenticationOptions = { ServerCertificate = certificate }
            });

        // Assert
        result.ShouldBeSameAs(options);
        configured.ShouldBeFalse();

        await using HttpConnectionListener listener = new(options);

        configured.ShouldBeTrue();
        listener.Protocols.ShouldBe(HttpProtocol.Http20);
    }
}
