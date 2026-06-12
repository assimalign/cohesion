using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Transports;

/// <summary>
/// Represents an AMQP connection layered on top of a carrier connection produced by a lower-level transport.
/// </summary>
/// <remarks>
/// The connection wraps either a single-stream carrier (the carrier <see cref="IConnection"/> itself)
/// or a multiplexed carrier (an <see cref="IMultiplexedConnection"/> whose AMQP stream is acquired
/// when the connection context is opened). Use <see cref="OpenAsync(CancellationToken)"/> to obtain
/// the <see cref="AmqpConnectionContext"/> used to negotiate protocol headers and exchange frames.
/// </remarks>
public abstract class AmqpConnection : IAmqpConnection
{
    /// <inheritdoc cref="IAmqpConnection.Id" />
    public abstract ConnectionId Id { get; }

    /// <inheritdoc cref="IAmqpConnection.State" />
    public abstract ConnectionState State { get; }

    /// <inheritdoc cref="IAmqpConnection.ConnectionClosed" />
    public abstract CancellationToken ConnectionClosed { get; }

    /// <inheritdoc cref="IAmqpConnection.Abort(Exception?)" />
    public abstract void Abort(Exception? reason = null);

    /// <summary>
    /// Opens the AMQP connection context used to negotiate the protocol header and exchange frames.
    /// </summary>
    /// <returns>The opened AMQP connection context.</returns>
    public abstract AmqpConnectionContext Open();

    /// <summary>
    /// Opens the AMQP connection context used to negotiate the protocol header and exchange frames.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for the open operation.</param>
    /// <returns>The opened AMQP connection context.</returns>
    public abstract ValueTask<AmqpConnectionContext> OpenAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();

    IAmqpConnectionContext IAmqpConnection.Open()
    {
        return Open();
    }

    async ValueTask<IAmqpConnectionContext> IAmqpConnection.OpenAsync(CancellationToken cancellationToken)
    {
        return await OpenAsync(cancellationToken).ConfigureAwait(false);
    }
}
