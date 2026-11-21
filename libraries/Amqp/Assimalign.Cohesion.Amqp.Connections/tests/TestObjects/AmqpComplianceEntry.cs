using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// One rule of a compliance map in the shape every map shares: what it requires, where the specification states
/// it, and the tests or corpus cases that pin it.
/// </summary>
/// <param name="Map">The map that holds the rule.</param>
/// <param name="Id">
/// The rule's identifier in its map, such as <c>C2</c>, or the type's name for <see cref="AmqpComplianceMap.TypeSystem"/>.
/// </param>
/// <param name="Statement">What the rule requires.</param>
/// <param name="Reference">The sections of the specification that state it, each cited as <c>Part N, section X</c>.</param>
/// <param name="Forbids">Whether the rule forbids something, which its refused evidence then shows refused.</param>
/// <param name="Leniency">
/// Whether the rule records where the engine accepts what a peer should not send, or what the specification
/// leaves open.
/// </param>
/// <param name="Accepted">The tests or corpus cases that show the engine doing what the rule requires or allows.</param>
/// <param name="Refused">The tests or corpus cases that show the engine refusing what the rule forbids.</param>
internal sealed record AmqpComplianceEntry(
    AmqpComplianceMap Map,
    string Id,
    string Statement,
    string Reference,
    bool Forbids,
    bool Leniency,
    IReadOnlyList<string> Accepted,
    IReadOnlyList<string> Refused)
{
    /// <summary>
    /// Gets the Parts of the specification the rule's reference cites, in the order it cites them, or an empty
    /// list when the reference does not cite each section as <c>Part N, section X</c>.
    /// </summary>
    public IReadOnlyList<int> Parts => AmqpComplianceMatrix.ReadParts(Reference) ?? [];

    /// <summary>
    /// Gets whether the map names corpus cases as the rule's evidence rather than tests.
    /// </summary>
    public bool NamesCases => AmqpComplianceMatrix.NamesCases(Map);
}
