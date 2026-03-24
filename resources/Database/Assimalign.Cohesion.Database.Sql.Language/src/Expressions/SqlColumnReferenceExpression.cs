namespace Assimalign.Cohesion.Database.Language.Sql;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a reference to a column, optionally qualified with a table alias and schema.
/// </summary>
/// <remarks>
/// Examples: <c>Id</c>, <c>u.Name</c>, <c>dbo.Users.Id</c>.
/// </remarks>
public sealed class SqlColumnReferenceExpression : SqlExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlColumnReferenceExpression"/>.
    /// </summary>
    /// <param name="columnName">The column name.</param>
    /// <param name="tableAlias">The table alias or name qualifier, if any.</param>
    /// <param name="schemaName">The schema qualifier, if any.</param>
    /// <param name="location">The source location.</param>
    internal SqlColumnReferenceExpression(string columnName, string? tableAlias, string? schemaName, Location? location)
        : base(location)
    {
        ColumnName = columnName;
        TableAlias = tableAlias;
        SchemaName = schemaName;
    }

    /// <summary>
    /// Gets the column name.
    /// </summary>
    public string ColumnName { get; }

    /// <summary>
    /// Gets the table alias or name qualifier, if present.
    /// </summary>
    public string? TableAlias { get; }

    /// <summary>
    /// Gets the schema qualifier, if present.
    /// </summary>
    public string? SchemaName { get; }
}
