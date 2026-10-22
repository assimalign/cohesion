namespace Assimalign.Cohesion.ObjectMapping.Internal;

/// <summary>Runs a built-in action against the already checked profile types.</summary>
internal abstract class TypedMapperAction<TTarget, TSource> : IMapperAction
{
    public abstract void Invoke(IMapperContext context);

    public abstract void Invoke(TTarget target, TSource source, ref MapperExecution execution);
}
