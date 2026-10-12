namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Replaces a column's definition while preserving its physical identity.</summary>
public sealed class SqlAlterColumnAction : SqlAlterAction
{
    internal SqlAlterColumnAction(SqlColumnDefinition column)
    {
        Column = column;
    }

    /// <summary>Gets the replacement column definition.</summary>
    public SqlColumnDefinition Column { get; }
}
