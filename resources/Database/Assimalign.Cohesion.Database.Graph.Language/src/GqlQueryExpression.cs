using System;
using System.Collections.Generic;
using System.Linq;
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

    private readonly IReadOnlyList<GqlStage>? _stages;

    /// <summary>Initializes a source-ordered linear statement.</summary>
    /// <param name="stages">The operations, including NEXT segment boundaries.</param>
    /// <param name="location">The statement source span.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stages"/> is null.</exception>
    public GqlQueryExpression(IReadOnlyList<GqlStage> stages, Location? location = null) : base(location)
    {
        ArgumentNullException.ThrowIfNull(stages);
        _stages = stages;
        MatchClauses = stages.OfType<GqlMatchStage>().Select(stage => stage.Clause).ToArray();
        Creates = stages.OfType<GqlInsertStage>().SelectMany(stage => stage.Paths ?? []).ToArray();
        DeleteVariables = stages.OfType<GqlDeleteStage>().SelectMany(stage => stage.Variables ?? []).ToArray();
        DetachDelete = stages.OfType<GqlDeleteStage>().Any(stage => stage.Detach);
        var final = stages.LastOrDefault() as GqlReturnStage;
        Projections = final?.Projections ?? [];
        SetQuantifier = final?.SetQuantifier ?? GqlSetQuantifier.All;
        ReturnStar = final?.ReturnStar ?? false;
        GroupBy = final?.GroupBy;
        OrderBy = final?.OrderBy ?? [];
        Offset = final?.Offset;
        Limit = final?.Limit;
    }

    /// <summary>Gets the operations in source order. Original constructors provide a single-segment facade.</summary>
    public IReadOnlyList<GqlStage> Stages
    {
        get
        {
            if (_stages is not null) { return _stages; }
            var stages = new List<GqlStage>();
            if (MatchClauses is not null)
            { foreach (var clause in MatchClauses) { stages.Add(new GqlMatchStage(clause)); } }
            if (Creates.Count != 0) { stages.Add(new GqlInsertStage(Creates)); }
            if (DeleteVariables.Count != 0 || DetachDelete) { stages.Add(new GqlDeleteStage(DeleteVariables, DetachDelete)); }
            if (Projections.Count != 0 || ReturnStar || SetQuantifier != GqlSetQuantifier.All || GroupBy is not null || OrderBy is null || OrderBy.Count != 0 || Offset is not null || Limit is not null)
            {
                stages.Add(new GqlReturnStage(Projections) { SetQuantifier = SetQuantifier, ReturnStar = ReturnStar,
                    GroupBy = GroupBy, OrderBy = OrderBy!, Offset = Offset, Limit = Limit });
            }
            return stages.AsReadOnly();
        }
    }

    /// <summary>Gets the linear segments separated by explicit NEXT boundaries.</summary>
    public IReadOnlyList<GqlQuerySegment> Segments
    {
        get
        {
            var segments = new List<GqlQuerySegment>();
            var current = new List<GqlStage>();
            foreach (var stage in Stages)
            {
                if (stage is GqlNextStage) { segments.Add(new GqlQuerySegment(current.AsReadOnly())); current = []; }
                else { current.Add(stage); }
            }
            segments.Add(new GqlQuerySegment(current.AsReadOnly()));
            return segments.AsReadOnly();
        }
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
    /// <summary>Gets the explicit GROUP BY names or expressions; empty for (), or null for implicit grouping.</summary>
    public IReadOnlyList<GqlExpression>? GroupBy { get; init; }
    /// <summary>Gets the ORDER BY keys in priority order.</summary>
    public IReadOnlyList<GqlSortItem> OrderBy { get; init; } = [];
    /// <summary>Gets the non-negative integer literal or parameter for OFFSET (also spelled SKIP).</summary>
    public GqlExpression? Offset { get; init; }
    /// <summary>Gets the non-negative integer literal or parameter for LIMIT.</summary>
    public GqlExpression? Limit { get; init; }
    /// <summary>Gets the optional read-only catalog subject; graph clauses cannot accompany it.</summary>
    public GqlCatalogSurface? CatalogSurface { get; init; }
}
