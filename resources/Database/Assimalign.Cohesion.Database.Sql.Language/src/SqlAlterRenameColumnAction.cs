namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Renames a column while preserving its physical ordinal.</summary>
public sealed class SqlAlterRenameColumnAction : SqlAlterAction
{
    internal SqlAlterRenameColumnAction(string columnName, string newName)
    {
        ColumnName = columnName;
        NewName = newName;
    }

    /// <summary>Gets the existing column name.</summary>
    public string ColumnName { get; }

    /// <summary>Gets the new column name.</summary>
    public string NewName { get; }
}
