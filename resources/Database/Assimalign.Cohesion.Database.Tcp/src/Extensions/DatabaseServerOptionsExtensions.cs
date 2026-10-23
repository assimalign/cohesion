using System;
using System.Globalization;
using System.Net;

using Assimalign.Cohesion.Connections.Tcp;
namespace Assimalign.Cohesion.Database;

/// <summary>
/// Provides resource-endpoint binding for database servers.
/// </summary>
public static class DatabaseServerOptionsExtensions
{
    extension<TOptions>(TOptions options) where TOptions : DatabaseServerOptions
    {
        /// <summary>
        /// Configures the server to listen on a resolved Cohesion endpoint.
        /// </summary>
        /// <param name="endpoint">The resolved database endpoint.</param>
        /// <returns><paramref name="options"/> for fluent composition.</returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="options"/> or <paramref name="endpoint"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="endpoint"/> is not an endpoint URI.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the endpoint host is not a literal IP address, <c>localhost</c>, or a wildcard.
        /// </exception>
        /// <remarks>
        /// The socket-facing host uses <see cref="Uri.IdnHost"/> by convention, which removes IPv6
        /// brackets and converts internationalized domain names to their ASCII-compatible form.
        /// </remarks>
        public TOptions Listen(Uri endpoint)
        {
            ArgumentNullException.ThrowIfNull(options);
            Uri.ThrowIfNotEndpoint(endpoint);

            IPAddress address = ResolveHost(endpoint.IdnHost);
            options.Listener = TcpConnectionListener.Create(
                tcp => tcp.EndPoint = new IPEndPoint(address, endpoint.Port));
            return options;
        }
    }

    private static IPAddress ResolveHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        if (host is "*" or "+" or "0.0.0.0")
        {
            return IPAddress.Any;
        }

        if (host is "[::]" or "::")
        {
            return IPAddress.IPv6Any;
        }

        if (IPAddress.TryParse(host, out IPAddress? address))
        {
            return address;
        }

        throw new InvalidOperationException(
            string.Format(CultureInfo.CurrentCulture, ErrorMessages.EndpointHostUnsupported, host));
    }
}
