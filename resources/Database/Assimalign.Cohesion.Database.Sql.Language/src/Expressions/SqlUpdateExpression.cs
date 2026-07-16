using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a parsed UPDATE statement.
/// </summary>
public sealed class SqlUpdateExpression : SqlQueryExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlUpdateExpression"/>.
    /// </summary>
    /// <param name="table">The target table.</param>
    /// <param name="assignments">The SET assignments.</param>
    /// <param name="where">The WHERE filter expression.</param>
    /// <param name="text">The raw statement text.</param>
    /// <param name="location">The source location.</param>
    internal SqlUpdateExpression(
        SqlTableReference table,
        IReadOnlyList<SqlAssignment> assignments,
        SqlExpression? where,
        string? text,
        Location? location)
        : base(SqlQueryCommandType.Update, text, location)
    {
        Table = table;
        Assignments = assignments;
        Where = where;
    }

    /// <summary>
    /// Gets the target table reference.
    /// </summary>
    public SqlTableReference Table { get; }

    /// <summary>
    /// Gets the SET assignments.
    /// </summary>
    public IReadOnlyList<SqlAssignment> Assignments { get; }

    /// <summary>
    /// Gets the WHERE filter expression, if present.
    /// </summary>
    public SqlExpression? Where { get; }
}
