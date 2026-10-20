namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>One ORDER BY key, its direction and its null placement.</summary>
/// <param name="Expression">The sort key, including RETURN aliases.</param>
/// <param name="Direction">The direction; ascending by default.</param>
/// <param name="NullOrdering">The explicit placement or the Cypher direction-dependent default.</param>
public sealed record GqlSortItem(GqlExpression Expression,
    GqlSortDirection Direction = GqlSortDirection.Ascending,
    GqlNullOrdering NullOrdering = GqlNullOrdering.Default);
