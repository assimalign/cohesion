namespace Assimalign.Cohesion.Database.Language.Sql;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a parsed ALTER TABLE statement.
/// </summary>
public sealed class SqlAlterTableExpression : SqlQueryExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlAlterTableExpression"/>.
    /// </summary>
    /// <param name="table">The table being altered.</param>
    /// <param name="action">The alter action (ADD or DROP COLUMN).</param>
    /// <param name="text">The raw statement text.</param>
    /// <param name="location">The source location.</param>
    internal SqlAlterTableExpression(
        SqlTableReference table,
        SqlAlterAction action,
        string? text,
        Location? location)
        : base(SqlQueryCommandType.Alter, text, location)
    {
        Table = table;
        Action = action;
    }

    /// <summary>
    /// Gets the table being altered.
    /// </summary>
    public SqlTableReference Table { get; }

    /// <summary>
    /// Gets the alter action.
    /// </summary>
    public SqlAlterAction Action { get; }
}
