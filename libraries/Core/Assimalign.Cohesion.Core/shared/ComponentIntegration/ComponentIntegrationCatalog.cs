namespace Assimalign.Cohesion;

// Deviates from the repo RootNamespace rule: shared contracts retain their owning namespace.
/// <summary>Maps the closed catalog to metadata names used by consuming compilations.</summary>
internal static class ComponentIntegrationCatalog
{
    internal static string? GetTargetTypeName(ComponentIntegrationTarget target) => target switch
    {
        ComponentIntegrationTarget.ServiceProviderBuilder => "Assimalign.Cohesion.DependencyInjection.IServiceProviderBuilder",
        _ => null,
    };

    internal static string? GetMethodName(ComponentIntegrationMethod method) => method switch
    {
        ComponentIntegrationMethod.AddSingleton => "AddSingleton",
        ComponentIntegrationMethod.AddScoped => "AddScoped",
        ComponentIntegrationMethod.AddTransient => "AddTransient",
        _ => null,
    };
}
