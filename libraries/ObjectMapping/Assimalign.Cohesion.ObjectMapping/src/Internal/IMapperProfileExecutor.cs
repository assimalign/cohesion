namespace Assimalign.Cohesion.ObjectMapping.Internal;

/// <summary>Executes built-in profiles without materializing a public context for built-in actions.</summary>
internal interface IMapperProfileExecutor
{
    void Invoke(ref MapperExecution execution);
}
