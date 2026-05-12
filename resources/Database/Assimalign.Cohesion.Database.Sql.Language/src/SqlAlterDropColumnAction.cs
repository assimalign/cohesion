namespace Assimalign.Cohesion.Database.Language.Sql;

/// <summary>
/// Represents an ALTER TABLE DROP COLUMN action.
/// </summary>
public sealed class SqlAlterDropColumnAction : SqlAlterAction
{
    /// <summary>
    /// Initializes a new <see cref="SqlAlterDropColumnAction"/>.
    /// </summary>
    /// <param name="columnName">The name of the column to drop.</param>
    internal SqlAlterDropColumnAction(string columnName)
    {
        ColumnName = columnName;
    }

    /// <summary>
    /// Gets the name of the column being dropped.
    /// </summary>
    public string ColumnName { get; }
}
