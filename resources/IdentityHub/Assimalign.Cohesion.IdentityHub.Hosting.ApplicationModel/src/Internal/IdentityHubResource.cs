using System.Collections.Generic;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.IdentityHub.Hosting.ApplicationModel.Internal;

/// <summary>
/// One running instance of an IdentityHub resource: the application this process runs, with the actions
/// the application added.
/// </summary>
internal sealed class IdentityHubResource : IApplicationInstance
{
    public IdentityHubResource(ResourceName name, DeploymentId? deployment, IEnumerable<IApplicationInstanceAction> addedActions)
    {
        Name = name;
        Deployment = deployment;
        Actions = [.. addedActions];
    }

    public ProviderName Provider { get; } = "IdentityHub";

    public ResourceName Name { get; }

    public InstanceKey Key { get; } = InstanceKey.New();

    public DeploymentId? Deployment { get; }

    public IReadOnlyCollection<IApplicationInstanceAction> Actions { get; }

    public IReadOnlyCollection<IApplicationInstanceHealthContributor> HealthContributors { get; } = [];
}
