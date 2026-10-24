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
    // Set once before publication, including cached view bodies. Child plans inherit the
    // executing statement's token; no token is retained in a cached plan.
    internal bool ContainsLike { get; set; }
}
