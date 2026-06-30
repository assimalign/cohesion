using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Represents an AMQP connection layered on top of a carrier connection produced by a lower-level transport.
/// </summary>
public interface IAmqpConnection : IAsyncDisposable
{
    /// <summary>
    /// Gets the unique identifier of the underlying carrier connection.
    /// </summary>
    ConnectionId Id { get; }

    /// <summary>
    /// Gets the current lifecycle state of the underlying carrier connection.
    /// </summary>
    ConnectionState State { get; }

    /// <summary>
    /// Gets a token that is signaled when the underlying carrier connection is closed or aborted.
    /// </summary>
    CancellationToken ConnectionClosed { get; }

    /// <summary>
    /// Aborts the underlying carrier connection immediately, discarding any in-flight data.
    /// </summary>
    /// <param name="reason">An optional exception describing why the connection was aborted.</param>
    void Abort(Exception? reason = null);

    /// <summary>
    /// Opens the AMQP connection context used to negotiate the protocol header and exchange frames.
    /// </summary>
    /// <returns>The opened AMQP connection context.</returns>
    IAmqpConnectionContext Open();

    /// <summary>
    /// Opens the AMQP connection context used to negotiate the protocol header and exchange frames.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for the open operation.</param>
    /// <returns>The opened AMQP connection context.</returns>
    ValueTask<IAmqpConnectionContext> OpenAsync(CancellationToken cancellationToken = default);
}
