using System;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Http.Transports;

namespace Assimalign.Cohesion.Web.Hosting;

/// <summary>
/// Provides hosting convenience extension members for <see cref="HttpConnectionListenerOptions"/>.
/// </summary>
/// <remarks>
/// HTTP/3 has no callback-based overload here because QUIC listeners bind asynchronously
/// (<c>QuicConnectionListener.CreateAsync</c>); create the listener first and register it through
/// <see cref="HttpConnectionListenerOptions.UseHttp3(Assimalign.Cohesion.Connections.IMultiplexedConnectionListener)"/>.
/// </remarks>
public static class WebHostingExtensions
{
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
