using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// A rule of AMQP 1.0 that the engine holds, with the tests that show what the rule allows and the tests that show
/// what it refuses. The maps of this shape name tests; the corpus maps hold encoded cases instead.
/// </summary>
/// <param name="Id">The rule's identifier in its map, such as <c>F2</c> or <c>S4</c>.</param>
/// <param name="Statement">What the rule requires.</param>
/// <param name="Reference">The sections of the specification that state it, each cited as <c>Part N, section X</c>.</param>
/// <param name="Tests">The names of the tests that show the engine doing what the rule requires or allows.</param>
/// <param name="Refusals">
/// The names of the tests that show the engine refusing what the rule forbids, whether it would send it or it
/// arrived from the peer; empty when the rule forbids nothing.
/// </param>
/// <param name="Leniency">
/// <see langword="true"/> when the rule records where the engine accepts what a peer should not send, or what
/// the specification leaves open, because the meaning is clear and nothing is at risk.
/// </param>
internal sealed record AmqpComplianceRule(
    string Id,
    string Statement,
    string Reference,
    IReadOnlyList<string> Tests,
    IReadOnlyList<string> Refusals,
    bool Leniency = false)
{
    /// <summary>
    /// Gets whether the rule forbids something, which a test then shows refused.
    /// </summary>
    public bool Forbids => Refusals.Count > 0;
}
