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
    public WebResource(ResourceName name, IEnumerable<IWebApplicationSetting> settings, IEnumerable<IResourceAction> addedActions)
    {
        Name = name;
        Actions = [.. CreateActions(settings), .. addedActions];
    }

    public ProviderName Provider { get; } = "Web";

    public ResourceName Name { get; }

    public ResourceInstanceKey Key { get; } = ResourceInstanceKey.New();

    public IReadOnlyCollection<IResourceAction> Actions { get; }

    public IReadOnlyCollection<IResourceHealthContributor> HealthContributors { get; } = [];

    // The last registration of a name is the one its feature uses, as UseCors does. A setting the
    // resource has no action for stays out of the manifest.
    private static IResourceAction[] CreateActions(IEnumerable<IWebApplicationSetting> settings)
    {
        var actions = new List<IResourceAction>();
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
