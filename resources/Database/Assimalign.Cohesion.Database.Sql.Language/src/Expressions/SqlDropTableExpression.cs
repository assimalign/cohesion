namespace Assimalign.Cohesion.Database.Language.Sql;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a parsed DROP TABLE statement.
/// </summary>
public sealed class SqlDropTableExpression : SqlQueryExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlDropTableExpression"/>.
    /// </summary>
    /// <param name="table">The table being dropped.</param>
    /// <param name="ifExists">Whether IF EXISTS was specified.</param>
    /// <param name="text">The raw statement text.</param>
    /// <param name="location">The source location.</param>
    internal SqlDropTableExpression(
        SqlTableReference table,
        bool ifExists,
        string? text,
        Location? location)
        : base(SqlQueryCommandType.Drop, text, location)
    {
        Table = table;
        IfExists = ifExists;
    }

    /// <summary>
    /// Gets the table being dropped.
    /// </summary>
    public SqlTableReference Table { get; }

    /// <summary>
    /// Gets whether IF EXISTS was specified.
    /// </summary>
    public bool IfExists { get; }
}
