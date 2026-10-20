namespace Assimalign.Cohesion.Database.Graph.Language;

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
