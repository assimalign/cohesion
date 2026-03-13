using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

/// <summary>
/// Represents a transport connection capable of multicast communication,
/// allowing data to be sent to a group of endpoints simultaneously.
/// </summary>
/// <remarks>
/// Used for protocols that support multicast groups such as UDP multicast (IGMP).
/// </remarks>
public interface IMulticastTransportConnection : ITransportConnection
{
    /// <summary>
    /// Joins a multicast group identified by the specified address.
    /// </summary>
    /// <param name="groupAddress">The multicast group address to join.</param>
    void JoinGroup(IPAddress groupAddress);

    /// <summary>
    /// Asynchronously joins a multicast group identified by the specified address.
    /// </summary>
    /// <param name="groupAddress">The multicast group address to join.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A <see cref="ValueTask"/> representing the join operation.</returns>
    ValueTask JoinGroupAsync(IPAddress groupAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Leaves a multicast group identified by the specified address.
    /// </summary>
    /// <param name="groupAddress">The multicast group address to leave.</param>
    void LeaveGroup(IPAddress groupAddress);

    /// <summary>
    /// Asynchronously leaves a multicast group identified by the specified address.
    /// </summary>
    /// <param name="groupAddress">The multicast group address to leave.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A <see cref="ValueTask"/> representing the leave operation.</returns>
    ValueTask LeaveGroupAsync(IPAddress groupAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a connection context for sending data to the specified multicast group.
    /// </summary>
    /// <param name="groupAddress">The multicast group address to send to.</param>
    /// <returns>The opened <see cref="ITransportConnectionContext"/>.</returns>
    ITransportConnectionContext OpenGroup(IPAddress groupAddress);

    /// <summary>
    /// Asynchronously opens a connection context for sending data to the specified multicast group.
    /// </summary>
    /// <param name="groupAddress">The multicast group address to send to.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A <see cref="ValueTask{ITransportConnectionContext}"/> representing the opened context.</returns>
    ValueTask<ITransportConnectionContext> OpenGroupAsync(IPAddress groupAddress, CancellationToken cancellationToken = default);
}
