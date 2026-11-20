using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// A rule of AMQP 1.0 for transferring, splitting, aborting or resuming a delivery that the engine holds, with the
/// tests that pin it.
/// </summary>
/// <param name="Id">The rule's identifier in its map, such as <c>F2</c> or <c>R4</c>.</param>
/// <param name="Statement">What the rule requires.</param>
/// <param name="Reference">The sections of the specification that state it.</param>
/// <param name="Forbids"><see langword="true"/> when the rule forbids something, so a test shows it refused.</param>
/// <param name="Tests">The names of the tests that pin the rule.</param>
/// <param name="Leniency">
/// <see langword="true"/> when the rule records where the engine accepts what a peer should not send, or what
/// the specification leaves open, because the meaning is clear and nothing is at risk.
/// </param>
internal sealed record AmqpTransferRule(string Id, string Statement, string Reference, bool Forbids, IReadOnlyList<string> Tests, bool Leniency = false);
