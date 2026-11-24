using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// A defect the AMQP engine once had, with the tests and corpus cases that fail if it returns.
/// </summary>
/// <param name="Id">The entry's identifier in the register, such as <c>D1</c>.</param>
/// <param name="Category">The kind of input or event that exposed the defect.</param>
/// <param name="Defect">What the engine did wrong.</param>
/// <param name="Origin">The work item that found or fixed the defect, such as <c>#727</c>.</param>
/// <param name="Reference">The sections of the specification the defect broke.</param>
/// <param name="Map">The compliance map whose rule the defect broke, or <see cref="AmqpComplianceMap.None"/>.</param>
/// <param name="Rule">
/// The rule's identifier in <paramref name="Map"/>, or the type's name for <see cref="AmqpComplianceMap.TypeSystem"/>;
/// <see langword="null"/> when no map holds the rule.
/// </param>
/// <param name="Tests">The names of the tests that fail when the defect returns.</param>
/// <param name="Cases">
/// The names of the cases in <paramref name="Map"/>'s corpus, from the rules the defect broke, that fail when it
/// returns. A returning defect can fail other cases too, such as every case that pipelines its frames.
/// </param>
internal sealed record AmqpRegression(
    string Id,
    AmqpRegressionCategory Category,
    string Defect,
    string Origin,
    string Reference,
    AmqpComplianceMap Map,
    string? Rule,
    IReadOnlyList<string> Tests,
    IReadOnlyList<string> Cases);
