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
        /// Returns a listener that applies the supplied layer to every accepted connection, upgrading each
        /// connection on its own task and holding at most 512 connections at a time.
        /// </summary>
        /// <param name="layer">The layer to apply.</param>
        /// <returns>The layered <see cref="IConnectionListener"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="layer"/> is <see langword="null"/>.</exception>
        /// <remarks>See <see cref="Use(IConnectionLayer, int)"/>.</remarks>
        public IConnectionListener Use(IConnectionLayer layer)
        {
            ArgumentNullException.ThrowIfNull(listener);
            ArgumentNullException.ThrowIfNull(layer);

            return new LayeredConnectionListener(listener, layer, LayeredConnectionListener.DefaultMaxConcurrentUpgrades);
        }

        /// <summary>
        /// Returns a listener that applies the supplied layer to every accepted connection, upgrading each
        /// connection on its own task and holding at most <paramref name="maxConcurrentUpgrades"/>
        /// connections at a time.
        /// </summary>
        /// <param name="layer">The layer to apply.</param>
        /// <param name="maxConcurrentUpgrades">
        /// The most connections the layered listener holds at once. A connection counts from the moment the
        /// layered listener accepts it from <paramref name="listener"/> until
        /// <see cref="IConnectionListener.AcceptAsync(System.Threading.CancellationToken)"/> returns it or
        /// its upgrade fails. At the limit the layered listener stops accepting from
        /// <paramref name="listener"/>, so further peers wait in that listener's own backlog.
        /// <see cref="Use(IConnectionLayer)"/> uses 512.
        /// </param>
        /// <returns>The layered <see cref="IConnectionListener"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="layer"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="maxConcurrentUpgrades"/> is less than 1.
        /// </exception>
        /// <remarks>
        /// <para>
        /// The layered listener accepts connections from <paramref name="listener"/> on its own loop, which
        /// the first <see cref="IConnectionListener.AcceptAsync(System.Threading.CancellationToken)"/>
        /// starts, and runs each connection's
        /// <see cref="IConnectionLayer.UpgradeAsync(IConnection, System.Threading.CancellationToken)"/> on
        /// its own task. <c>AcceptAsync</c> returns connections whose upgrade has completed, in the order
        /// they complete, so a peer that is slow, silent, or rejected holds up only its own connection.
        /// </para>
        /// <para>
        /// An upgrade that throws, including one its layer cancels because a handshake timed out, fails
        /// only its connection: the connection is disposed, the failure is reported through the
        /// <c>Assimalign.Cohesion.Connections</c> event source, and the listener keeps accepting.
        /// <c>AcceptAsync</c> throws only for the listener's own failure, after returning every connection
        /// that was already upgrading. Canceling an <c>AcceptAsync</c> call abandons that wait and nothing
        /// else; disposing the layered listener cancels every upgrade in flight and disposes every
        /// connection it has not returned.
        /// </para>
        /// </remarks>
        public IConnectionListener Use(IConnectionLayer layer, int maxConcurrentUpgrades)
        {
            ArgumentNullException.ThrowIfNull(listener);
            ArgumentNullException.ThrowIfNull(layer);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentUpgrades);

            return new LayeredConnectionListener(listener, layer, maxConcurrentUpgrades);
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
