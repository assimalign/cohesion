using System.Collections.Generic;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.Scheduler.Hosting.ApplicationModel.Internal;

/// <summary>
/// One running instance of a Scheduler resource: the application this process runs, with the actions
/// the application added.
/// </summary>
internal sealed class SchedulerResource : IApplicationInstance
{
    public SchedulerResource(ResourceName name, DeploymentId? deployment, IEnumerable<IApplicationInstanceAction> addedActions)
    {
        Name = name;
        Deployment = deployment;
        Actions = [.. addedActions];
    }

    public ProviderName Provider { get; } = "Scheduler";

    public ResourceName Name { get; }

    public InstanceKey Key { get; } = InstanceKey.New();

    public DeploymentId? Deployment { get; }

    public IReadOnlyCollection<IApplicationInstanceAction> Actions { get; }

    public IReadOnlyCollection<IApplicationInstanceHealthContributor> HealthContributors { get; } = [];
}
