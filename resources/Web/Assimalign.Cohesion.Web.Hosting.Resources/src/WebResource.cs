using System.Collections.Generic;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Web.Hosting.Resources.Internal;

namespace Assimalign.Cohesion.Web.Hosting.Resources;

/// <summary>
/// One running instance of a Web resource: the application this process serves, with the actions
/// its control plane exposes.
/// </summary>
/// <remarks>
/// Created by <c>builder.AddResource(name, ...)</c>. When the gateway that provisioned the process
/// names a manifest endpoint, the instance publishes its manifest before the application serves
/// traffic and restores the state the gateway answers with.
/// </remarks>
public sealed class WebResource : IResource
{
    internal WebResource(ResourceName name, WebCorsAction? cors)
    {
        Name = name;
        Cors = cors;
        Actions = cors is null ? [] : [cors];
    }

    /// <summary>
    /// Gets the resource's provider, <c>Web</c>.
    /// </summary>
    public ProviderName Provider { get; } = "Web";

    /// <inheritdoc/>
    public ResourceName Name { get; }

    /// <inheritdoc/>
    public ResourceInstanceKey Key { get; } = ResourceInstanceKey.New();

    /// <inheritdoc/>
    public IReadOnlyCollection<IResourceAction> Actions { get; }

    /// <inheritdoc/>
    public IReadOnlyCollection<IResourceHealthContributor> HealthContributors { get; } = [];

    /// <summary>
    /// Gets the CORS policy in effect, or <see langword="null"/> when the resource has no <c>cors</c> action.
    /// </summary>
    public WebCorsPolicy? CorsPolicy => Cors?.Current;

    internal WebCorsAction? Cors { get; }
}
