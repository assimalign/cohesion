using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents the receiving endpoint of an attached link: it grants credit and receives deliveries
/// (AMQP 1.0 Part 2, section 2.6.7).
/// </summary>
/// <remarks>
/// With a positive <see cref="AmqpReceiverOptions.Prefetch"/>, the receiver keeps that many deliveries
/// granted or buffered, topping credit up as the application takes deliveries. With a prefetch of zero,
/// the application grants credit itself with <see cref="AddCredit(uint)"/>.
/// </remarks>
public interface IAmqpReceiver : IAmqpLink
{
    /// <summary>
    /// Gets the link credit the receiver has granted and the sender has not used.
    /// </summary>
    uint Credit { get; }

    /// <summary>
    /// Returns the next complete delivery, waiting for one when none is buffered.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    /// <returns>The next delivery.</returns>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.LinkDetached"/> once the link has detached and no delivery is buffered.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the wait is canceled.</exception>
    ValueTask<IAmqpDelivery> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the next complete delivery when one is buffered, without waiting.
    /// </summary>
    /// <param name="delivery">The next delivery, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a delivery was buffered; otherwise <see langword="false"/>.</returns>
    bool TryReceive([NotNullWhen(true)] out IAmqpDelivery? delivery);

    /// <summary>
    /// Grants the sender more link credit.
    /// </summary>
    /// <param name="credit">The number of additional deliveries the sender may send.</param>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.LinkDetached"/> when the link has detached.</exception>
    void AddCredit(uint credit);

    /// <summary>
    /// Asks the sender to drain: to send what it has within the granted credit and give back the rest,
    /// and waits until the receiver's credit is zero.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    /// <returns>A task that completes when the link has no credit left.</returns>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.LinkDetached"/> when the link detaches first.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the wait is canceled.</exception>
    ValueTask DrainAsync(CancellationToken cancellationToken = default);
}
