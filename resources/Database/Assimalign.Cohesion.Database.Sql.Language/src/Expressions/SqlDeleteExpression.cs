namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a parsed DELETE statement.
/// </summary>
public sealed class SqlDeleteExpression : SqlQueryExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlDeleteExpression"/>.
    /// </summary>
    /// <param name="table">The target table.</param>
    /// <param name="where">The WHERE filter expression.</param>
    /// <param name="text">The raw statement text.</param>
    /// <param name="location">The source location.</param>
    internal SqlDeleteExpression(
        SqlTableReference table,
        SqlExpression? where,
        string? text,
        Location? location)
        : base(SqlQueryCommandType.Delete, text, location)
    {
        Table = table;
        Where = where;
    }

    /// <summary>
    /// Gets the target table reference.
    /// </summary>
    public SqlTableReference Table { get; }

    /// <summary>
    /// Gets the WHERE filter expression, if present.
    /// </summary>
    public SqlExpression? Where { get; }
}
