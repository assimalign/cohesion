using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// A rule of AMQP 1.0 the engine does not hold yet, with the work item that tracks it. A gap names no test: once
/// the engine holds the rule, the rule moves into a compliance map with the tests that pin it.
/// </summary>
/// <param name="Id">The gap's identifier, such as <c>G1</c>.</param>
/// <param name="Statement">What the specification requires, and what the engine does instead.</param>
/// <param name="Reference">The sections of the specification that state the rule, each cited as <c>Part N, section X</c>.</param>
/// <param name="WorkItem">The work item that tracks closing the gap, such as <c>#1931</c>.</param>
internal sealed record AmqpComplianceGap(string Id, string Statement, string Reference, string WorkItem)
{
    /// <summary>
    /// Gets the Parts of the specification the gap's reference cites, or an empty list when the reference does not
    /// cite each section as <c>Part N, section X</c>.
    /// </summary>
    public IReadOnlyList<int> Parts => AmqpComplianceMatrix.ReadParts(Reference) ?? [];
}
