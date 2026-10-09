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
/// node, and casts to the sealed leaf the kind names. Binding never throws for something the
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

/// <summary>The leaves of <see cref="SqlBoundExpression"/>, one per node class.</summary>
internal enum SqlBoundExpressionKind : byte
{
    /// <summary><see cref="SqlBoundConstant"/>: a literal or other value fixed when the expression was bound.</summary>
    Constant,

    /// <summary><see cref="SqlBoundColumn"/>: a source column, by ordinal in the row.</summary>
    Column,

    /// <summary><see cref="SqlBoundSlot"/>: a grouping key, an aggregate's result or a completed output column, by ordinal.</summary>
    Slot,

    /// <summary><see cref="SqlBoundParameter"/>: a statement parameter's supplied value.</summary>
    Parameter,

    /// <summary><see cref="SqlBoundCall"/>: a scalar function call bound to its function.</summary>
    Call,

    /// <summary><see cref="SqlBoundCoalesce"/>: <c>COALESCE</c>, which evaluates its arguments lazily.</summary>
    Coalesce,

    /// <summary><see cref="SqlBoundLogical"/>: an <c>AND</c> or <c>OR</c> chain.</summary>
    Logical,

    /// <summary><see cref="SqlBoundBinary"/>: a comparison, arithmetic or concatenation operator.</summary>
    Binary,

    /// <summary><see cref="SqlBoundUnary"/>: <c>-</c>, <c>+</c> or <c>NOT</c>.</summary>
    Unary,

    /// <summary><see cref="SqlBoundIsNull"/>: <c>IS [NOT] NULL</c>.</summary>
    IsNull,

    /// <summary><see cref="SqlBoundBetween"/>: <c>[NOT] BETWEEN</c>.</summary>
    Between,

    /// <summary><see cref="SqlBoundIn"/>: <c>[NOT] IN</c> over a value list.</summary>
    In,

    /// <summary><see cref="SqlBoundInSubquery"/>: <c>[NOT] IN</c> over a subquery's materialized values.</summary>
    InSubquery,

    /// <summary><see cref="SqlBoundLike"/>: <c>[NOT] LIKE</c>.</summary>
    Like,

    /// <summary><see cref="SqlBoundCase"/>: a searched or simple <c>CASE</c>.</summary>
    Case,

    /// <summary><see cref="SqlBoundCast"/>: <c>CAST</c>.</summary>
    Cast,

    /// <summary><see cref="SqlBoundSubquery"/>: a scalar subquery's or an <c>EXISTS</c>'s materialized value.</summary>
    Subquery,

    /// <summary><see cref="SqlBoundFailure"/>: a node whose evaluation fails, as it did before binding existed.</summary>
    Failure,
}
