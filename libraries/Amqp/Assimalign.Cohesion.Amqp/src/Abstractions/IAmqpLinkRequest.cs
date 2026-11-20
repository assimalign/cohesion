using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents a link the remote peer is attaching, waiting for the application to accept or refuse it
/// (AMQP 1.0 Part 2, section 2.6.3).
/// </summary>
/// <remarks>
/// A peer that attaches as a receiver asks the local endpoint to send, so <see cref="Role"/> is
/// <see cref="AmqpRole.Sender"/> and the request is accepted with
/// <see cref="AcceptSenderAsync(AmqpSenderOptions?, CancellationToken)"/>; a peer that attaches as a
/// sender is accepted with <see cref="AcceptReceiverAsync(AmqpReceiverOptions?, CancellationToken)"/>.
/// Refusing sends an <c>attach</c> without the terminus the local endpoint owns, followed by a
/// <c>detach</c> carrying the error.
/// </remarks>
public interface IAmqpLinkRequest
{
    /// <summary>
    /// Gets the session the link is attaching on.
    /// </summary>
    IAmqpSession Session { get; }

    /// <summary>
    /// Gets the link name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the role the local endpoint takes when it accepts: the opposite of the peer's role.
    /// </summary>
    AmqpRole Role { get; }

    /// <summary>
    /// Gets the source the peer's <c>attach</c> carried, or <see langword="null"/>.
    /// </summary>
    AmqpSource? Source { get; }

    /// <summary>
    /// Gets the target the peer's <c>attach</c> carried, or <see langword="null"/>.
    /// </summary>
    IAmqpTarget? Target { get; }

    /// <summary>
    /// Gets the sender settlement mode the peer's <c>attach</c> carried.
    /// </summary>
    AmqpSenderSettleMode SenderSettleMode { get; }

    /// <summary>
    /// Gets the receiver settlement mode the peer's <c>attach</c> carried.
    /// </summary>
    AmqpReceiverSettleMode ReceiverSettleMode { get; }

    /// <summary>
    /// Gets the largest message, in bytes, the peer accepts, or <see langword="null"/> when it set no limit.
    /// </summary>
    ulong? MaxMessageSize { get; }

    /// <summary>
    /// Gets the unsettled deliveries the peer listed from an earlier attachment of the link, keyed by
    /// delivery tag, or <see langword="null"/> when it listed none (AMQP 1.0 Part 2, section 2.6.13).
    /// </summary>
    IReadOnlyDictionary<AmqpDeliveryTag, AmqpDeliveryState?>? Unsettled { get; }

    /// <summary>
    /// Gets a value indicating whether <see cref="Unsettled"/> holds only part of the peer's unsettled
    /// deliveries.
    /// </summary>
    bool IncompleteUnsettled { get; }

    /// <summary>
    /// Gets the extension capabilities the peer offered, or <see langword="null"/>.
    /// </summary>
    IReadOnlyList<AmqpSymbol>? OfferedCapabilities { get; }

    /// <summary>
    /// Gets the extension capabilities the peer desired, or <see langword="null"/>.
    /// </summary>
    IReadOnlyList<AmqpSymbol>? DesiredCapabilities { get; }

    /// <summary>
    /// Gets the link properties the peer sent, or <see langword="null"/>.
    /// </summary>
    IReadOnlyDictionary<AmqpSymbol, object?>? Properties { get; }

    /// <summary>
    /// Accepts the request as the sending endpoint and answers the peer's <c>attach</c>.
    /// </summary>
    /// <param name="options">
    /// The local answer; <see langword="null"/> echoes the peer's proposal. A set
    /// <see cref="AmqpSenderOptions.Source"/> replaces the proposed source, such as to assign a dynamic
    /// node's address. The name and target always come from the request.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The attached sender.</returns>
    /// <exception cref="System.InvalidOperationException">Thrown when <see cref="Role"/> is not <see cref="AmqpRole.Sender"/>, or the request was already answered.</exception>
    /// <exception cref="System.ArgumentException">Thrown when the options make an answering <c>attach</c> AMQP 1.0 forbids, or carry a value the type system cannot encode. The request stays unanswered.</exception>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.SessionEnded"/> when the session has ended.</exception>
    ValueTask<IAmqpSender> AcceptSenderAsync(AmqpSenderOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepts the request as the receiving endpoint, answers the peer's <c>attach</c>, and grants the
    /// link its prefetch credit.
    /// </summary>
    /// <param name="options">
    /// The local answer; <see langword="null"/> echoes the peer's proposal. A set
    /// <see cref="AmqpReceiverOptions.Target"/> replaces the proposed target, such as to assign a dynamic
    /// node's address. The name and source always come from the request.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The attached receiver.</returns>
    /// <exception cref="System.InvalidOperationException">Thrown when <see cref="Role"/> is not <see cref="AmqpRole.Receiver"/>, or the request was already answered.</exception>
    /// <exception cref="System.ArgumentException">Thrown when the options make an answering <c>attach</c> AMQP 1.0 forbids, or carry a value the type system cannot encode. The request stays unanswered.</exception>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.SessionEnded"/> when the session has ended.</exception>
    ValueTask<IAmqpReceiver> AcceptReceiverAsync(AmqpReceiverOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refuses the request: answers the peer's <c>attach</c> without the terminus the local endpoint owns,
    /// then closes the link with the error.
    /// </summary>
    /// <param name="error">The reason for the refusal, such as <see cref="AmqpErrorCondition.NotFound"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when the refusal is queued.</returns>
    /// <exception cref="System.ArgumentNullException">Thrown when <paramref name="error"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.InvalidOperationException">Thrown when the request was already answered.</exception>
    ValueTask RejectAsync(AmqpError error, CancellationToken cancellationToken = default);
}
