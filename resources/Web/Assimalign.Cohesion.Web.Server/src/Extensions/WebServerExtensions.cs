using System;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Http.Connections;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// Convenience helpers that configure an HTTP listener over the TCP connection driver.
/// </summary>
/// <remarks>
/// HTTP/3 over QUIC is intentionally omitted here: the QUIC listener is created
/// asynchronously (<c>QuicConnectionListener.CreateAsync</c>), so its convenience
/// surface is deferred to the Web hosting/application-model work rather than bridged
/// synchronously. Compose HTTP/3 directly via
/// <see cref="HttpConnectionListenerOptions.UseHttp3(IMultiplexedConnectionListener)"/>.
/// </remarks>
public static class WebServerExtensions
{
    extension(HttpConnectionListenerOptions options)
    {
        /// <summary>
        /// Configures an HTTP/1.1 listener over a TCP connection listener.
        /// </summary>
        /// <param name="configure">Configures the TCP connection listener.</param>
        /// <returns>The same options instance.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
        public HttpConnectionListenerOptions UseHttp1(Action<TcpConnectionListenerOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            return options.UseHttp1(TcpConnectionListener.Create(configure));
        }

        /// <summary>
        /// Configures a prior-knowledge HTTP/2 listener over a TCP connection listener.
        /// </summary>
        /// <param name="configure">Configures the TCP connection listener.</param>
        /// <returns>The same options instance.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
        public HttpConnectionListenerOptions UseHttp2(Action<TcpConnectionListenerOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            return options.UseHttp2(TcpConnectionListener.Create(configure));
        }
    }
}
