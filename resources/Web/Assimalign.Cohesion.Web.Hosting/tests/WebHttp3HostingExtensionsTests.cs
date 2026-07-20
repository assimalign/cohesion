using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Quic;
using Assimalign.Cohesion.Connections.Security;
using Assimalign.Cohesion.Http.Connections;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>
/// Covers the HTTP/3 (QUIC) registration surface (<c>UseHttp3</c>) added to
/// <see cref="HttpConnectionListenerOptions"/> for issue #767. The deterministic tests pin argument
/// validation, ALPN/TLS-1.3 defaulting (and preservation), and that the QUIC listener creation is
/// deferred to materialization rather than run at configuration time — none of which need a QUIC
/// implementation on the host. The platform-sensitive tests gate on <see cref="QuicListener.IsSupported"/>
/// and either assert the bind succeeds or assert the documented <see cref="PlatformNotSupportedException"/>,
/// so a CI machine without libmsquic never hard-fails. The full request round-trip over QUIC lives in
/// <see cref="WebHttp3HostingIntegrationTests"/>.
/// </summary>
// System.Net.Quic is Windows/Linux/macOS only; the class is annotated to match UseHttp3 and the QUIC
// driver, and every test that materializes a listener also gates on QuicListener.IsSupported at runtime.
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public class WebHttp3HostingExtensionsTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp3: Should throw when the configure callback is null")]
    public void UseHttp3_WithNullConfigure_ShouldThrowArgumentNullException()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => options.UseHttp3((Action<QuicConnectionListenerOptions>)null!));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp3 (TLS options): Should throw when the configure callback is null")]
    public void UseHttp3WithTlsOptions_WithNullConfigure_ShouldThrowArgumentNullException()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => options.UseHttp3(null!, new TlsServerOptions()));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp3 (TLS options): Should throw when the TLS options are null")]
    public void UseHttp3WithTlsOptions_WithNullTlsOptions_ShouldThrowArgumentNullException()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => options.UseHttp3(quic => { }, null!));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp3 (TLS options): Should default ALPN to h3 and TLS to 1.3 when unset")]
    public void UseHttp3WithTlsOptions_WithUnsetApplicationProtocols_ShouldDefaultToHttp3AndTls13()
    {
        // Arrange — a fresh TlsServerOptions leaves ApplicationProtocols null and EnabledSslProtocols None.
        HttpConnectionListenerOptions options = new();
        TlsServerOptions tlsOptions = new();
        tlsOptions.AuthenticationOptions.ApplicationProtocols.ShouldBeNull();
        tlsOptions.AuthenticationOptions.EnabledSslProtocols.ShouldBe(SslProtocols.None);

        // Act
        options.UseHttp3(quic => { }, tlsOptions);

        // Assert — h3 ALPN makes the listener reachable as HTTP/3; TLS 1.3 is inherent to QUIC.
        tlsOptions.AuthenticationOptions.ApplicationProtocols.ShouldNotBeNull();
        tlsOptions.AuthenticationOptions.ApplicationProtocols.ShouldBe(new List<SslApplicationProtocol>
        {
            SslApplicationProtocol.Http3
        });
        tlsOptions.AuthenticationOptions.EnabledSslProtocols.ShouldBe(SslProtocols.Tls13);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp3 (TLS options): Should preserve caller-supplied application protocols")]
    public void UseHttp3WithTlsOptions_WithExplicitApplicationProtocols_ShouldPreserveThem()
    {
        // Arrange — an explicit ALPN choice must survive UseHttp3 untouched.
        HttpConnectionListenerOptions options = new();
        List<SslApplicationProtocol> explicitProtocols = new()
        {
            SslApplicationProtocol.Http3
        };
        TlsServerOptions tlsOptions = new()
        {
            AuthenticationOptions = new SslServerAuthenticationOptions
            {
                ApplicationProtocols = explicitProtocols
            }
        };

        // Act
        options.UseHttp3(quic => { }, tlsOptions);

        // Assert — same instance, unmodified.
        tlsOptions.AuthenticationOptions.ApplicationProtocols.ShouldBeSameAs(explicitProtocols);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp3: Should return the same options for chaining and defer QUIC creation")]
    public void UseHttp3_WithConfiguredOptions_ShouldDeferCreation()
    {
        // Arrange
        bool configured = false;
        HttpConnectionListenerOptions options = new();

        // Act
        HttpConnectionListenerOptions result = options.UseHttp3(quic =>
        {
            configured = true;
            quic.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
        });

        // Assert — the wrapper returns the same options for fluent chaining and defers construction:
        // the QUIC listener (whose bind is async) is not created until the HttpConnectionListener
        // materializes the registration at server start. This holds on every platform because no
        // QUIC listener is bound here.
        result.ShouldBeSameAs(options);
        configured.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp3: Should bind the QUIC listener at materialization or throw PlatformNotSupportedException at start")]
    public async Task UseHttp3_OnMaterialization_ShouldBindOrThrowPlatformNotSupported()
    {
        // Arrange
        using X509Certificate2 certificate = TestObjects.SelfSignedCertificateFactory.Create("localhost");
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(quic =>
        {
            quic.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
            quic.ServerAuthenticationOptions.ServerCertificate = certificate;
        });

        // Act / Assert — materialization happens in the HttpConnectionListener constructor.
        if (QuicListener.IsSupported)
        {
            await using HttpConnectionListener listener = new(options);

            listener.Protocols.ShouldBe(HttpProtocol.Http30);
        }
        else
        {
            // On a platform without a QUIC implementation the deferred factory surfaces the driver's
            // PlatformNotSupportedException at start, exactly as QuicConnectionListener.CreateAsync does.
            Should.Throw<PlatformNotSupportedException>(() => new HttpConnectionListener(options));
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp3: Should register HTTP/2 and HTTP/3 simultaneously on one listener")]
    public async Task UseHttp3_WithHttp2s_ShouldRegisterBothProtocols()
    {
        // Coexistence (issue #767 acceptance): h2 and h3 on one WebApplication over different
        // endpoints. With advertisement enabled the listener has both a stream protocol to carry the
        // Alt-Svc header and an h3 endpoint to advertise. Gated on QUIC support because it binds a
        // real QUIC listener.
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using X509Certificate2 certificate = TestObjects.SelfSignedCertificateFactory.Create("localhost");
        HttpConnectionListenerOptions options = new();
        options.UseHttp2s(
            tcp => tcp.EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            new TlsServerOptions
            {
                AuthenticationOptions = { ServerCertificate = certificate }
            });
        options.UseHttp3(quic =>
        {
            quic.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
            quic.ServerAuthenticationOptions.ServerCertificate = certificate;
        });
        options.AdvertiseAltService(_ => { });

        // Act
        await using HttpConnectionListener listener = new(options);

        // Assert — both protocols are registered on the single listener.
        listener.Protocols.ShouldBe(HttpProtocol.Http20 | HttpProtocol.Http30);
    }
}
