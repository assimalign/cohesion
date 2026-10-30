namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>The leaves of <see cref="SqlBoundExpression"/>, one per node class.</summary>
internal enum SqlBoundExpressionKind : byte
{
    /// <summary><see cref="SqlBoundConstant"/>: a literal or other value fixed when the expression was bound.</summary>
    Constant,

    /// <summary><see cref="SqlBoundColumn"/>: a source column, by ordinal in the row.</summary>
    Column,

    /// <summary><see cref="SqlBoundOuterColumn"/>: a value in an enclosing query's evaluation frame.</summary>
    OuterColumn,

    /// <summary><see cref="SqlBoundGrouping"/>: a mask in its owning outer query's grouped frame.</summary>
    Grouping,

    /// <summary><see cref="SqlBoundSlot"/>: a grouping key, an aggregate's result or a completed output column, by ordinal.</summary>
    Slot,

    /// <summary><see cref="SqlBoundParameter"/>: a statement parameter's supplied value.</summary>
    Parameter,

    /// <summary><see cref="SqlBoundCall"/>: a scalar function call bound to its function.</summary>
    Call,

    /// <summary><see cref="SqlBoundCoalesce"/>: <c>COALESCE</c>, which evaluates its arguments lazily.</summary>
    Coalesce,

    /// <summary>NULLIF and EXTRACT evaluator forms.</summary>
    SpecialForm,

    /// <summary>A session sequence operation.</summary>
    Sequence,

    /// <summary><see cref="SqlBoundLogical"/>: an <c>AND</c> or <c>OR</c> chain.</summary>
    Logical,

    /// <summary><see cref="SqlBoundBinary"/>: a comparison, arithmetic or concatenation operator.</summary>
    Binary,

    /// <summary><see cref="SqlBoundUnary"/>: <c>-</c>, <c>+</c> or <c>NOT</c>.</summary>
    Unary,

    /// <summary><see cref="SqlBoundIsNull"/>: <c>IS [NOT] NULL</c>.</summary>
    IsNull,

    /// <summary>A null-safe distinct predicate.</summary>
    Distinct,

    /// <summary>A Boolean truth test.</summary>
    BooleanTest,

    /// <summary><see cref="SqlBoundBetween"/>: <c>[NOT] BETWEEN</c>.</summary>
    Between,

    /// <summary><see cref="SqlBoundIn"/>: <c>[NOT] IN</c> over a value list.</summary>
    In,

    /// <summary><see cref="SqlBoundInSubquery"/>: <c>[NOT] IN</c> over a subquery's materialized values.</summary>
    InSubquery,

    /// <summary>A scalar ANY, SOME or ALL subquery comparison.</summary>
    QuantifiedComparison,

    /// <summary><see cref="SqlBoundLike"/>: <c>[NOT] LIKE</c> or <c>[NOT] ILIKE</c>.</summary>
    Like,

    /// <summary><see cref="SqlBoundCase"/>: a searched or simple <c>CASE</c>.</summary>
    Case,

    /// <summary><see cref="SqlBoundCast"/>: <c>CAST</c>.</summary>
    Cast,

    /// <summary><see cref="SqlBoundSubquery"/>: a scalar subquery's or an <c>EXISTS</c>'s materialized value.</summary>
    Subquery,

    /// <summary><see cref="SqlBoundFailure"/>: a node whose evaluation fails, as it did before binding existed.</summary>
    Failure,

    /// <summary>A row-independent column DEFAULT expression.</summary>
    Default,

    /// <summary><see cref="SqlBoundDefaultItem"/>: use the target column's omitted-value path.</summary>
    DefaultItem,

    /// <summary><see cref="SqlBoundDomainArgument"/>: domain coercion before a function's STRICT dispatch.</summary>
    DomainArgument,
}
