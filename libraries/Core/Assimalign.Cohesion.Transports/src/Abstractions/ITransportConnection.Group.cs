using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

/// <summary>
/// Represents a logical group of transport connections, enabling fan-out
/// communication to multiple members simultaneously.
/// </summary>
/// <remarks>
/// Use cases include pub-sub rooms, connection pools, and broadcast scenarios
/// where a single write should be delivered to all group members.
/// </remarks>
public interface IGroupTransportConnection : ITransportConnection
{
    /// <summary>
    /// The current members of the connection group.
    /// </summary>
    IReadOnlyCollection<ITransportConnection> Members { get; }

    /// <summary>
    /// Adds a connection to the group.
    /// </summary>
    /// <param name="connection">The connection to add.</param>
    void Add(ITransportConnection connection);

    /// <summary>
    /// Asynchronously adds a connection to the group.
    /// </summary>
    /// <param name="connection">The connection to add.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A <see cref="ValueTask"/> representing the add operation.</returns>
    ValueTask AddAsync(ITransportConnection connection, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a connection from the group.
    /// </summary>
    /// <param name="connection">The connection to remove.</param>
    /// <returns><c>true</c> if the connection was removed; otherwise, <c>false</c>.</returns>
    bool Remove(ITransportConnection connection);

    /// <summary>
    /// Asynchronously removes a connection from the group.
    /// </summary>
    /// <param name="connection">The connection to remove.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A <see cref="ValueTask{Boolean}"/> indicating whether the connection was removed.</returns>
    ValueTask<bool> RemoveAsync(ITransportConnection connection, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a broadcast context that fans out writes to all group members.
    /// </summary>
    /// <returns>The opened <see cref="ITransportConnectionContext"/>.</returns>
    ITransportConnectionContext OpenBroadcast();

    /// <summary>
    /// Asynchronously opens a broadcast context that fans out writes to all group members.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A <see cref="ValueTask{ITransportConnectionContext}"/> representing the opened context.</returns>
    ValueTask<ITransportConnectionContext> OpenBroadcastAsync(CancellationToken cancellationToken = default);
}
