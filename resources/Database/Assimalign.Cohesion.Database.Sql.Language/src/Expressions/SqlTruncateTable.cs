namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>One table reference in a TRUNCATE list.</summary>
public sealed class SqlTruncateTable
{
    internal SqlTruncateTable(SqlTableReference table, bool only)
    {
        Table = table;
        Only = only;
    }

    /// <summary>Gets the optionally namespace-qualified table.</summary>
    public SqlTableReference Table { get; }

    /// <summary>Gets whether ONLY was written. SQL tables currently have no descendants.</summary>
    public bool Only { get; }
}
