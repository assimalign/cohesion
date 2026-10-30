using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Tests;

/// <summary>
/// One instance of the <c>Web/orders-api</c> resource unless a test says otherwise.
/// </summary>
internal sealed class TestResource : IApplicationResourceInstance
{
    public TestResource(params IApplicationInstanceAction[] actions)
    {
        Actions = actions;
    }

    public ProviderName Provider { get; init; } = "Web";

    public ResourceName Name { get; init; } = "orders-api";

    public InstanceKey Key { get; init; } = InstanceKey.New();

    public DeploymentId? Deployment { get; init; }

    public IReadOnlyCollection<IApplicationInstanceAction> Actions { get; init; }

    public IReadOnlyCollection<IApplicationHealthContributor> HealthContributors { get; init; } = [];
}
