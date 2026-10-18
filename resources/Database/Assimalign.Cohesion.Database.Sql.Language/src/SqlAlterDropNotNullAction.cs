namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Allows null values in an ordinary non-key column.</summary>
public sealed class SqlAlterDropNotNullAction : SqlAlterAction
{
    internal SqlAlterDropNotNullAction(string columnName)
    {
        ColumnName = columnName;
    }

    /// <summary>Gets the column name.</summary>
    public string ColumnName { get; }
}
