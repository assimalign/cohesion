namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Changes a column's type, optionally converting values from the original row.</summary>
public sealed class SqlAlterColumnTypeAction : SqlAlterAction
{
    internal SqlAlterColumnTypeAction(SqlColumnDefinition column, SqlExpression? usingExpression)
    {
        Column = column;
        UsingExpression = usingExpression;
    }

    /// <summary>Gets the target column name, type and optional collation.</summary>
    public SqlColumnDefinition Column { get; }

    /// <summary>Gets the conversion expression bound against the original row, or null for an assignment cast.</summary>
    public SqlExpression? UsingExpression { get; }
}
