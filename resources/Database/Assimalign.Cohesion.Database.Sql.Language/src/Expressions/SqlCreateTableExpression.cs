using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a parsed CREATE TABLE statement.
/// </summary>
public sealed class SqlCreateTableExpression : SqlQueryExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlCreateTableExpression"/>.
    /// </summary>
    /// <param name="table">The table being created.</param>
    /// <param name="columns">The column definitions.</param>
    /// <param name="ifNotExists">Whether IF NOT EXISTS was specified.</param>
    /// <param name="text">The raw statement text.</param>
    /// <param name="location">The source location.</param>
    internal SqlCreateTableExpression(
        SqlTableReference table,
        IReadOnlyList<SqlColumnDefinition> columns,
        bool ifNotExists,
        string? text,
        Location? location)
        : base(SqlQueryCommandType.Create, text, location)
    {
        Table = table;
        Columns = columns;
        IfNotExists = ifNotExists;
    }

    /// <summary>
    /// Gets the table being created.
    /// </summary>
    public SqlTableReference Table { get; }

    /// <summary>
    /// Gets the column definitions.
    /// </summary>
    public IReadOnlyList<SqlColumnDefinition> Columns { get; }

    /// <summary>
    /// Gets whether IF NOT EXISTS was specified.
    /// </summary>
    public bool IfNotExists { get; }
}
