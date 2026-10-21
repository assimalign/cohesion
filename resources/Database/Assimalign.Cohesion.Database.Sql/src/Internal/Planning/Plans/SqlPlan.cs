namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The bound plan produced by <see cref="SqlPlanner"/>: the AST resolved against the
/// catalog into executable shape. Rule-based and deliberately simple — a cost-based
/// planner replaces the binding internals later without changing the executor seam.
/// Every scalar expression a plan evaluates is a <see cref="SqlBoundExpression"/> the
/// planner compiled once; the executor never resolves a name or a collation per row.
/// </summary>
internal abstract record SqlPlan
{
    // Set once before publication, including cached view bodies. Child plans inherit the
    // executing statement's token; no token is retained in a cached plan.
    internal bool ContainsLike { get; set; }
}
