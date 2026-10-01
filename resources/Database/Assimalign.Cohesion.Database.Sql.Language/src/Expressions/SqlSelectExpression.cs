using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a parsed SELECT statement.
/// </summary>
public sealed class SqlSelectExpression : SqlQueryExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlSelectExpression"/>.
    /// </summary>
    /// <param name="columns">The columns in the SELECT list.</param>
    /// <param name="from">The FROM table reference.</param>
    /// <param name="joins">The JOIN clauses.</param>
    /// <param name="where">The WHERE filter expression.</param>
    /// <param name="groupBy">The GROUP BY expressions.</param>
    /// <param name="having">The HAVING filter expression.</param>
    /// <param name="orderBy">The ORDER BY columns.</param>
    /// <param name="limit">The LIMIT expression.</param>
    /// <param name="offset">The OFFSET expression.</param>
    /// <param name="isDistinct">Whether DISTINCT was specified.</param>
    /// <param name="text">The raw statement text.</param>
    /// <param name="location">The source location.</param>
    internal SqlSelectExpression(
        IReadOnlyList<SqlSelectColumn> columns,
        SqlTableReference? from,
        IReadOnlyList<SqlJoinClause> joins,
        SqlExpression? where,
        IReadOnlyList<SqlExpression> groupBy,
        SqlExpression? having,
        IReadOnlyList<SqlOrderByColumn> orderBy,
        SqlExpression? limit,
        SqlExpression? offset,
        bool isDistinct,
        string? text,
        Location? location)
        : base(SqlQueryCommandType.Select, text, location)
    {
        Columns = columns;
        From = from;
        Joins = joins;
        Where = where;
        GroupBy = groupBy;
        Having = having;
        OrderBy = orderBy;
        Limit = limit;
        Offset = offset;
        IsDistinct = isDistinct;

        int depth = System.Math.Max(SqlExpression.DepthOf(where), SqlExpression.DepthOf(having));
        depth = System.Math.Max(depth, System.Math.Max(SqlExpression.DepthOf(limit), SqlExpression.DepthOf(offset)));
        depth = System.Math.Max(depth, SqlExpression.DepthOf(groupBy));
        foreach (var column in columns)
        {
            depth = System.Math.Max(depth, column.Expression.Depth);
        }
        foreach (var join in joins)
        {
            depth = System.Math.Max(depth, SqlExpression.DepthOf(join.Condition));
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
    internal int ExpressionDepth { get; }

    /// <summary>
    /// Gets the columns in the SELECT list.
    /// </summary>
    public IReadOnlyList<SqlSelectColumn> Columns { get; }

    /// <summary>
    /// Gets the FROM table reference, if present.
    /// </summary>
    public SqlTableReference? From { get; }

    /// <summary>
    /// Gets the JOIN clauses.
    /// </summary>
    public IReadOnlyList<SqlJoinClause> Joins { get; }

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
    /// Gets the LIMIT expression, if present.
    /// </summary>
    public SqlExpression? Limit { get; }

    /// <summary>
    /// Gets the OFFSET expression, if present.
    /// </summary>
    public SqlExpression? Offset { get; }

    /// <summary>
    /// Gets whether DISTINCT was specified.
    /// </summary>
    public bool IsDistinct { get; }
}
