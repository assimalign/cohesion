using System.Collections.Generic;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// The resource types and actions one provider serves. The ResourceManager's catalog is the union
/// of its providers' manifests, closed per build.
/// </summary>
/// <param name="Types">The resource types.</param>
/// <param name="Actions">The actions on those types.</param>
public sealed record ResourceProviderManifest(IReadOnlyList<ResourceTypeManifest> Types, IReadOnlyList<ResourceActionManifest> Actions);
