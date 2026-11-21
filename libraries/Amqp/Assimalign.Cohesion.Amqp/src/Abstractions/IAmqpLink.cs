using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents an attached AMQP link (AMQP 1.0 Part 2, section 2.6): a unidirectional route from a source
/// to a target, carried on a session.
/// </summary>
/// <remarks>
/// <see cref="IAmqpSender"/> and <see cref="IAmqpReceiver"/> add each role's operations.
/// <see cref="IAsyncDisposable.DisposeAsync"/> closes the link (<see cref="CloseAsync(AmqpError?, CancellationToken)"/>)
/// without an error.
/// </remarks>
public interface IAmqpLink : IAsyncDisposable
{
    /// <summary>
    /// Gets the session the link is attached on.
    /// </summary>
    IAmqpSession Session { get; }

    /// <summary>
    /// Gets the link name, which identifies the link between the two containers.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the local endpoint's role.
    /// </summary>
    AmqpRole Role { get; }

    /// <summary>
    /// Gets the local handle of the link.
    /// </summary>
    uint Handle { get; }

    /// <summary>
    /// Gets the source as established: the local source for a sender, and the peer's source for a
    /// receiver, since the sender is authoritative for it.
    /// </summary>
    AmqpSource? Source { get; }

    /// <summary>
    /// Gets the target as established: the peer's target for a sender, and the local target for a
    /// receiver, since the receiver is authoritative for it.
    /// </summary>
    IAmqpTarget? Target { get; }

    /// <summary>
    /// Gets the sender settlement mode in effect, which the sender decides.
    /// </summary>
    AmqpSenderSettleMode SenderSettleMode { get; }

    /// <summary>
    /// Gets the receiver settlement mode in effect, which the receiver decides.
    /// </summary>
    AmqpReceiverSettleMode ReceiverSettleMode { get; }

    /// <summary>
    /// Gets the largest message, in bytes, the peer's <c>attach</c> said it accepts, or
    /// <see langword="null"/> when it set no limit.
    /// </summary>
    ulong? RemoteMaxMessageSize { get; }

    /// <summary>
    /// Gets the unsettled deliveries the peer's <c>attach</c> listed from an earlier attachment of the link,
    /// keyed by delivery tag, or <see langword="null"/> when it listed none (AMQP 1.0 Part 2, section 2.6.13).
    /// </summary>
    IReadOnlyDictionary<AmqpDeliveryTag, AmqpDeliveryState?>? RemoteUnsettled { get; }

    /// <summary>
    /// Gets a value indicating whether <see cref="RemoteUnsettled"/> holds only part of the peer's unsettled
    /// deliveries.
    /// </summary>
    bool RemoteIncompleteUnsettled { get; }

    /// <summary>
    /// Gets the comparison of the two ends' unsettled maps from the link's <c>attach</c> frames: the deliveries
    /// from earlier attachments the sender resumes, those the receiver considers settled, and those the
    /// receiver has no record of (AMQP 1.0 Part 2, section 2.6.13).
    /// </summary>
    /// <remarks>
    /// The link holds both ends to it: a sender resumes or aborts only a delivery in
    /// <see cref="AmqpUnsettledReconciliation.Resumable"/>, once, and sends no new delivery under such a tag or
    /// while either map was incomplete; a receiver ignores a resume of any other delivery. For a link neither end
    /// resumed, every set is empty.
    /// </remarks>
    AmqpUnsettledReconciliation UnsettledReconciliation { get; }

    /// <summary>
    /// Gets the extension capabilities the peer offered in its <c>attach</c>, or <see langword="null"/>.
    /// </summary>
    IReadOnlyList<AmqpSymbol>? RemoteOfferedCapabilities { get; }

    /// <summary>
    /// Gets the extension capabilities the peer desired in its <c>attach</c>, or <see langword="null"/>.
    /// </summary>
    IReadOnlyList<AmqpSymbol>? RemoteDesiredCapabilities { get; }

    /// <summary>
    /// Gets the link properties the peer sent in its <c>attach</c>, or <see langword="null"/>.
    /// </summary>
    IReadOnlyDictionary<AmqpSymbol, object?>? RemoteProperties { get; }

    /// <summary>
    /// Gets a token that is canceled once the link has detached, by either peer or because its session ended.
    /// </summary>
    CancellationToken LinkDetached { get; }

    /// <summary>
    /// Detaches the link without closing it (<c>closed</c> = <see langword="false"/>), so either peer can
    /// attach it again under the same name and resume its state, and waits for the peer's <c>detach</c>.
    /// </summary>
    /// <param name="error">The error to report to the peer, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">A token that ends the wait for the peer's <c>detach</c>.</param>
    /// <returns>A task that completes when the link has detached.</returns>
    ValueTask DetachAsync(AmqpError? error = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the link (<c>detach</c> with <c>closed</c> = <see langword="true"/>), ending its state at both
    /// endpoints, and waits for the peer's <c>detach</c>.
    /// </summary>
    /// <param name="error">The error to report to the peer, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">A token that ends the wait for the peer's <c>detach</c>.</param>
    /// <returns>A task that completes when the link is closed.</returns>
    ValueTask CloseAsync(AmqpError? error = null, CancellationToken cancellationToken = default);
}
