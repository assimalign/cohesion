using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// An encoded <c>attach</c> in the terminus corpus and the outcome AMQP 1.0 requires for it.
/// </summary>
/// <param name="Name">A short description of the case, unique in the corpus.</param>
/// <param name="Rule">The identifier of the rule the case exercises.</param>
/// <param name="Hex">The encoded <c>attach</c> performative, in hexadecimal.</param>
/// <param name="Opening">
/// <see langword="true"/> when the attach opens the link; <see langword="false"/> when it answers the
/// other endpoint's, so it carries back the terminus that endpoint owns.
/// </param>
/// <param name="Refusal">The description the attach is refused with, or <see langword="null"/> for one it must accept.</param>
/// <param name="Decoding">
/// <see langword="true"/> when decoding refuses the attach, which closes the connection because the model
/// cannot hold the value; <see langword="false"/> when the attach decodes and only its link is refused.
/// </param>
/// <param name="Condition">The condition of a decoding refusal, or <see langword="null"/> for <c>amqp:invalid-field</c>.</param>
internal sealed record AmqpTerminusCase(
    string Name,
    string Rule,
    string Hex,
    bool Opening,
    string? Refusal,
    bool Decoding = false,
    AmqpSymbol? Condition = null);
