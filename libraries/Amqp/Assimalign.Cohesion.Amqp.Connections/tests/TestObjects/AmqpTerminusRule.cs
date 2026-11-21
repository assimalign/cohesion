namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// A rule of the AMQP 1.0 source and target definitions that the terminus corpus exercises.
/// </summary>
/// <param name="Id">The rule's identifier in the corpus, such as <c>T2</c>.</param>
/// <param name="Statement">What the rule requires.</param>
/// <param name="Reference">The sections of the specification that state it.</param>
/// <param name="Forbids"><see langword="true"/> when the rule forbids something, so the corpus holds a case it refuses.</param>
/// <param name="Values">
/// <see langword="true"/> when the rule governs a value inside a terminus, which the encoder refuses as well;
/// <see langword="false"/> when only the attach rules or the decoder see it.
/// </param>
internal sealed record AmqpTerminusRule(string Id, string Statement, string Reference, bool Forbids, bool Values);
