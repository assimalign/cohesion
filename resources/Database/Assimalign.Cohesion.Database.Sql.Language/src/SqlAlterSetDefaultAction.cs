namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Changes the default used by later omitted-column writes.</summary>
public sealed class SqlAlterSetDefaultAction : SqlAlterAction
{
    internal SqlAlterSetDefaultAction(string columnName, SqlExpression defaultValue)
    {
        ColumnName = columnName;
        DefaultValue = defaultValue;
    }

    /// <summary>Gets the column name.</summary>
    public string ColumnName { get; }

    /// <summary>Gets the new default expression.</summary>
    public SqlExpression DefaultValue { get; }
}
