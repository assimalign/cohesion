using System.Collections.Generic;

using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Tests;

/// <summary>
/// One running instance of a resource, with no actions of its own.
/// </summary>
internal sealed class TestResource : IApplicationResourceInstance
{
    public TestResource(ProviderName provider, ResourceName name)
    {
        Provider = provider;
        Name = name;
    }

    public ProviderName Provider { get; }

    public ResourceName Name { get; }

    public InstanceKey Key { get; } = InstanceKey.New();

    public IReadOnlyCollection<IApplicationInstanceAction> Actions => [];

    public IReadOnlyCollection<IApplicationHealthContributor> HealthContributors => [];
}
