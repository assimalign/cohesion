using System;

using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web.Hosting.Internal;

namespace Assimalign.Cohesion.Web.Hosting;

/// <summary>
/// Provides hosting convenience extension members for <see cref="HttpConnectionListenerOptions"/>
/// and <see cref="WebApplicationServerBuilder"/>.
/// </summary>
/// <remarks>
/// HTTP/3 has no callback-based overload here because QUIC listeners bind asynchronously
/// (<c>QuicConnectionListener.CreateAsync</c>); create the listener first and register it through
/// <see cref="HttpConnectionListenerOptions.UseHttp3(Assimalign.Cohesion.Connections.IMultiplexedConnectionListener)"/>.
/// </remarks>
public static class WebHostingExtensions
{
    extension(WebApplicationServerBuilder builder)
    {
        /// <summary>
        /// Configures the web server's listener endpoints and server limits from a Cohesion
        /// <see cref="IConfiguration"/> section at builder time. Binding is explicit and AOT-safe
        /// (no reflection); see <see cref="HttpServerLimits"/> for the limit surface and the
        /// expected section shape (Kestrel <c>appsettings</c> parity).
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
    }
}
