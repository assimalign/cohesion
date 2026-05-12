namespace Assimalign.Cohesion.Database.Language.Sql;

/// <summary>
/// Represents a reference to a table, optionally qualified with a schema and alias.
/// </summary>
/// <remarks>
/// Examples: <c>Users</c>, <c>dbo.Users</c>, <c>Users u</c>, <c>dbo.Users AS u</c>.
/// </remarks>
public sealed class SqlTableReference
{
    /// <summary>
    /// Initializes a new <see cref="SqlTableReference"/>.
    /// </summary>
    /// <param name="tableName">The table name.</param>
    /// <param name="schemaName">The schema qualifier, if any.</param>
    /// <param name="alias">The table alias, if any.</param>
    internal SqlTableReference(string tableName, string? schemaName, string? alias)
    {
        TableName = tableName;
        SchemaName = schemaName;
        Alias = alias;
    }

    /// <summary>
    /// Gets the table name.
    /// </summary>
    public string TableName { get; }

    /// <summary>
    /// Gets the schema qualifier, if present.
    /// </summary>
    public string? SchemaName { get; }

    /// <summary>
    /// Gets the table alias, if present.
    /// </summary>
    public string? Alias { get; }
}
