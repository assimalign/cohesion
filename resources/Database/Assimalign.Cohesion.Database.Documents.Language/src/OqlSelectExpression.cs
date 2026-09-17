using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A query over one collection in the session's database.</summary>
public sealed class OqlSelectExpression : OqlExpression
{
    /// <summary>Initializes a collection query.</summary>
    /// <param name="collection">The unqualified collection name or reserved COHESION_SCHEMA system collection name.</param>
    /// <param name="alias">The optional iteration variable.</param>
    /// <param name="projections">The projected expressions.</param>
    /// <param name="predicate">The optional document filter.</param>
    /// <param name="groupBy">The grouping expressions.</param>
    /// <param name="having">The optional group filter.</param>
    /// <param name="orderBy">The ordering expressions.</param>
    /// <param name="location">The source span.</param>
    public OqlSelectExpression(string collection, string? alias, IReadOnlyList<OqlProjection> projections,
        OqlExpression? predicate, IReadOnlyList<OqlExpression> groupBy, OqlExpression? having,
        IReadOnlyList<OqlOrdering> orderBy, Location? location = null) : base(location)
    {
        Collection = collection;
        Alias = alias;
        Projections = projections;
        Predicate = predicate;
        GroupBy = groupBy;
        Having = having;
        OrderBy = orderBy;
    }

    /// <summary>Gets the collection name; database qualification is never accepted.</summary>
    public string Collection { get; }
    /// <summary>Gets the optional iteration variable.</summary>
    public string? Alias { get; }
    /// <summary>Gets the projection expressions in source order.</summary>
    public IReadOnlyList<OqlProjection> Projections { get; }
    /// <summary>Gets the optional document predicate.</summary>
    public OqlExpression? Predicate { get; }
    /// <summary>Gets the grouping expressions.</summary>
    public IReadOnlyList<OqlExpression> GroupBy { get; }
    /// <summary>Gets the optional group predicate.</summary>
    public OqlExpression? Having { get; }
    /// <summary>Gets the ordering expressions, from primary to final key.</summary>
    public IReadOnlyList<OqlOrdering> OrderBy { get; }
}
