namespace Assimalign.Cohesion;

// Deviates from the repo RootNamespace rule: shared contracts retain their owning namespace.
/// <summary>Lists the supported composition seams without referencing their assemblies.</summary>
internal enum ComponentIntegrationTarget
{
    ServiceProviderBuilder = 0,
}
