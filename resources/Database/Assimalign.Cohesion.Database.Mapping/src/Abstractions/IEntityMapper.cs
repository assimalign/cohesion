namespace Assimalign.Cohesion.Database.Mapping;

/// <summary>Supplies statically bound identity and detached change snapshots for an entity mapping.</summary>
/// <typeparam name="TEntity">The mutable entity type.</typeparam>
/// <typeparam name="TKey">The immutable identity value.</typeparam>
/// <typeparam name="TSnapshot">The mapping-owned snapshot shape.</typeparam>
/// <remarks>Capture must copy mutable mapped values. Snapshots and keys must remain immutable;
/// comparison must cover exactly the mapped values. No member discovery is performed by the core.</remarks>
public interface IEntityMapper<TEntity, TKey, TSnapshot>
    where TEntity : class
    where TKey : notnull
{
    /// <summary>Reads the entity's stable identity.</summary>
    /// <param name="entity">The non-null entity.</param>
    /// <returns>The non-null immutable key.</returns>
    TKey GetKey(TEntity entity);

    /// <summary>Captures the mapped values without retaining mutable entity state.</summary>
    /// <param name="entity">The non-null entity.</param>
    /// <returns>An immutable, detached snapshot.</returns>
    TSnapshot Capture(TEntity entity);

    /// <summary>Compares the mapped values of two snapshots.</summary>
    /// <param name="left">The first snapshot.</param>
    /// <param name="right">The second snapshot.</param>
    /// <returns>Whether every mapped value is equal.</returns>
    bool AreEqual(TSnapshot left, TSnapshot right);
}
