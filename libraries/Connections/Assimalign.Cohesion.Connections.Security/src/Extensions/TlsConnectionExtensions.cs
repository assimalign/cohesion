using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.Security.Internal;

namespace Assimalign.Cohesion.Connections.Security;

/// <summary>
/// Provides TLS extension members for connections, listeners, and factories.
/// </summary>
public static class TlsConnectionExtensions
{
    extension(IConnection connection)
    {
        /// <summary>
        /// Upgrades the connection to a server-side TLS session, performing the TLS handshake over the
        /// connection's duplex pipe and returning a new connection whose pipe is encrypted.
        /// </summary>
        /// <param name="options">The server TLS options.</param>
        /// <param name="cancellationToken">A token to cancel the handshake.</param>
        /// <returns>A new secured <see cref="IConnection"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
        public ValueTask<IConnection> UpgradeToTlsAsync(TlsServerOptions options, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(options);

            return TlsConnection.AuthenticateAsServerAsync(connection, options, cancellationToken);
        }

        /// <summary>
        /// Upgrades the connection to a client-side TLS session, performing the TLS handshake over the
        /// connection's duplex pipe and returning a new connection whose pipe is encrypted.
        /// </summary>
        /// <param name="options">The client TLS options.</param>
        /// <param name="cancellationToken">A token to cancel the handshake.</param>
        /// <returns>A new secured <see cref="IConnection"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
        public ValueTask<IConnection> UpgradeToTlsAsync(TlsClientOptions options, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(options);

            return TlsConnection.AuthenticateAsClientAsync(connection, options, cancellationToken);
        }
    }

    extension(IConnectionListener listener)
    {
        /// <summary>
        /// Returns a listener whose accepted connections are secured with server-side TLS.
        /// </summary>
        /// <param name="options">The server TLS options.</param>
        /// <returns>The TLS-layered <see cref="IConnectionListener"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
        public IConnectionListener UseTls(TlsServerOptions options)
        {
            ArgumentNullException.ThrowIfNull(listener);
            ArgumentNullException.ThrowIfNull(options);

            return listener.Use(new TlsConnectionLayer(options));
        }
    }

    extension(IConnectionFactory factory)
    {
        /// <summary>
        /// Returns a factory whose established connections are secured with client-side TLS.
        /// </summary>
        /// <param name="options">The client TLS options.</param>
        /// <returns>The TLS-layered <see cref="IConnectionFactory"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
        public IConnectionFactory UseTls(TlsClientOptions options)
        {
            ArgumentNullException.ThrowIfNull(factory);
            ArgumentNullException.ThrowIfNull(options);

            return factory.Use(new TlsConnectionLayer(options));
        }
    }
}
