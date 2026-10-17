namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>One WHEN/THEN alternative.</summary>
/// <param name="When">The predicate or comparison value.</param>
/// <param name="Then">The selected result expression.</param>
public sealed record GqlCaseAlternative(GqlExpression When, GqlExpression Then);
