using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents the server side of one SASL authentication exchange (AMQP 1.0 Part 5, section 5.3.2).
/// </summary>
/// <remarks>
/// The listener calls <see cref="EvaluateAsync(ReadOnlyMemory{byte}?, CancellationToken)"/> with the client's
/// initial response, then with each response to a challenge, until a step completes the exchange.
/// </remarks>
public interface IAmqpSaslServerExchange
{
    /// <summary>
    /// Evaluates the client's initial response, from its <c>sasl-init</c>, or its response to the last challenge.
    /// </summary>
    /// <param name="response">
    /// The response, or <see langword="null"/> when the client's <c>sasl-init</c> carried no initial response.
    /// </param>
    /// <param name="cancellationToken">A token the listener cancels when the handshake times out or the connection ends.</param>
    /// <returns>
    /// The next step: a challenge for the client, or the outcome that completes the exchange. A step that
    /// is not <see cref="AmqpSaslCode.Ok"/> refuses the connection.
    /// </returns>
    ValueTask<AmqpSaslServerStep> EvaluateAsync(ReadOnlyMemory<byte>? response, CancellationToken cancellationToken = default);
}
