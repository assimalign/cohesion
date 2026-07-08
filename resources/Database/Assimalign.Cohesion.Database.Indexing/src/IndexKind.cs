namespace Assimalign.Cohesion.Database.Indexing;

/// <summary>
/// The physical structure backing an index.
/// </summary>
public enum IndexKind : byte
{
    /// <summary>
    /// A B+Tree: ordered keys, point and range lookups, forward and reverse scans.
    /// The default structure.
    /// </summary>
    BTree = 0,

    /// <summary>
    /// A hash table: point lookups only, no ordering. Post-MVP.
    /// </summary>
    Hash,
}
