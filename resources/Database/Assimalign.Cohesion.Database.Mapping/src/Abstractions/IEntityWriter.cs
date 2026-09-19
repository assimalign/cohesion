namespace Assimalign.Cohesion.Database.Mapping;

/// <summary>Writes mapped entity values to a model-owned target using statically bound access.</summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TTarget">The model-specific target.</typeparam>
public interface IEntityWriter<in TEntity, in TTarget>
{
    /// <summary>Writes mapped values to the supplied target.</summary>
    /// <param name="entity">The entity to write.</param>
    /// <param name="target">The model-specific destination.</param>
    void Write(TEntity entity, TTarget target);
}
