using System;

namespace Assimalign.Cohesion.Connections;

using Assimalign.Cohesion.Connections.Internal;

/// <summary>
/// Provides composition extension members for applying <see cref="IConnectionLayer"/> instances
/// to connection listeners and factories.
/// </summary>
/// <remarks>
/// Layers compose innermost-first: <c>listener.Use(a).Use(b)</c> applies <c>a</c> to each accepted
/// connection, then <c>b</c> to the connection <c>a</c> produced.
/// </remarks>
public static class ConnectionLayerExtensions
{
    extension(IConnectionListener listener)
    {
        /// <summary>
        /// Returns a listener that applies the supplied layer to every accepted connection.
        /// </summary>
        /// <param name="layer">The layer to apply.</param>
        /// <returns>The layered <see cref="IConnectionListener"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="layer"/> is <see langword="null"/>.</exception>
        public IConnectionListener Use(IConnectionLayer layer)
        {
            ArgumentNullException.ThrowIfNull(listener);
            ArgumentNullException.ThrowIfNull(layer);

            return new LayeredConnectionListener(listener, layer);
        }
    }

    extension(IConnectionFactory factory)
    {
        /// <summary>
        /// Returns a factory that applies the supplied layer to every established connection.
        /// </summary>
        /// <param name="layer">The layer to apply.</param>
        /// <returns>The layered <see cref="IConnectionFactory"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="layer"/> is <see langword="null"/>.</exception>
        public IConnectionFactory Use(IConnectionLayer layer)
        {
            ArgumentNullException.ThrowIfNull(factory);
            ArgumentNullException.ThrowIfNull(layer);

            return new LayeredConnectionFactory(factory, layer);
        }
    }
}
