using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a parsed SELECT statement.
/// </summary>
public sealed class SqlSelectExpression : SqlQueryExpression
{
    /// <summary>Gets the statement's non-recursive WITH definitions, in declaration order.</summary>
    public IReadOnlyList<SqlCommonTableExpression> CommonTables
    {
        get => _commonTables;
        internal set
        {
            _commonTables = value;
            foreach (var definition in value) { ExpressionDepth = System.Math.Max(ExpressionDepth, definition.Body.ExpressionDepth); }
        }
    }

    private IReadOnlyList<SqlCommonTableExpression> _commonTables = [];

    /// <summary>
    /// Initializes a new <see cref="SqlSelectExpression"/>.
    /// </summary>
    /// <param name="columns">The columns in the SELECT list.</param>
    /// <param name="from">The comma-separated FROM join trees.</param>
    /// <param name="where">The WHERE filter expression.</param>
    /// <param name="groupBy">The GROUP BY expressions.</param>
    /// <param name="having">The HAVING filter expression.</param>
    /// <param name="orderBy">The ORDER BY columns.</param>
    /// <param name="limit">The LIMIT expression.</param>
    /// <param name="offset">The OFFSET expression.</param>
    /// <param name="isDistinct">Whether DISTINCT was specified.</param>
    /// <param name="text">The raw statement text.</param>
    /// <param name="location">The source location.</param>
    /// <param name="limitSyntaxKind">The spelling that supplied the row limit.</param>
    internal SqlSelectExpression(
        IReadOnlyList<SqlSelectColumn> columns,
        IReadOnlyList<SqlFromItem> from,
        SqlExpression? where,
        IReadOnlyList<SqlExpression> groupBy,
        SqlExpression? having,
        IReadOnlyList<SqlOrderByColumn> orderBy,
        SqlExpression? limit,
        SqlExpression? offset,
        bool isDistinct,
        string? text,
        Location? location,
        SqlLimitSyntaxKind? limitSyntaxKind = null)
        : base(SqlQueryCommandType.Select, text, location)
    {
        Columns = columns;
        From = from;
        Where = where;
        GroupBy = groupBy;
        Having = having;
        OrderBy = orderBy;
        Limit = limit;
        LimitSyntaxKind = limit is null ? null : limitSyntaxKind ?? SqlLimitSyntaxKind.Limit;
        Offset = offset;
        IsDistinct = isDistinct;

        int depth = System.Math.Max(SqlExpression.DepthOf(where), SqlExpression.DepthOf(having));
        depth = System.Math.Max(depth, System.Math.Max(SqlExpression.DepthOf(limit), SqlExpression.DepthOf(offset)));
        depth = System.Math.Max(depth, SqlExpression.DepthOf(groupBy));
        foreach (var column in columns)
        {
            depth = System.Math.Max(depth, column.Expression.Depth);
        }
        foreach (var item in from)
        {
            if (item.Table is SqlDerivedTableReference derived)
            {
                depth = System.Math.Max(depth, 1 + derived.Query.ExpressionDepth);
            }
            foreach (var join in item.Joins)
            {
                if (join.Table is SqlDerivedTableReference joined)
                {
                    depth = System.Math.Max(depth, 1 + joined.Query.ExpressionDepth);
                }
                depth = System.Math.Max(depth, SqlExpression.DepthOf(join.Condition));
            }
        }
        foreach (var order in orderBy)
        {
            depth = System.Math.Max(depth, order.Expression.Depth);
        }

        ExpressionDepth = depth;
    }

    /// <summary>
    /// Gets the greatest <see cref="SqlExpression.Depth"/> among the query's clauses, or 0 when
    /// it has none. A subquery node is one level deeper than the query it wraps.
    /// </summary>
    internal int ExpressionDepth { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    /// A SELECT is the one query kind another statement nests, as a subquery or the source of an
    /// <c>INSERT</c>, so it is the one a caller can take out of a parsed statement and execute
    /// without a parser's measure.
    /// </remarks>
    private protected override int ExpressionTreeDepth => ExpressionDepth;

    /// <summary>
    /// Gets the columns in the SELECT list.
    /// </summary>
    public IReadOnlyList<SqlSelectColumn> Columns { get; }

    /// <summary>
    /// Gets the comma-separated FROM join trees; JOIN binds more tightly than comma.
    /// </summary>
    public IReadOnlyList<SqlFromItem> From { get; }


    /// <summary>
    /// Gets the WHERE filter expression, if present.
    /// </summary>
    public SqlExpression? Where { get; }

    /// <summary>
    /// Gets the GROUP BY expressions.
    /// </summary>
    public IReadOnlyList<SqlExpression> GroupBy { get; }

    /// <summary>
    /// Gets the HAVING filter expression, if present.
    /// </summary>
    public SqlExpression? Having { get; }

    /// <summary>
    /// Gets the ORDER BY columns.
    /// </summary>
    public IReadOnlyList<SqlOrderByColumn> OrderBy { get; }

    /// <summary>
    /// Gets the row-count expression supplied by LIMIT, FETCH or TOP, if present.
    /// </summary>
    public SqlExpression? Limit { get; }

    /// <summary>Gets the limit spelling, or null when the query has no row limit.</summary>
    public SqlLimitSyntaxKind? LimitSyntaxKind { get; }

    /// <summary>
    /// Gets the OFFSET expression, if present.
    /// </summary>
    public SqlExpression? Offset { get; }

    /// <summary>
    /// Gets whether DISTINCT was specified.
    /// </summary>
    public bool IsDistinct { get; }
}
