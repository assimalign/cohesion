namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>One ORDER BY key, its direction and its null placement.</summary>
/// <param name="Expression">The sort key, including RETURN aliases.</param>
/// <param name="Direction">The direction; ascending by default.</param>
/// <param name="NullOrdering">The explicit placement or the Cypher direction-dependent default.</param>
public sealed record GqlSortItem(GqlExpression Expression,
    GqlSortDirection Direction = GqlSortDirection.Ascending,
    GqlNullOrdering NullOrdering = GqlNullOrdering.Default);

/// <summary>The direction of an ORDER BY key.</summary>
public enum GqlSortDirection
{
    /// <summary>Smaller values precede larger values.</summary>
    Ascending,
    /// <summary>Larger values precede smaller values.</summary>
    Descending,
}

/// <summary>The placement of NULL for one ORDER BY key.</summary>
public enum GqlNullOrdering
{
    /// <summary>NULL last ascending, first descending.</summary>
    Default,
    /// <summary>NULL precedes every non-null value.</summary>
    First,
    /// <summary>NULL follows every non-null value.</summary>
    Last,
}
