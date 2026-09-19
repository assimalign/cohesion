using System;

namespace Assimalign.Cohesion.Database.Mapping;

/// <summary>Maintains identity within one registered mapping in a unit of work.</summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TKey">The immutable key type.</typeparam>
/// <remarks>Keys must not change while tracked. The set is single-caller and must not be used during save.
/// Separate registrations represent separate identity spaces, even when their CLR entity types match.</remarks>
public interface ITrackedEntities<TEntity, in TKey> where TEntity : class where TKey : notnull
{
    /// <summary>Tracks an existing entity, or returns the instance already tracked for its key.</summary>
    /// <param name="entity">The materialized entity.</param>
    /// <returns>The canonical tracked instance; existing local values are never overwritten.</returns>
    /// <exception cref="ArgumentNullException">The entity is null.</exception>
    /// <exception cref="InvalidOperationException">A save is active or a tracked key changed.</exception>
    TEntity Attach(TEntity entity);

    /// <summary>Tracks an entity for insertion.</summary>
    /// <param name="entity">The new entity with its application-assigned key.</param>
    /// <exception cref="ArgumentNullException">The entity is null.</exception>
    /// <exception cref="InvalidOperationException">The key is already tracked, a key changed, or a save is active.</exception>
    void Add(TEntity entity);

    /// <summary>Schedules deletion, or cancels an entity's pending insertion.</summary>
    /// <param name="entity">The exact tracked instance.</param>
    /// <exception cref="ArgumentNullException">The entity is null.</exception>
    /// <exception cref="InvalidOperationException">The instance is not tracked, a key changed, or a save is active.</exception>
    void Remove(TEntity entity);

    /// <summary>Finds the canonical tracked instance, including one pending deletion.</summary>
    /// <param name="key">The identity to find.</param>
    /// <returns>The tracked entity, or null when absent.</returns>
    /// <exception cref="ArgumentNullException">The key is null.</exception>
    /// <exception cref="InvalidOperationException">A save is active or a tracked key changed.</exception>
    TEntity? Find(TKey key);
}
