using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Represents a connection layer: a transformation applied to an established connection that
/// returns a new connection (for example, TLS, connection-level compression, proxy-protocol
/// handling, or traffic accounting).
/// </summary>
/// <remarks>
/// <para>
/// A layer is the generic unit of composition in the connection model. Layers are applied once
/// per connection at establishment time (via
/// <c>IConnectionListener.Use(...)</c> / <c>IConnectionFactory.Use(...)</c> or by calling
/// <see cref="UpgradeAsync(IConnection, CancellationToken)"/> directly), so composition adds no
/// per-byte cost: the steady-state data path is whatever the layer's returned connection exposes.
/// </para>
/// <para>
/// A pass-through layer (metrics, logging, a proxy-protocol preamble reader) may return the inner
/// connection unchanged. A transforming layer (TLS) returns a decorated connection whose pipe
/// carries the transformed byte stream.
/// </para>
/// </remarks>
public interface IConnectionLayer
{
    /// <summary>
    /// Describes how this layer changes the capabilities of connections it upgrades.
    /// </summary>
    /// <param name="capabilities">The capabilities of the connection source beneath this layer.</param>
    /// <returns>
    /// The capabilities of connections produced by this layer; return <paramref name="capabilities"/>
    /// unchanged for pass-through layers.
    /// </returns>
    ConnectionCapabilities Describe(ConnectionCapabilities capabilities);

    /// <summary>
    /// Applies the layer to an established connection.
    /// </summary>
    /// <param name="connection">The connection to upgrade.</param>
    /// <param name="cancellationToken">A token to cancel the upgrade (for example, a handshake).</param>
    /// <returns>
    /// The upgraded <see cref="IConnection"/>, or <paramref name="connection"/> itself for
    /// pass-through layers.
    /// </returns>
    ValueTask<IConnection> UpgradeAsync(IConnection connection, CancellationToken cancellationToken = default);
}
