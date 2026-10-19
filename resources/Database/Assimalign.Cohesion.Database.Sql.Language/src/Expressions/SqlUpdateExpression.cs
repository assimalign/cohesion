using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a parsed UPDATE statement.
/// </summary>
public sealed class SqlUpdateExpression : SqlQueryExpression
{
    /// <summary>Gets the statement's non-recursive WITH definitions, in declaration order.</summary>
    public IReadOnlyList<SqlCommonTableExpression> CommonTables { get; internal set; } = [];

    /// <summary>
    /// Initializes a new <see cref="SqlUpdateExpression"/>.
    /// </summary>
    /// <param name="table">The target table.</param>
    /// <param name="assignments">The SET assignments.</param>
    /// <param name="where">The WHERE filter expression.</param>
    /// <param name="text">The raw statement text.</param>
    /// <param name="location">The source location.</param>
    /// <param name="returning">The written-row projection, if specified.</param>
    internal SqlUpdateExpression(
        SqlTableReference table,
        IReadOnlyList<SqlAssignment> assignments,
        SqlExpression? where,
        string? text,
        Location? location,
        IReadOnlyList<SqlSelectColumn>? returning = null)
        : base(SqlQueryCommandType.Update, text, location)
    {
        Table = table;
        Returning = returning;
        Assignments = assignments;
        Where = where;
    }

    /// <summary>
    /// Gets the target table reference.
    /// </summary>
    public SqlTableReference Table { get; }

    /// <summary>Gets the written-row projection, or null when RETURNING is absent.</summary>
    public IReadOnlyList<SqlSelectColumn>? Returning { get; }

    /// <summary>
    /// Gets the SET assignments.
    /// </summary>
    public IReadOnlyList<SqlAssignment> Assignments { get; }

    /// <summary>
    /// Gets the WHERE filter expression, if present.
    /// </summary>
    public SqlExpression? Where { get; }
}
