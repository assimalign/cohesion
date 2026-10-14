using System;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Provides access to the StreamLimits connection feature.
/// </summary>
public static partial class ConnectionStreamLimitsExtensions
{
    extension(IMultiplexedConnection connection)
    {
        /// <summary>
        /// Gets the registered StreamLimits feature, or <see langword="null"/> when absent.
        /// </summary>
        /// <exception cref="ArgumentNullException">The connection is <see langword="null"/>.</exception>
        public IMultiplexedConnectionStreamLimits? StreamLimits
        {
            get
            {
                ArgumentNullException.ThrowIfNull(connection);
                return connection.Features.Get<IMultiplexedConnectionStreamLimits>();
            }
        }
    }
}
