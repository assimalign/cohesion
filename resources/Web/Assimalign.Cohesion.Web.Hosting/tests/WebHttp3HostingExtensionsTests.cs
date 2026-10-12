using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.Connections.Quic;
using Assimalign.Cohesion.Connections.Security;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web.Hosting.Internal;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>
/// Covers the HTTP/3 (QUIC) registration surface (<c>UseHttp3</c>) added to
/// <see cref="HttpConnectionListenerOptions"/> for issue #767. The deterministic tests pin argument
/// validation, ALPN/TLS-1.3 defaulting (and preservation), and that QUIC listener creation is
/// deferred to materialization while endpoint acquisition is deferred to
/// <see cref="IHttpConnectionListener.BindAsync(System.Threading.CancellationToken)"/> — none of which need a QUIC
/// implementation on the host. The platform-sensitive tests gate on <see cref="QuicListener.IsSupported"/>
/// and either await a successful bind or assert the documented <see cref="PlatformNotSupportedException"/>,
/// so a CI machine without libmsquic never hard-fails. The full request round-trip over QUIC lives in
/// <see cref="WebHttp3HostingIntegrationTests"/>.
/// </summary>
// System.Net.Quic is Windows/Linux/macOS only; the class is annotated to match UseHttp3 and the QUIC
// driver. Tests that bind a listener gate on QuicListener.IsSupported; unbound construction needs no msquic.
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

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp3: The TLS handshake timeout should override the QUIC callback and preserve disabling")]
    [InlineData(null, 10000)]
    [InlineData(3500, 3500)]
    [InlineData(0, -1)]
    [InlineData(-1, -1)]
    [InlineData(-3000, -1)]
    public async Task UseHttp3WithTlsOptions_OnHandshakeTimeout_ShouldMapToQuic(int? milliseconds, int expectedMilliseconds)
    {
        // Arrange — an explicit TLS surface overrides a conflicting timeout from the raw QUIC callback.
        HttpConnectionListenerOptions options = new();
        TlsServerOptions tlsOptions = new();
        if (milliseconds is { } value)
        {
            tlsOptions.HandshakeTimeout = TimeSpan.FromMilliseconds(value);
        }

        QuicConnectionListenerOptions? configured = null;
        options.UseHttp3(quic =>
        {
            configured = quic;
            quic.HandshakeTimeout = TimeSpan.FromSeconds(99);
        }, tlsOptions);

        // Act — materialize the deferred callback without acquiring an endpoint or using native QUIC.
        await using HttpConnectionListener listener = new(options);

        // Assert — a non-positive TLS value maps to QUIC's explicit infinite sentinel, including zero.
        listener.Protocols.ShouldBe(HttpProtocol.Http30);
        configured.ShouldNotBeNull().HandshakeTimeout.ShouldBe(TimeSpan.FromMilliseconds(expectedMilliseconds));
        configured.ServerAuthenticationOptions.ShouldBeSameAs(tlsOptions.AuthenticationOptions);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - HTTP/3 configuration: The endpoint handshake timeout should flow through TLS options into QUIC")]
    [InlineData(null, 10000, 10000)]
    [InlineData("3", 3000, 3000)]
    [InlineData("00:00:03.500", 3500, 3500)]
    [InlineData("0", 0, -1)]
    [InlineData("infinite", -1, -1)]
    [InlineData("-1", -1, -1)]
    [InlineData("-3", -3000, -1)]
    public async Task CreateTlsOptions_OnConfiguredHandshakeTimeout_ShouldFlowToHttp3(
        string? raw, int tlsMilliseconds, int quicMilliseconds)
    {
        // Arrange — use the same TLS composition routine the endpoint binder calls for HTTP/3.
        using CertificateFiles files = CertificateFiles.Create();
        IConfiguration configuration = SeededConfiguration.Build(new Dictionary<string, string?>
        {
            ["Http:Endpoints:Quic:Protocol"] = "Http3",
            ["Http:Endpoints:Quic:Port"] = "0",
            ["Http:Endpoints:Quic:Certificate:Path"] = files.PfxPath,
            ["Http:Endpoints:Quic:Certificate:Password"] = CertificateFiles.Password,
            ["Http:Endpoints:Quic:HandshakeTimeout"] = raw,
        });
        IConfigurationSection endpoint = configuration.GetEntry("Http:Endpoints:Quic").ShouldBeAssignableTo<IConfigurationSection>()!;
        List<X509Certificate2> owned = new();

        try
        {
            // Act — bind TLS settings, then materialize an unbound QUIC listener through the public overload.
            TlsServerOptions tlsOptions = HttpServerConfiguration.CreateTlsOptions(endpoint, "Quic", owned.Add, contentRootPath: null);
            HttpConnectionListenerOptions options = new();
            QuicConnectionListenerOptions? configured = null;
            options.UseHttp3(quic => configured = quic, tlsOptions);
            await using HttpConnectionListener listener = new(options);

            // Assert — bare integers are seconds, and TLS's disabling semantics survive the QUIC mapping.
            tlsOptions.HandshakeTimeout.ShouldBe(TimeSpan.FromMilliseconds(tlsMilliseconds));
            configured.ShouldNotBeNull().HandshakeTimeout.ShouldBe(TimeSpan.FromMilliseconds(quicMilliseconds));
            listener.Protocols.ShouldBe(HttpProtocol.Http30);
        }
        finally
        {
            foreach (X509Certificate2 certificate in owned)
            {
                certificate.Dispose();
            }
        }
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

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp3: Should construct unbound and bind QUIC explicitly or throw PlatformNotSupportedException")]
    public async Task UseHttp3_OnBindAsync_ShouldBindOrThrowPlatformNotSupported()
    {
        // Arrange
        using X509Certificate2 certificate = TestObjects.SelfSignedCertificateFactory.Create("localhost");
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(quic =>
        {
            quic.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
            quic.ServerAuthenticationOptions.ServerCertificate = certificate;
        });

        // Act — materialization constructs an unbound QUIC listener on every supported target OS.
        await using HttpConnectionListener listener = new(options);
        listener.Protocols.ShouldBe(HttpProtocol.Http30);

        // Assert — operating-system endpoint acquisition happens only at the awaited bind boundary.
        if (QuicListener.IsSupported)
        {
            await listener.BindAsync();
        }
        else
        {
            await Should.ThrowAsync<PlatformNotSupportedException>(() => listener.BindAsync().AsTask());
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
        await listener.BindAsync();

        // Assert — both protocols are registered and both endpoint binds completed.
        listener.Protocols.ShouldBe(HttpProtocol.Http20 | HttpProtocol.Http30);
    }
}
