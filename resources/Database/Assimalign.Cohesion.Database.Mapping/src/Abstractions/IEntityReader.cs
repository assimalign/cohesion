namespace Assimalign.Cohesion.Database.Mapping;

/// <summary>Materializes an entity from a model-owned source shape using statically bound access.</summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TSource">The source shape, which may preserve documents or paths.</typeparam>
public interface IEntityReader<out TEntity, in TSource>
{
    /// <summary>Materializes one entity.</summary>
    /// <param name="source">The model-specific source.</param>
    /// <returns>The materialized entity.</returns>
    TEntity Read(TSource source);
}
