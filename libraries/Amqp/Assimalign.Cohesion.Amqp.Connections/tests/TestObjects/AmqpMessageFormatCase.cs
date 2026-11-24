using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// An encoded message in the compliance corpus and the outcome AMQP 1.0 requires for it.
/// </summary>
/// <param name="Name">A short description of the case, unique in the corpus.</param>
/// <param name="Rule">The identifier of the rule the case exercises.</param>
/// <param name="Hex">The encoded message, in hexadecimal.</param>
/// <param name="Refusal">The message decoding must fail with, or <see langword="null"/> for a message it must accept.</param>
/// <param name="Condition">The error condition of the refusal, or <see langword="null"/> for <c>amqp:decode-error</c>.</param>
internal sealed record AmqpMessageFormatCase(string Name, string Rule, string Hex, string? Refusal, AmqpSymbol? Condition = null);
