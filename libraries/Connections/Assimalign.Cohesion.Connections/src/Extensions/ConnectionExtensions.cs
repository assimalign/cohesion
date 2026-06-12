using System;
using System.IO;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Provides convenience extension members for <see cref="IConnection"/>.
/// </summary>
public static class ConnectionExtensions
{
    extension(IConnection connection)
    {
        /// <summary>
        /// Lazily adapts the connection's duplex pipe as a bidirectional <see cref="Stream"/>.
        /// </summary>
        /// <returns>A <see cref="Stream"/> reading from the connection's input and writing to its output.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="connection"/> is <see langword="null"/>.</exception>
        public Stream AsStream()
        {
            ArgumentNullException.ThrowIfNull(connection);

            return new DuplexPipeStream(connection);
        }
    }
}
