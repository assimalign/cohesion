namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// A rule of the AMQP 1.0 message format that the compliance corpus exercises.
/// </summary>
/// <param name="Id">The rule's identifier in the corpus, such as <c>MF1</c>.</param>
/// <param name="Statement">What the rule requires.</param>
/// <param name="Reference">The section of the specification that states it.</param>
/// <param name="Layout">
/// <see langword="true"/> when the rule governs the order and multiplicity of sections, which finding the
/// bare message also enforces; <see langword="false"/> when it governs the values inside a section.
/// </param>
internal sealed record AmqpMessageFormatRule(string Id, string Statement, string Reference, bool Layout);
