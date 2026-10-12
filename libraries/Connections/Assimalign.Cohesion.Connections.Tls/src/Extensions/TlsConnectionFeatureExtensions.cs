using System;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Provides access to the TLS handshake facts registered on a connection.
/// </summary>
public static partial class TlsConnectionFeatureExtensions
{
    extension(IConnection connection)
    {
        /// <summary>
        /// Gets the TLS handshake facts, or <see langword="null"/> when none are registered.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
        public ITlsConnectionInfo? Tls
        {
            get
            {
                ArgumentNullException.ThrowIfNull(connection);
                return connection.Features.Get<ITlsConnectionInfo>();
            }
        }
    }

    extension(IMultiplexedConnection connection)
    {
        /// <summary>
        /// Gets the TLS handshake facts, or <see langword="null"/> when none are registered.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
        public ITlsConnectionInfo? Tls
        {
            get
            {
                ArgumentNullException.ThrowIfNull(connection);
                return connection.Features.Get<ITlsConnectionInfo>();
            }
        }
    }
}
