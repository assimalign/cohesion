namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// The level of a <see cref="LockResource"/> in the lock hierarchy.
/// </summary>
public enum LockResourceKind : byte
{
    /// <summary>The whole logical database.</summary>
    Database = 0,

    /// <summary>A container object: table, collection, container, or index.</summary>
    Object,

    /// <summary>A single entry: row, document, key, node, relationship, or blob.</summary>
    Entry,
}
