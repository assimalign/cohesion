namespace Assimalign.Cohesion.Database.Indexing;

/// <summary>
/// The physical identity of a live B+Tree index: what a catalog persists so the
/// index can be re-attached when the database reopens.
/// </summary>
/// <param name="ObjectId">The identity of the object (table, collection, container) the index belongs to.</param>
/// <param name="Definition">The index definition.</param>
/// <param name="RootPageId">The page identifier of the tree's current root.</param>
public sealed record BTreeIndexRegistration(ulong ObjectId, IndexDefinition Definition, long RootPageId);
