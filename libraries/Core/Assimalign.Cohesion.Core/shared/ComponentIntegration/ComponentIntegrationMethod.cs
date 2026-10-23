namespace Assimalign.Cohesion;

// Deviates from the repo RootNamespace rule: shared contracts retain their owning namespace.
/// <summary>Lists supported registration methods on the service-provider builder.</summary>
internal enum ComponentIntegrationMethod
{
    AddSingleton = 0,
    AddScoped = 1,
    AddTransient = 2,
}
