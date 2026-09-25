namespace Assimalign.Cohesion.Database.Indexing;

/// <summary>
/// The definition used to create an index.
/// </summary>
/// <param name="Name">The name of the index, unique within its owning object.</param>
/// <param name="Kind">The physical structure backing the index.</param>
/// <param name="IsUnique">Whether the index enforces key uniqueness.</param>
public readonly record struct IndexDefinition(
    string Name,
    IndexKind Kind = IndexKind.BTree,
    bool IsUnique = false);
