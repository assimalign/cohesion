namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The bound plan produced by <see cref="SqlPlanner"/>: the AST resolved against the
/// catalog into executable shape. Transient probe costing chooses physical access
/// paths after binding without changing the executor seam or persisted catalog.
/// Every scalar expression a plan evaluates is a <see cref="SqlBoundExpression"/> the
/// planner compiled once; the executor never resolves a name or a collation per row.
/// </summary>
internal abstract record SqlPlan
{
    // Set once before publication, including cached view bodies.
    internal bool ContainsLike { get; set; }
    internal bool ContainsSequences => this is SqlSequenceScopePlan;
    internal SqlSequenceUsage? SequenceUsage => (this as SqlSequenceScopePlan)?.Usage;
}
