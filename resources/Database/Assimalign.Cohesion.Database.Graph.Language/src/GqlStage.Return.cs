using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>Projects a binding table using RETURN or Cypher WITH.</summary>
/// <param name="Projections">The projected expressions.</param>
/// <param name="IsWith">Whether the source used WITH.</param>
/// <param name="Location">The source span.</param>
public sealed record GqlReturnStage(IReadOnlyList<GqlProjection> Projections, bool IsWith = false, Location? Location = null) : GqlStage(Location)
{
    /// <summary>Gets the set quantifier; the default is ALL.</summary>
    public GqlSetQuantifier SetQuantifier { get; init; }
    /// <summary>Gets whether all variables are projected in scope order.</summary>
    public bool ReturnStar { get; init; }
    /// <summary>Gets explicit grouping expressions, or null for implicit grouping.</summary>
    public IReadOnlyList<GqlExpression>? GroupBy { get; init; }
    /// <summary>Gets the ordering keys.</summary>
    public IReadOnlyList<GqlSortItem> OrderBy { get; init; } = [];
    /// <summary>Gets the row offset.</summary>
    public GqlExpression? Offset { get; init; }
    /// <summary>Gets the row limit.</summary>
    public GqlExpression? Limit { get; init; }
    /// <summary>Gets the WITH predicate, evaluated after ordering and paging.</summary>
    public GqlExpression? Predicate { get; init; }
}
