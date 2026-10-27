namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Replaces a column's full definition through validation or a table rewrite.</summary>
public sealed class SqlAlterColumnAction : SqlAlterAction
{
    internal SqlAlterColumnAction(SqlColumnDefinition column)
    {
        Column = column;
    }

    /// <summary>Gets the replacement column definition.</summary>
    public SqlColumnDefinition Column { get; }
}
