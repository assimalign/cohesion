using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// One resource's dependency on another, and the permissions it needs there.
/// </summary>
/// <param name="Gateway">The gateway whose set declares the resource depended on.</param>
/// <param name="Provider">The provider of the resource depended on.</param>
/// <param name="Name">The name of the resource depended on.</param>
/// <param name="Permissions">
/// The permissions needed on it, without duplicates; empty for a dependency without access.
/// </param>
public sealed record ApplicationResourceDependency(
    GatewayName Gateway,
    ProviderName Provider,
    ResourceName Name,
    IReadOnlyList<ResourcePermission> Permissions);
