namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A projected value expression with an optional column alias.</summary>
/// <param name="Expression">The expression evaluated for each input binding.</param>
/// <param name="Alias">The optional result column name.</param>
public sealed record GqlProjection(GqlExpression Expression, string? Alias = null);
