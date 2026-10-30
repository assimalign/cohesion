using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Hosting;
using Assimalign.Cohesion.Web.Cors;

namespace Assimalign.Cohesion.Web.Hosting.ApplicationModel.Internal;

/// <summary>
/// One running instance of a Web resource: the application this process serves, with an action for
/// each setting the application registered that the resource knows how to expose, followed by the
/// actions the application added.
/// </summary>
internal sealed class WebResource : IApplicationResourceInstance
{
    public WebResource(ResourceName name, DeploymentId? deployment, Uri controlPlaneEndpoint, IEnumerable<IWebApplicationSetting> settings, IEnumerable<IApplicationInstanceAction> addedActions)
    {
        Name = name;
        Deployment = deployment;
        ControlPlaneEndpoint = controlPlaneEndpoint;
        Actions = [.. CreateActions(settings), .. addedActions];
    }

    public ProviderName Provider { get; } = "Web";

    public ResourceName Name { get; }

    public InstanceKey Key { get; } = InstanceKey.New();

    public DeploymentId? Deployment { get; }

    // Where the control-plane server listens, which the gateway calls.
    public Uri? ControlPlaneEndpoint { get; }

    public IReadOnlyCollection<IApplicationInstanceAction> Actions { get; }

    public IReadOnlyCollection<IApplicationHealthContributor> HealthContributors { get; } = [];

    // The last registration of a name is the one its feature uses, as UseCors does. A setting the
    // resource has no action for stays out of the manifest.
    private static IApplicationInstanceAction[] CreateActions(IEnumerable<IWebApplicationSetting> settings)
    {
        var actions = new List<IApplicationInstanceAction>();
        foreach (IWebApplicationSetting setting in settings.GroupBy(static setting => setting.Name).Select(static group => group.Last()))
        {
            if (setting is IWebApplicationSetting<CorsPolicy?> cors)
            {
                actions.Add(new WebCorsAction(cors));
            }
        }

        return [.. actions];
    }
}
