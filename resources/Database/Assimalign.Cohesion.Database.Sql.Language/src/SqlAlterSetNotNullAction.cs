namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Requires every live and future column value to be nonnull.</summary>
public sealed class SqlAlterSetNotNullAction : SqlAlterAction
{
    internal SqlAlterSetNotNullAction(string columnName)
    {
        ColumnName = columnName;
    }

    /// <summary>Gets the column name.</summary>
    public string ColumnName { get; }
}
