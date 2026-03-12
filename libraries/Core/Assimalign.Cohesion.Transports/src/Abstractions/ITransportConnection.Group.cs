using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

/// <summary>
/// Represents a one-to-many group transport connection.
/// </summary>
public interface IGroupTransportConnection : ITransportConnection
{
    /// <summary>
    /// Joins the connection to a named group.
    /// </summary>
    /// <param name="groupName">The logical group name.</param>
    void JoinGroup(string groupName);

    /// <summary>
    /// Joins the connection to a named group.
    /// </summary>
    /// <param name="groupName">The logical group name.</param>
    /// <param name="cancellationToken">The cancellation token for the asynchronous operation.</param>
    /// <returns>A task that completes when the group join operation has completed.</returns>
    ValueTask JoinGroupAsync(string groupName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Leaves a named group.
    /// </summary>
    /// <param name="groupName">The logical group name.</param>
    void LeaveGroup(string groupName);

    /// <summary>
    /// Leaves a named group.
    /// </summary>
    /// <param name="groupName">The logical group name.</param>
    /// <param name="cancellationToken">The cancellation token for the asynchronous operation.</param>
    /// <returns>A task that completes when the group leave operation has completed.</returns>
    ValueTask LeaveGroupAsync(string groupName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a datagram payload to a specific group.
    /// </summary>
    /// <param name="groupName">The target logical group name.</param>
    /// <param name="datagram">The datagram payload to send.</param>
    /// <param name="cancellationToken">The cancellation token for the asynchronous operation.</param>
    /// <returns>The number of bytes sent to the group members.</returns>
    ValueTask<int> SendToGroupAsync(string groupName, ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a datagram payload to all currently joined groups.
    /// </summary>
    /// <param name="datagram">The datagram payload to send.</param>
    /// <param name="cancellationToken">The cancellation token for the asynchronous operation.</param>
    /// <returns>The number of bytes sent across all group destinations.</returns>
    ValueTask<int> BroadcastAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken = default);
}
