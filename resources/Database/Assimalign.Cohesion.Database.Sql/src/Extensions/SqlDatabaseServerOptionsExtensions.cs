using System;
using System.Net;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// Provides resource-endpoint binding for SQL database servers.
/// </summary>
public static class SqlDatabaseServerOptionsExtensions
{
    extension(SqlDatabaseServerOptions options)
    {
        /// <summary>
        /// Configures the server to listen on a resolved Cohesion endpoint.
        /// </summary>
        /// <param name="endpoint">The resolved database endpoint.</param>
        /// <returns><paramref name="options"/> for fluent composition.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="endpoint"/> has no host or a non-positive port.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the endpoint host is not a literal IP address, <c>localhost</c>, or a wildcard.
        /// </exception>
        public SqlDatabaseServerOptions Listen(EndpointAddress endpoint)
        {
            return options.Listen(endpoint.Url);
        }

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
        public SqlDatabaseServerOptions Listen(Uri endpoint)
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
            $"The database endpoint host '{host}' is not a literal IP address, 'localhost', or a wildcard. DNS resolution is not performed at bind time.");
    }
}
