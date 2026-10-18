namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>One WHEN/THEN alternative.</summary>
/// <param name="When">The predicate or comparison value.</param>
/// <param name="Then">The selected result expression.</param>
public sealed record GqlCaseAlternative(GqlExpression When, GqlExpression Then)
{
    /// <summary>Gets the simple-case comparison: =, &lt;&gt;, !=, &lt;, &lt;=, &gt;, &gt;=, IS NULL or IS NOT NULL.
    /// Searched CASE uses its Boolean When expression instead.</summary>
    public string Comparison { get; init; } = "=";
}
