namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// One node of an engine-owned bound expression: what the planner compiles each scalar expression
/// of a statement into, once, before any row is read, and what <see cref="SqlExpressionEvaluator"/>
/// walks for every row. A column reference is an ordinal, a function call holds the function it
/// resolved to, a comparison holds the collation it uses, and a subquery is a slot its plan fills,
/// so nothing is resolved by name, and no collation is searched for, per row.
/// </summary>
/// <remarks>
/// <para>
/// This is PostgreSQL's executor shape: <c>ExecInitExpr</c> compiles an expression tree once per
/// execution, and <c>ExecInitFunc</c> binds a call's function address then, not per row
/// (<c>src/backend/executor/execExpr.c</c>). The language package's AST is never rebuilt: a
/// bound node may refer to the AST node it came from (a CAST's resolved target) but no AST node is
/// constructed here, which <c>general-rules.md</c> forbids <c>Database.Sql</c> to do.
/// </para>
/// <para>
/// The family is closed and internal: the evaluator switches on <see cref="Kind"/>, one jump per
/// node, and casts to the concrete node the kind names. Binding never throws for something the
/// evaluator used to throw only when it reached the node (an unsupported construct, a missing
/// parameter value, an oversized literal): such a node binds as <see cref="SqlBoundFailure"/>,
/// which raises the same exception when, and only when, it is evaluated. Nodes are immutable, so
/// a bound CHECK predicate shared by every session of a table version is safe to evaluate on any
/// thread.
/// </para>
/// </remarks>
internal abstract class SqlBoundExpression
{
    private protected SqlBoundExpression(SqlBoundExpressionKind kind)
    {
        Kind = kind;
    }

    /// <summary>Gets which leaf of the family this node is.</summary>
    internal SqlBoundExpressionKind Kind { get; }
}
