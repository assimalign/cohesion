using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A query over one collection in the session's database.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlSelectExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Language.OqlSelectExpression.DebugView))]
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
    /// <param name="isDistinct">Whether to eliminate duplicate projected values.</param>
    /// <param name="isValue">Whether to return one bare expression rather than named members.</param>
    /// <param name="limit">The optional maximum row count, as a signed integer literal or parameter.</param>
    /// <param name="offset">The optional number of rows to skip, as a signed integer literal or parameter.</param>
    /// <param name="location">The source span.</param>
    /// <param name="unnest">The ordered array bindings applied before WHERE.</param>
    public OqlSelectExpression(string collection, string? alias, IReadOnlyList<OqlProjection> projections,
        OqlExpression? predicate, IReadOnlyList<OqlExpression> groupBy, OqlExpression? having,
        IReadOnlyList<OqlOrdering> orderBy, bool isDistinct, bool isValue, OqlExpression? limit = null,
        OqlExpression? offset = null, Location? location = null, IReadOnlyList<OqlUnnestExpression>? unnest = null) : base(location)
    {
        Collection = collection;
        Alias = alias;
        Projections = projections;
        Predicate = predicate;
        GroupBy = groupBy;
        Having = having;
        OrderBy = orderBy;
        IsDistinct = isDistinct;
        IsValue = isValue;
        Limit = limit;
        Offset = offset;
        Unnest = unnest ?? [];
    }

    /// <summary>Gets whether duplicate projected values are eliminated.</summary>
    public bool IsDistinct { get; }
    /// <summary>Gets whether the projection returns one bare value (VALUE or RAW).</summary>
    public bool IsValue { get; }

    /// <summary>Gets the collection name; database qualification is never accepted.</summary>
    public string Collection { get; }
    /// <summary>Gets the optional iteration variable.</summary>
    public string? Alias { get; }
    /// <summary>Gets the array bindings in expansion order.</summary>
    public IReadOnlyList<OqlUnnestExpression> Unnest { get; }
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
    /// <summary>Gets the optional maximum row count.</summary>
    public OqlExpression? Limit { get; }
    /// <summary>Gets the optional number of rows to skip.</summary>
    public OqlExpression? Offset { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Documents.Language.OqlSelectExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Documents.Language.OqlSelectExpression value)
        {
            _target = value;
        }

        public bool IsDistinct => _target.IsDistinct;
        public bool IsValue => _target.IsValue;
        public string Collection => _target.Collection;
        public string? Alias => _target.Alias;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Documents.Language.OqlUnnestExpression> Unnest => _target.Unnest;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Documents.Language.OqlProjection> Projections => _target.Projections;
        public global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression? Predicate => _target.Predicate;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression> GroupBy => _target.GroupBy;
        public global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression? Having => _target.Having;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Documents.Language.OqlOrdering> OrderBy => _target.OrderBy;
        public global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression? Limit => _target.Limit;
        public global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression? Offset => _target.Offset;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
