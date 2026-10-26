namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>A parenthesized join item whose constituent relation namespaces remain visible.</summary>
public sealed class SqlJoinedTableReference : SqlRelationReference
{
    internal SqlJoinedTableReference(SqlFromItem item) : base(null)
    {
        Item = item;
    }

    /// <summary>Gets the grouped join item, preserving its association and ON scopes.</summary>
    public SqlFromItem Item { get; }
}
