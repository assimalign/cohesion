using System;
using System.Collections.Generic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Threading.Tasks;

using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.Quic;
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
/// HTTP/3 is served over QUIC through the callback members
/// <see cref="WebHostingExtensions.UseHttp3(HttpConnectionListenerOptions, Action{QuicConnectionListenerOptions})"/>
/// and
/// <see cref="WebHostingExtensions.UseHttp3(HttpConnectionListenerOptions, Action{QuicConnectionListenerOptions}, TlsServerOptions)"/>.
/// Unlike the stream protocols there is no plaintext form: QUIC's transport security is always-on
/// (TLS 1.3 is inherent to the protocol, RFC 9001), so both members register a secured listener whose
/// <see cref="ConnectionCapabilities.Security"/> is <see cref="ConnectionSecurity.Tls"/> and the HTTP
/// layer derives the <c>https</c> scheme from it, exactly as the <c>UseHttp1s</c>/<c>UseHttp2s</c>
/// members do for TCP.
/// </para>
/// <para>
/// The h1/h2 callbacks compose a <em>synchronous</em> listener factory, but binding a QUIC listener is
/// asynchronous (<c>QuicConnectionListener.CreateAsync</c>). Rather than force an async shape onto the
/// registration surface, the h3 members register a deferred factory that materializes the QUIC listener
/// when the <see cref="HttpConnectionListener"/> is constructed — at server start, never at
/// configuration time — blocking once on the async bind at that single point (the same sync-over-async
/// bridge the connection primitives use). This is the resolution of the historical omission the earlier
/// revision of these remarks recorded.
/// </para>
/// <para>
/// <c>System.Net.Quic</c> is available only on Windows, Linux, and macOS, and only when the platform
/// ships a usable QUIC implementation (for example libmsquic). The h3 members are therefore annotated
/// <see cref="SupportedOSPlatformAttribute"/> for those operating systems, and when the running platform
/// lacks QUIC support (<c>QuicListener.IsSupported</c> is <see langword="false"/>) materialization throws
/// <see cref="PlatformNotSupportedException"/> at start, matching <c>QuicConnectionListener.CreateAsync</c>.
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

        /// <summary>
        /// Serves HTTP/3 over a QUIC multiplexed connection listener configured by the supplied
        /// callback, registering a deferred factory that binds the QUIC listener when the server
        /// starts.
        /// </summary>
        /// <param name="configure">
        /// The QUIC listener configuration callback (endpoint, TLS via
        /// <see cref="QuicConnectionListenerOptions.ServerAuthenticationOptions"/>, per-connection
        /// stream limits, and error codes). The caller supplies the server certificate through
        /// <see cref="SslServerAuthenticationOptions.ServerCertificate"/> (or a selection callback) on
        /// those authentication options — the QUIC-native equivalent of the <c>TlsServerOptions</c>
        /// surface the TCP <c>UseHttp1s</c>/<c>UseHttp2s</c> members use. When the callback leaves the
        /// ALPN application-protocol list unset it is defaulted to <see cref="SslApplicationProtocol.Http3"/>
        /// (ALPN <c>h3</c>) and, when the enabled TLS protocols are unset, to
        /// <see cref="SslProtocols.Tls13"/>, so the listener is reachable as HTTP/3.
        /// </param>
        /// <returns>The current options instance.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
        /// <remarks>
        /// <para>
        /// The callback runs at materialization, not at registration: the QUIC listener is bound when
        /// the <see cref="HttpConnectionListener"/> is constructed (server start), never at
        /// configuration time. QUIC's transport security is always-on, so the materialized listener
        /// reports <see cref="ConnectionCapabilities.Security"/> equal to
        /// <see cref="ConnectionSecurity.Tls"/> and every served request carries the <c>https</c> scheme.
        /// </para>
        /// <para>
        /// When the running platform lacks QUIC support (<c>QuicListener.IsSupported</c> is
        /// <see langword="false"/>) materialization throws <see cref="PlatformNotSupportedException"/> at
        /// start. Registered alongside an HTTP/1.1 or HTTP/2 listener plus
        /// <see cref="HttpConnectionListenerOptions.AdvertiseAltService(Action{HttpAltServiceAdvertisementOptions})"/>,
        /// this listener's bound endpoint is what the server advertises in the RFC 7838 <c>Alt-Svc</c>
        /// header on the stream protocols' responses.
        /// </para>
        /// </remarks>
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        public HttpConnectionListenerOptions UseHttp3(Action<QuicConnectionListenerOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            return options.UseHttp3(() =>
            {
                QuicConnectionListenerOptions quicOptions = new();
                configure(quicOptions);
                EnsureApplicationProtocols(quicOptions.ServerAuthenticationOptions, SslApplicationProtocol.Http3);
                EnsureTls13(quicOptions.ServerAuthenticationOptions);

                return MaterializeQuicListener(quicOptions);
            });
        }

        /// <summary>
        /// Serves HTTP/3 over a QUIC multiplexed connection listener whose TLS is supplied through the
        /// same <see cref="TlsServerOptions"/> surface the TCP <c>UseHttp1s</c>/<c>UseHttp2s</c> members
        /// use, registering a deferred factory that binds the QUIC listener when the server starts.
        /// </summary>
        /// <param name="configure">
        /// The QUIC listener configuration callback (endpoint, per-connection stream limits, and error
        /// codes). Any <see cref="QuicConnectionListenerOptions.ServerAuthenticationOptions"/> set here
        /// is superseded by <paramref name="tlsOptions"/>, which is the authoritative TLS surface for
        /// this overload.
        /// </param>
        /// <param name="tlsOptions">
        /// The server TLS options. The caller supplies the server certificate through
        /// <see cref="TlsServerOptions.AuthenticationOptions"/>; certificate sourcing is a Security-area
        /// concern and is intentionally not modeled here. When
        /// <see cref="SslServerAuthenticationOptions.ApplicationProtocols"/> is left unset it is defaulted
        /// to <see cref="SslApplicationProtocol.Http3"/> (ALPN <c>h3</c>, RFC 7301) and, when the enabled
        /// TLS protocols are unset, to <see cref="SslProtocols.Tls13"/> (QUIC requires TLS 1.3, RFC 9001);
        /// a caller-supplied protocol list is preserved unmodified.
        /// </param>
        /// <returns>The current options instance.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> or <paramref name="tlsOptions"/> is <see langword="null"/>.</exception>
        /// <remarks>
        /// This overload mirrors the ergonomics of <see cref="UseHttp2s(Action{TcpConnectionListenerOptions}, TlsServerOptions)"/>:
        /// the endpoint is configured through the callback while the certificate flows through
        /// <paramref name="tlsOptions"/>. The ALPN/TLS defaults are applied to <paramref name="tlsOptions"/>
        /// eagerly (a mutation, so a subsequent read observes them); the QUIC listener itself is bound at
        /// materialization (server start), never at configuration time. Materialization throws
        /// <see cref="PlatformNotSupportedException"/> at start when the platform lacks QUIC support.
        /// </remarks>
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        public HttpConnectionListenerOptions UseHttp3(Action<QuicConnectionListenerOptions> configure, TlsServerOptions tlsOptions)
        {
            ArgumentNullException.ThrowIfNull(configure);
            ArgumentNullException.ThrowIfNull(tlsOptions);

            EnsureApplicationProtocols(tlsOptions, SslApplicationProtocol.Http3);
            EnsureTls13(tlsOptions.AuthenticationOptions);

            return options.UseHttp3(() =>
            {
                QuicConnectionListenerOptions quicOptions = new();
                configure(quicOptions);
                quicOptions.ServerAuthenticationOptions = tlsOptions.AuthenticationOptions;

                return MaterializeQuicListener(quicOptions);
            });
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
        EnsureApplicationProtocols(tlsOptions.AuthenticationOptions, defaultProtocol);
    }

    /// <summary>
    /// Defaults the ALPN application-protocol list on the supplied authentication options when the
    /// caller left it unset, preserving any caller-supplied list. Mutation is intentional so a
    /// subsequent read observes the negotiated protocol.
    /// </summary>
    /// <param name="authentication">The authentication options whose application protocols are defaulted.</param>
    /// <param name="defaultProtocol">The ALPN protocol id to install when none was supplied.</param>
    private static void EnsureApplicationProtocols(SslServerAuthenticationOptions authentication, SslApplicationProtocol defaultProtocol)
    {
        if (authentication.ApplicationProtocols is null || authentication.ApplicationProtocols.Count == 0)
        {
            authentication.ApplicationProtocols = new List<SslApplicationProtocol> { defaultProtocol };
        }
    }

    /// <summary>
    /// Defaults the enabled TLS protocol set to <see cref="SslProtocols.Tls13"/> when the caller left it
    /// unset. QUIC requires TLS 1.3 (RFC 9001); making the default explicit records that requirement and
    /// leaves any deliberate caller choice untouched.
    /// </summary>
    /// <param name="authentication">The authentication options whose enabled TLS protocols are defaulted.</param>
    private static void EnsureTls13(SslServerAuthenticationOptions authentication)
    {
        if (authentication.EnabledSslProtocols == SslProtocols.None)
        {
            authentication.EnabledSslProtocols = SslProtocols.Tls13;
        }
    }

    /// <summary>
    /// Materializes a QUIC connection listener from the supplied options. Binding QUIC is asynchronous
    /// (<c>QuicConnectionListener.CreateAsync</c>) but the transport's multiplexed listener factory seam
    /// is synchronous, so this blocks once — at server start, when the
    /// <see cref="HttpConnectionListener"/> invokes the deferred factory — offloading the bind to the
    /// thread pool so no captured <see cref="System.Threading.SynchronizationContext"/> can deadlock it.
    /// A <see cref="PlatformNotSupportedException"/> from an unsupported platform surfaces here (at start).
    /// </summary>
    /// <param name="quicOptions">The QUIC listener options to bind.</param>
    /// <returns>The bound QUIC connection listener.</returns>
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static QuicConnectionListener MaterializeQuicListener(QuicConnectionListenerOptions quicOptions)
    {
        return Task.Run(() => QuicConnectionListener.CreateAsync(quicOptions).AsTask()).GetAwaiter().GetResult();
    }
}
