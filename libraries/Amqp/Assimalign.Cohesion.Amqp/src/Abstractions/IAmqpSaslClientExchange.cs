using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents the client side of one SASL authentication exchange (AMQP 1.0 Part 5, section 5.3.2).
/// </summary>
/// <remarks>
/// The factory sends the initial response in its <c>sasl-init</c>, answers each challenge with
/// <see cref="RespondAsync(ReadOnlyMemory{byte}, CancellationToken)"/>, and, when the server accepts the
/// client, passes the outcome's additional data to <see cref="CompleteAsync(ReadOnlyMemory{byte}, CancellationToken)"/>.
/// </remarks>
public interface IAmqpSaslClientExchange
{
    /// <summary>
    /// Returns the initial response the client's <c>sasl-init</c> carries.
    /// </summary>
    /// <param name="cancellationToken">A token the factory cancels when the handshake times out.</param>
    /// <returns>The initial response, or <see langword="null"/> to send none.</returns>
    ValueTask<ReadOnlyMemory<byte>?> GetInitialResponseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Answers a challenge from the server.
    /// </summary>
    /// <param name="challenge">The challenge data.</param>
    /// <param name="cancellationToken">A token the factory cancels when the handshake times out.</param>
    /// <returns>The response data.</returns>
    ValueTask<ReadOnlyMemory<byte>> RespondAsync(ReadOnlyMemory<byte> challenge, CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes the exchange after the server accepted the client, such as to verify the server's
    /// signature in the outcome. Throwing refuses the server, and the connection fails.
    /// </summary>
    /// <param name="additionalData">The additional data of the server's <c>sasl-outcome</c>; empty when it sent none.</param>
    /// <param name="cancellationToken">A token the factory cancels when the handshake times out.</param>
    /// <returns>A task that completes when the client accepts the outcome.</returns>
    ValueTask CompleteAsync(ReadOnlyMemory<byte> additionalData, CancellationToken cancellationToken = default);
}
