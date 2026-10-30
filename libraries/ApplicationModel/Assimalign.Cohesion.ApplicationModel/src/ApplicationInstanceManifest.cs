using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// What a resource instance publishes about itself when its host starts: its identity, the
/// deployment that started it, and the actions its control plane exposes.
/// </summary>
/// <param name="Provider">The resource's provider.</param>
/// <param name="Name">The resource's name.</param>
/// <param name="Key">The instance's key.</param>
/// <param name="Deployment">
/// The deployment that started the instance, as its environment named it; <see langword="null"/> for
/// an instance no gateway deployed. A gateway counts a deployment ready only once an instance that
/// repeats its id publishes.
/// </param>
/// <param name="Actions">The instance's actions, ordered by name.</param>
public sealed record ApplicationInstanceManifest(
    ProviderName Provider,
    ResourceName Name,
    InstanceKey Key,
    DeploymentId? Deployment,
    IReadOnlyList<ApplicationActionDescriptor> Actions);
