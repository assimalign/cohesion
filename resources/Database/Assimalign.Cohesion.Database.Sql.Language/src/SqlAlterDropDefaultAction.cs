namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Removes a column's insert-time default.</summary>
public sealed class SqlAlterDropDefaultAction : SqlAlterAction
{
    internal SqlAlterDropDefaultAction(string columnName)
    {
        ColumnName = columnName;
    }

    /// <summary>Gets the column name.</summary>
    public string ColumnName { get; }
}
