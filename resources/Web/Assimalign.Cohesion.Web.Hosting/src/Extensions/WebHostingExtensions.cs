using System;
using System.Collections.Generic;
using System.Net.Security;

using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.Security;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web.Hosting.Internal;

namespace Assimalign.Cohesion.Web.Hosting;

/// <summary>
/// Provides hosting convenience extension members for <see cref="HttpConnectionListenerOptions"/>
/// and <see cref="WebApplicationServerBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// The plaintext <see cref="WebHostingExtensions.UseHttp1(HttpConnectionListenerOptions, Action{TcpConnectionListenerOptions})"/>
/// / <see cref="WebHostingExtensions.UseHttp2(HttpConnectionListenerOptions, Action{TcpConnectionListenerOptions})"/>
/// members register a bare TCP listener; the TLS-enabled
/// <see cref="WebHostingExtensions.UseHttp1s(HttpConnectionListenerOptions, Action{TcpConnectionListenerOptions}, TlsServerOptions)"/>
/// / <see cref="WebHostingExtensions.UseHttp2s(HttpConnectionListenerOptions, Action{TcpConnectionListenerOptions}, TlsServerOptions)"/>
/// members compose the security library's <c>UseTls</c> layer onto that TCP listener <em>before</em>
/// registration. TLS is never configured inside <c>Assimalign.Cohesion.Http.Connections</c> — it is a
/// pre-composed transport layer, and the layered listener's
/// <see cref="ConnectionCapabilities.Security"/> is the single source of truth from which the HTTP
/// layer derives the <c>https</c> scheme.
/// </para>
/// <para>
/// HTTP/3 has no callback-based overload here because QUIC listeners bind asynchronously
/// (<c>QuicConnectionListener.CreateAsync</c>) and QUIC's transport security is always-on; create the
/// listener first and register it through
/// <see cref="HttpConnectionListenerOptions.UseHttp3(Assimalign.Cohesion.Connections.IMultiplexedConnectionListener)"/>.
/// </para>
/// </remarks>
public static class WebHostingExtensions
{
    extension(WebApplicationServerBuilder builder)
    {
        /// <summary>
        /// Configures the web server's listener endpoints and per-endpoint limits from a Cohesion
        /// <see cref="IConfiguration"/> section at builder time. Binding is explicit and AOT-safe
        /// (no reflection); the bound <c>Limits</c> section is applied to every endpoint the
        /// section registers (all keys to HTTP/1.1 endpoints; the shared
        /// <see cref="HttpConnectionListenerLimits"/> keys to HTTP/2 endpoints). See
        /// <c>HttpServerConfiguration</c> for the expected section shape (Kestrel
        /// <c>appsettings</c> parity).
        /// </summary>
        /// <param name="configuration">The configuration to bind from.</param>
        /// <param name="sectionKey">The root section key. Defaults to <c>"Http"</c>.</param>
        /// <returns>The current server builder.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="configuration"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">Thrown when a configured value cannot be parsed.</exception>
        public WebApplicationServerBuilder UseConfiguration(IConfiguration configuration, string sectionKey = HttpServerConfiguration.DefaultSectionKey)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            return builder.UseServer((_, options) => HttpServerConfiguration.Bind(configuration, sectionKey, options));
        }
    }

    extension(HttpConnectionListenerOptions options)
    {
        /// <summary>
        /// Serves HTTP/1.1 over a TCP connection listener configured by the supplied callback.
        /// </summary>
        /// <param name="configure">The TCP listener configuration callback.</param>
        /// <returns>The current options instance.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
        public HttpConnectionListenerOptions UseHttp1(Action<TcpConnectionListenerOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            return options.UseHttp1(() => TcpConnectionListener.Create(configure));
        }

        /// <summary>
        /// Serves prior-knowledge HTTP/2 over a TCP connection listener configured by the supplied callback.
        /// </summary>
        /// <param name="configure">The TCP listener configuration callback.</param>
        /// <returns>The current options instance.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
        public HttpConnectionListenerOptions UseHttp2(Action<TcpConnectionListenerOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            return options.UseHttp2(() => TcpConnectionListener.Create(configure));
        }

        /// <summary>
        /// Serves HTTP/1.1 over TLS by composing the security library's <c>UseTls</c> layer onto a TCP
        /// connection listener configured by the supplied callback, then registering the secured
        /// listener for HTTP/1.1.
        /// </summary>
        /// <param name="configure">The TCP listener configuration callback (endpoint, socket options).</param>
        /// <param name="tlsOptions">
        /// The server TLS options. The caller supplies the server certificate through
        /// <see cref="TlsServerOptions.AuthenticationOptions"/>; certificate sourcing is a Security-area
        /// concern and is intentionally not modeled here. When
        /// <see cref="SslServerAuthenticationOptions.ApplicationProtocols"/> is left unset, it is defaulted
        /// to <see cref="SslApplicationProtocol.Http11"/> (ALPN <c>http/1.1</c>, RFC 7301); a caller-supplied
        /// protocol list is preserved unmodified.
        /// </param>
        /// <returns>The current options instance.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> or <paramref name="tlsOptions"/> is <see langword="null"/>.</exception>
        /// <remarks>
        /// TLS is composed <em>before</em> registration, so the layered listener reports
        /// <see cref="ConnectionCapabilities.Security"/> equal to <see cref="ConnectionSecurity.Tls"/>. The
        /// HTTP layer derives the <c>https</c> scheme from that capability — there is no registration-time
        /// <c>isSecure</c> flag.
        /// </remarks>
        public HttpConnectionListenerOptions UseHttp1s(Action<TcpConnectionListenerOptions> configure, TlsServerOptions tlsOptions)
        {
            ArgumentNullException.ThrowIfNull(configure);
            ArgumentNullException.ThrowIfNull(tlsOptions);

            EnsureApplicationProtocols(tlsOptions, SslApplicationProtocol.Http11);

            return options.UseHttp1(() => TcpConnectionListener.Create(configure).UseTls(tlsOptions));
        }

        /// <summary>
        /// Serves HTTP/2 over TLS by composing the security library's <c>UseTls</c> layer onto a TCP
        /// connection listener configured by the supplied callback, then registering the secured
        /// listener for HTTP/2.
        /// </summary>
        /// <param name="configure">The TCP listener configuration callback (endpoint, socket options).</param>
        /// <param name="tlsOptions">
        /// The server TLS options. The caller supplies the server certificate through
        /// <see cref="TlsServerOptions.AuthenticationOptions"/>; certificate sourcing is a Security-area
        /// concern and is intentionally not modeled here. When
        /// <see cref="SslServerAuthenticationOptions.ApplicationProtocols"/> is left unset, it is defaulted
        /// to <see cref="SslApplicationProtocol.Http2"/> (ALPN <c>h2</c>, RFC 7301); a caller-supplied
        /// protocol list is preserved unmodified.
        /// </param>
        /// <returns>The current options instance.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> or <paramref name="tlsOptions"/> is <see langword="null"/>.</exception>
        /// <remarks>
        /// TLS is composed <em>before</em> registration, so the layered listener reports
        /// <see cref="ConnectionCapabilities.Security"/> equal to <see cref="ConnectionSecurity.Tls"/>. The
        /// HTTP layer derives the <c>https</c> scheme from that capability — there is no registration-time
        /// <c>isSecure</c> flag. Browsers and the .NET HTTP client negotiate HTTP/2 over TLS via ALPN, so
        /// the defaulted <c>h2</c> protocol id is what makes the secured listener reachable as HTTP/2.
        /// </remarks>
        public HttpConnectionListenerOptions UseHttp2s(Action<TcpConnectionListenerOptions> configure, TlsServerOptions tlsOptions)
        {
            ArgumentNullException.ThrowIfNull(configure);
            ArgumentNullException.ThrowIfNull(tlsOptions);

            EnsureApplicationProtocols(tlsOptions, SslApplicationProtocol.Http2);

            return options.UseHttp2(() => TcpConnectionListener.Create(configure).UseTls(tlsOptions));
        }
    }

    /// <summary>
    /// Defaults the ALPN application-protocol list on the supplied TLS options when the caller left it
    /// unset, preserving any caller-supplied list. Mutation is intentional: the default is applied to the
    /// caller's options so a subsequent read observes the negotiated protocol.
    /// </summary>
    /// <param name="tlsOptions">The TLS options whose application protocols are defaulted.</param>
    /// <param name="defaultProtocol">The ALPN protocol id to install when none was supplied.</param>
    private static void EnsureApplicationProtocols(TlsServerOptions tlsOptions, SslApplicationProtocol defaultProtocol)
    {
        SslServerAuthenticationOptions authentication = tlsOptions.AuthenticationOptions;

        if (authentication.ApplicationProtocols is null || authentication.ApplicationProtocols.Count == 0)
        {
            authentication.ApplicationProtocols = new List<SslApplicationProtocol> { defaultProtocol };
        }
    }
}
