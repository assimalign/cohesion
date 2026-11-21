using System;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Provides access to the SASL facts registered on an AMQP connection.
/// </summary>
public static class AmqpSaslConnectionFeatureExtensions
{
    extension(IConnection connection)
    {
        /// <summary>
        /// Gets the facts the connection's SASL exchange established, or <see langword="null"/> when the
        /// connection skipped SASL.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="connection"/> is <see langword="null"/>.</exception>
        public IAmqpSaslConnectionInfo? AmqpSasl
        {
            get
            {
                ArgumentNullException.ThrowIfNull(connection);
                return connection.Features.Get<IAmqpSaslConnectionInfo>();
            }
        }
    }
}
