namespace Assimalign.Cohesion.ObjectMapping.Internal;

/// <summary>Executes a profile whose declared types are already known at the call site.</summary>
internal interface IMapperProfileExecutor<TTarget, TSource>
{
    void Invoke(TTarget target, TSource source, ref MapperExecution execution);
}
