namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>The executable ISO GQL aggregate functions.</summary>
public enum GqlAggregateFunction
{
    /// <summary>Counts rows or non-null values as Int64.</summary>
    Count,
    /// <summary>Sums non-null numeric values.</summary>
    Sum,
    /// <summary>Averages non-null numeric values.</summary>
    Avg,
    /// <summary>Selects the least non-null value.</summary>
    Min,
    /// <summary>Selects the greatest non-null value.</summary>
    Max,
    /// <summary>Collects non-null values in a list; also spelled collect in Cypher.</summary>
    CollectList,
}
