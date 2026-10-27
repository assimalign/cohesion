using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a parsed DELETE statement.
/// </summary>
public sealed class SqlDeleteExpression : SqlQueryExpression
{
    /// <summary>Gets the statement's non-recursive WITH definitions, in declaration order.</summary>
    public IReadOnlyList<SqlCommonTableExpression> CommonTables { get; internal set; } = [];

    /// <summary>
    /// Initializes a new <see cref="SqlDeleteExpression"/>.
    /// </summary>
    /// <param name="table">The target table.</param>
    /// <param name="where">The WHERE filter expression.</param>
    /// <param name="text">The raw statement text.</param>
    /// <param name="location">The source location.</param>
    /// <param name="returning">The written-row projection, if specified.</param>
    /// <param name="sources">The USING sources, if specified.</param>
    internal SqlDeleteExpression(
        SqlTableReference table,
        SqlExpression? where,
        string? text,
        Location? location,
        IReadOnlyList<SqlSelectColumn>? returning = null,
        IReadOnlyList<SqlFromItem>? sources = null)
        : base(SqlQueryCommandType.Delete, text, location)
    {
        Table = table;
        Using = sources ?? [];
        Returning = returning;
        Where = where;
    }

    /// <summary>
    /// Gets the target table reference.
    /// </summary>
    public SqlTableReference Table { get; }

    /// <summary>Gets the comma-separated USING sources and their join trees; empty when absent.</summary>
    public IReadOnlyList<SqlFromItem> Using { get; }

    /// <summary>Gets the written-row projection, or null when RETURNING is absent.</summary>
    public IReadOnlyList<SqlSelectColumn>? Returning { get; }

    /// <summary>
    /// Gets the WHERE filter expression, if present.
    /// </summary>
    public SqlExpression? Where { get; }
}
