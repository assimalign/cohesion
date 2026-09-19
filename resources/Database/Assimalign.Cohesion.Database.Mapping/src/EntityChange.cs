namespace Assimalign.Cohesion.Database.Mapping;

/// <summary>Contains detached values staged by a single save.</summary>
/// <typeparam name="TKey">The immutable identity value.</typeparam>
/// <typeparam name="TSnapshot">The mapping-owned immutable snapshot.</typeparam>
/// <param name="Kind">The persistence operation.</param>
/// <param name="Key">The identity captured when tracking began.</param>
/// <param name="Original">The accepted snapshot, or default for an addition.</param>
/// <param name="Current">The snapshot being saved, or default for a deletion.</param>
public readonly record struct EntityChange<TKey, TSnapshot>(
    EntityChangeKind Kind, TKey Key, TSnapshot Original, TSnapshot Current) where TKey : notnull;
