using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a parsed CREATE INDEX statement:
/// <c>CREATE [UNIQUE] INDEX [IF NOT EXISTS] name ON table (column [, ...])</c>.
/// </summary>
public sealed class SqlCreateIndexExpression : SqlQueryExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlCreateIndexExpression"/>.
    /// </summary>
    /// <param name="indexName">The name of the index being created.</param>
    /// <param name="table">The table the index is created on.</param>
    /// <param name="columns">The ordered key column names.</param>
    /// <param name="isUnique">Whether UNIQUE was specified.</param>
    /// <param name="ifNotExists">Whether IF NOT EXISTS was specified.</param>
    /// <param name="text">The raw statement text.</param>
    /// <param name="location">The source location.</param>
    internal SqlCreateIndexExpression(
        string indexName,
        SqlTableReference table,
        IReadOnlyList<string> columns,
        bool isUnique,
        bool ifNotExists,
        string? text,
        Location? location)
        : base(SqlQueryCommandType.Create, text, location)
    {
        IndexName = indexName;
        Table = table;
        Columns = columns;
        IsUnique = isUnique;
        IfNotExists = ifNotExists;
    }

    /// <summary>
    /// Gets the name of the index being created.
    /// </summary>
    public string IndexName { get; }

    /// <summary>
    /// Gets the table the index is created on.
    /// </summary>
    public SqlTableReference Table { get; }

    /// <summary>
    /// Gets the ordered key column names.
    /// </summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>
    /// Gets whether the index enforces key uniqueness.
    /// </summary>
    public bool IsUnique { get; }

    /// <summary>
    /// Gets whether IF NOT EXISTS was specified.
    /// </summary>
    public bool IfNotExists { get; }
}
