using System;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Provides access to the StreamAbort connection feature.
/// </summary>
public static partial class ConnectionStreamAbortExtensions
{
    extension(IConnection connection)
    {
        /// <summary>
        /// Gets the registered StreamAbort feature, or <see langword="null"/> when absent.
        /// </summary>
        /// <exception cref="ArgumentNullException">The connection is <see langword="null"/>.</exception>
        public IMultiplexedStreamAbort? DirectionalAbort
        {
            get
            {
                ArgumentNullException.ThrowIfNull(connection);
                return connection.Features.Get<IMultiplexedStreamAbort>();
            }
        }
    }
}
