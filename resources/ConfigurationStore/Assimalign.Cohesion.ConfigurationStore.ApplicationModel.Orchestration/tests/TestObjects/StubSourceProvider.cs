using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ConfigurationStore.ApplicationModel.Orchestration.Tests;

/// <summary>
/// An application-registered source provider that is not a ConfigurationStore provider, so a test
/// can check that <c>UseConfigurationStore</c> never replaces a registration it did not make.
/// </summary>
internal sealed class StubSourceProvider : IResourceSourceProvider
{
    public string? ResourceKind => null;
}
