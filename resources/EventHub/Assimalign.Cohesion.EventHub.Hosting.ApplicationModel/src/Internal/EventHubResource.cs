using System.Collections.Generic;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.EventHub.Hosting.ApplicationModel.Internal;

/// <summary>
/// One running instance of an EventHub resource: the application this process runs, with the actions
/// the application added.
/// </summary>
internal sealed class EventHubResource : IApplicationInstance
{
    public EventHubResource(ResourceName name, DeploymentId? deployment, IEnumerable<IApplicationInstanceAction> addedActions)
    {
        Name = name;
        Deployment = deployment;
        Actions = [.. addedActions];
    }

    public ProviderName Provider { get; } = "EventHub";

    public ResourceName Name { get; }

    public InstanceKey Key { get; } = InstanceKey.New();

    public DeploymentId? Deployment { get; }

    public IReadOnlyCollection<IApplicationInstanceAction> Actions { get; }

    public IReadOnlyCollection<IApplicationInstanceHealthContributor> HealthContributors { get; } = [];
}
