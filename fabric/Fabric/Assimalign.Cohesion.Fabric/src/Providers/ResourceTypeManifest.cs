using System.Collections.Generic;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// One resource type a provider serves.
/// </summary>
/// <param name="Type">The type.</param>
/// <param name="ApiVersions">The versions of the type the provider accepts.</param>
/// <param name="Tiers">For a composite, the roles of its private children, such as <c>web</c> and <c>store</c>; empty otherwise.</param>
public sealed record ResourceTypeManifest(ResourceTypeName Type, IReadOnlyList<ApiVersion> ApiVersions, IReadOnlyList<string> Tiers);
