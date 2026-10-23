namespace Assimalign.Cohesion;

// Deviates from the repo RootNamespace rule: shared contracts retain their owning namespace.
/// <summary>Selects the signature emitted by one component-integration declaration.</summary>
internal enum ComponentIntegrationTemplate
{
    Factory = 0,
    Builder = 1,
    BuilderWithServiceProvider = 2,
    Custom = 3,
}
