using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// What a resource instance publishes about itself when its host starts: its identity and the
/// actions its control plane exposes.
/// </summary>
/// <param name="Provider">The resource's provider.</param>
/// <param name="Name">The resource's name.</param>
/// <param name="Key">The instance's key.</param>
/// <param name="Actions">The instance's actions, ordered by name.</param>
public sealed record ResourceManifest(
    ProviderName Provider,
    ResourceName Name,
    ResourceInstanceKey Key,
    IReadOnlyList<ResourceActionDescriptor> Actions);
