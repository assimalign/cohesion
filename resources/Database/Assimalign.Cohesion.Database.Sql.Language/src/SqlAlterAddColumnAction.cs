namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>
/// Represents an ALTER TABLE ADD COLUMN action.
/// </summary>
public sealed class SqlAlterAddColumnAction : SqlAlterAction
{
    /// <summary>
    /// Initializes a new <see cref="SqlAlterAddColumnAction"/>.
    /// </summary>
    /// <param name="column">The column definition to add.</param>
    internal SqlAlterAddColumnAction(SqlColumnDefinition column)
    {
        Column = column;
    }

    /// <summary>
    /// Gets the column definition being added.
    /// </summary>
    public SqlColumnDefinition Column { get; }
}
