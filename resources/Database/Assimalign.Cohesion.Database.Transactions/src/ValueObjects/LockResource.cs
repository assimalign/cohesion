using System;

namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Identifies a lockable resource in the lock hierarchy.
/// </summary>
/// <param name="Kind">The level of the resource in the lock hierarchy.</param>
/// <param name="ObjectId">The identity of the container object (table, collection, index) the resource belongs to; zero at the database level.</param>
/// <param name="EntryId">The identity of the entry (row, document, key) within the object; zero for object- and database-level resources.</param>
public readonly record struct LockResource(LockResourceKind Kind, ulong ObjectId, ulong EntryId)
{
    /// <summary>
    /// Creates a database-level resource.
    /// </summary>
    /// <returns>The database-level lock resource.</returns>
    public static LockResource Database() => new(LockResourceKind.Database, 0, 0);

    /// <summary>
    /// Creates an object-level resource (table, collection, container, or index).
    /// </summary>
    /// <param name="objectId">The identity of the object.</param>
    /// <returns>The object-level lock resource.</returns>
    public static LockResource Object(ulong objectId) => new(LockResourceKind.Object, objectId, 0);

    /// <summary>
    /// Creates an entry-level resource (row, document, key, node, or blob).
    /// </summary>
    /// <param name="objectId">The identity of the containing object.</param>
    /// <param name="entryId">The identity of the entry within the object.</param>
    /// <returns>The entry-level lock resource.</returns>
    public static LockResource Entry(ulong objectId, ulong entryId) => new(LockResourceKind.Entry, objectId, entryId);
}

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
