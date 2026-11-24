namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// A rule of AMQP 1.0 connection establishment, from the protocol header to the <c>open</c> frames and the
/// keep-alive traffic after them, that the header-phase corpus exercises.
/// </summary>
/// <param name="Id">The rule's identifier in the corpus, such as <c>H2</c>.</param>
/// <param name="Statement">What the rule requires.</param>
/// <param name="Reference">The sections of the specification that state it.</param>
/// <param name="Forbids"><see langword="true"/> when the rule forbids something, so the corpus holds a case that is refused.</param>
/// <param name="Leniency">
/// <see langword="true"/> when the rule records where the engine accepts what a peer should not send,
/// because the meaning is clear and nothing is at risk; its cases pin that tolerance.
/// </param>
internal sealed record AmqpHeaderPhaseRule(string Id, string Statement, string Reference, bool Forbids, bool Leniency = false);
