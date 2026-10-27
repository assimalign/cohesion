using System.Collections.Generic;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// One application resource as manifest data.
/// </summary>
/// <param name="Provider">The resource's provider.</param>
/// <param name="Name">The resource's name, unique among its set's resources of the provider.</param>
/// <param name="Dependencies">The resources it depends on, in declaration order.</param>
public sealed record ApplicationResourceManifest(
    ProviderName Provider,
    ResourceName Name,
    IReadOnlyList<ApplicationResourceDependency> Dependencies);
