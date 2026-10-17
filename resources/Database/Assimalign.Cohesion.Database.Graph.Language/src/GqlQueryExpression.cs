using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A finite graph query or read-only catalog statement in one database.</summary>
public sealed class GqlQueryExpression : GqlExpression
{
    /// <summary>Initializes a new instance of the <see cref="GqlQueryExpression"/> class.</summary>
    /// <param name="matches">The patterns whose variables are bound before mutation or projection.</param>
    /// <param name="predicate">The optional scalar filter over matched bindings.</param>
    /// <param name="creates">The patterns inserted for each matching binding.</param>
    /// <param name="deleteVariables">The bound variables to delete.</param>
    /// <param name="detachDelete">Whether deleting nodes also deletes incident relationships.</param>
    /// <param name="projections">The result columns in source order.</param>
    /// <param name="location">The source span.</param>
    public GqlQueryExpression(IReadOnlyList<GqlPathPattern> matches, GqlExpression? predicate,
        IReadOnlyList<GqlPathPattern> creates, IReadOnlyList<string> deleteVariables, bool detachDelete,
        IReadOnlyList<GqlProjection> projections, Location? location = null)
        : this(matches is { Count: 0 } && predicate is null ? [] : [new GqlMatchClause(matches, predicate, false)],
            creates, deleteVariables, detachDelete, projections, location)
    {
    }

    /// <summary>Initializes a query with source-ordered matching clauses.</summary>
    /// <param name="matchClauses">The matching clauses evaluated before mutation or projection.</param>
    /// <param name="creates">The patterns inserted for each matching binding.</param>
    /// <param name="deleteVariables">The bound variables to delete.</param>
    /// <param name="detachDelete">Whether deleting nodes also deletes incident relationships.</param>
    /// <param name="projections">The result columns in source order.</param>
    /// <param name="location">The source span.</param>
    public GqlQueryExpression(IReadOnlyList<GqlMatchClause> matchClauses,
        IReadOnlyList<GqlPathPattern> creates, IReadOnlyList<string> deleteVariables, bool detachDelete,
        IReadOnlyList<GqlProjection> projections, Location? location = null) : base(location)
    {
        MatchClauses = matchClauses;
        Creates = creates;
        DeleteVariables = deleteVariables;
        DetachDelete = detachDelete;
        Projections = projections;
    }

    /// <summary>Gets the matching clauses in source order.</summary>
    public IReadOnlyList<GqlMatchClause> MatchClauses { get; }
    /// <summary>Gets the paths when there is exactly one mandatory clause; empty otherwise.</summary>
    public IReadOnlyList<GqlPathPattern> Matches => MatchClauses is [{ Optional: false } clause] ? clause.Paths : [];
    /// <summary>Gets the predicate when there is exactly one mandatory clause; null otherwise.</summary>
    public GqlExpression? Predicate => MatchClauses is [{ Optional: false } clause] ? clause.Predicate : null;
    /// <summary>Gets the patterns to insert.</summary>
    public IReadOnlyList<GqlPathPattern> Creates { get; }
    /// <summary>Gets the bound node or relationship variables to delete.</summary>
    public IReadOnlyList<string> DeleteVariables { get; }
    /// <summary>Gets whether deletion cascades to incident relationships.</summary>
    public bool DetachDelete { get; }
    /// <summary>Gets whether EXPLAIN requests a description instead of execution.</summary>
    public bool IsExplain { get; private set; }

    internal void Explain() => IsExplain = true;
    /// <summary>Gets the result projections.</summary>
    public IReadOnlyList<GqlProjection> Projections { get; }
    /// <summary>Gets the RETURN set quantifier; omitted quantifiers mean ALL.</summary>
    public GqlSetQuantifier SetQuantifier { get; init; }
    /// <summary>Gets whether RETURN * projects every named binding in declaration order.</summary>
    public bool ReturnStar { get; init; }
    /// <summary>Gets the optional read-only catalog subject; graph clauses cannot accompany it.</summary>
    public GqlCatalogSurface? CatalogSurface { get; init; }
}
