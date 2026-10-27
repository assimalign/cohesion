using System.Collections.Generic;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

/// <summary>
/// One instance of the <c>Web/orders-api</c> resource unless a test says otherwise.
/// </summary>
internal sealed class TestResource : IResource
{
    public TestResource(params IResourceAction[] actions)
    {
        Actions = actions;
    }

    public ProviderName Provider { get; init; } = "Web";

    public ResourceName Name { get; init; } = "orders-api";

    public ResourceInstanceKey Key { get; init; } = ResourceInstanceKey.New();

    public IReadOnlyCollection<IResourceAction> Actions { get; init; }

    public IReadOnlyCollection<IResourceHealthContributor> HealthContributors { get; init; } = [];
}
