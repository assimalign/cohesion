using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a parsed SELECT statement.
/// </summary>
public sealed class SqlSelectExpression : SqlQueryBodyExpression
{
    private readonly object _grouping;

    /// <summary>
    /// Initializes a new <see cref="SqlSelectExpression"/>.
    /// </summary>
    /// <param name="columns">The columns in the SELECT list.</param>
    /// <param name="from">The comma-separated FROM join trees.</param>
    /// <param name="where">The WHERE filter expression.</param>
    /// <param name="groupBy">The GROUP BY expressions and grouping elements.</param>
    /// <param name="having">The HAVING filter expression.</param>
    /// <param name="orderBy">The ORDER BY columns.</param>
    /// <param name="limit">The LIMIT expression.</param>
    /// <param name="offset">The OFFSET expression.</param>
    /// <param name="isDistinct">Whether DISTINCT was specified.</param>
    /// <param name="text">The raw statement text.</param>
    /// <param name="location">The source location.</param>
    /// <param name="limitSyntaxKind">The spelling that supplied the row limit.</param>
    /// <param name="isGroupByDistinct">Whether duplicate expanded grouping sets are removed.</param>
    /// <param name="windows">The optional named windows in declaration order.</param>
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
        SqlLimitSyntaxKind? limitSyntaxKind = null,
        bool isGroupByDistinct = false, IReadOnlyList<SqlNamedWindow>? windows = null)
        : base(text, location)
    {
        Columns = columns;
        From = from;
        Where = where;
        _grouping = windows is { Count: > 0 } ? new WindowClause(groupBy, windows) : groupBy;
        IsGroupByDistinct = isGroupByDistinct;
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
            depth = System.Math.Max(depth, GroupDepth(item.Table));
            if (item.Table is SqlDerivedTableReference derived)
            {
                depth = System.Math.Max(depth, 1 + derived.Query.ExpressionDepth);
            }
            foreach (var join in item.Joins)
            {
                depth = System.Math.Max(depth, GroupDepth(join.Table));
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

        foreach (var window in Windows) { depth = System.Math.Max(depth, window.Specification.ExpressionDepth); }
        ExpressionDepth = depth;
    }

    private static int GroupDepth(SqlRelationReference reference)
    {
        if (reference is SqlDerivedTableReference derived) { return 1 + derived.Query.ExpressionDepth; }
        if (reference is not SqlJoinedTableReference group) { return 0; }
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        int depth = GroupDepth(group.Item.Table);
        foreach (var join in group.Item.Joins)
        {
            depth = System.Math.Max(depth, System.Math.Max(GroupDepth(join.Table), SqlExpression.DepthOf(join.Condition)));
        }
        return depth + 1;
    }

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
    /// Gets the GROUP BY expressions and grouping elements.
    /// </summary>
    public IReadOnlyList<SqlExpression> GroupBy => _grouping is WindowClause clause ? clause.GroupBy : (IReadOnlyList<SqlExpression>)_grouping;

    /// <summary>Gets the named windows, in declaration order.</summary>
    public IReadOnlyList<SqlNamedWindow> Windows => (_grouping as WindowClause)?.Windows ?? [];

    private sealed record WindowClause(IReadOnlyList<SqlExpression> GroupBy, IReadOnlyList<SqlNamedWindow> Windows);

    /// <summary>Gets whether GROUP BY DISTINCT removes duplicate expanded grouping sets.</summary>
    public bool IsGroupByDistinct { get; }

    /// <summary>
    /// Gets the HAVING filter expression, if present.
    /// </summary>
    public SqlExpression? Having { get; }

    /// <summary>
    /// Gets whether DISTINCT was specified.
    /// </summary>
    public bool IsDistinct { get; }
}
