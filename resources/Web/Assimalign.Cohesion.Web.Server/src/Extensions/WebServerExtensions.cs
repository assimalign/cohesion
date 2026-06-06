using Assimalign.Cohesion.Http.Transports;
using Assimalign.Cohesion.Transports;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text;

namespace Assimalign.Cohesion.Web;

using Server.Internal;

public static class WebServerExtensions
{
    extension(HttpConnectionListenerOptions options)
    {
        /// <summary>
        /// Adds a TCP transport configured for HTTP/1.1.
        /// </summary>
        /// <param name="configure">The transport configuration callback.</param>
        /// <returns>The current options instance.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
        public HttpConnectionListenerOptions UseHttp1(Action<TcpServerTransportOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            return options.UseHttp(() =>
            {
                return new DefaultHttpTransport(
                    TcpServerTransport.Create(configure),
                    HttpProtocol.Http11);
            });
        }

        /// <summary>
        /// Adds a TCP transport configured for prior-knowledge HTTP/2.
        /// </summary>
        /// <param name="configure">The transport configuration callback.</param>
        /// <returns>The current options instance.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
        public HttpConnectionListenerOptions UseHttp2(Action<TcpServerTransportOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            return options.UseHttp(() =>
            {
                return new DefaultHttpTransport(
                    TcpServerTransport.Create(configure),
                    HttpProtocol.Http20);
            });
        }

        /// <summary>
        /// Adds a QUIC transport configured for HTTP/3.
        /// </summary>
        /// <param name="configure">The transport configuration callback.</param>
        /// <returns>The current options instance.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        [SupportedOSPlatform("osx")]
        public HttpConnectionListenerOptions UseHttp3(Action<QuicServerTransportOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            return options.UseHttp(() =>
            {
                return new DefaultHttpTransport(QuicServerTransport.Create(configure));
            });
        }
    }
}
