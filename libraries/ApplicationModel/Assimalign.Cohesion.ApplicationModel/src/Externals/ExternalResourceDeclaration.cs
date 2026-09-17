using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Declares a resource at the boundary of a build-produced application closure.
/// </summary>
public sealed class ExternalResourceDeclaration
{
    /// <summary>Initializes a generated application-boundary declaration.</summary>
    /// <param name="name">The referenced resource name.</param>
    /// <param name="application">The application that owns the resource.</param>
    /// <param name="endpoints">The endpoint names consumed by the current closure.</param>
    /// <param name="optional">Whether an unresolved external may be skipped.</param>
    /// <param name="manifest">The embedded target manifest, when available.</param>
    /// <param name="closure">The target's embedded transitive manifest closure, when available.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="endpoints"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="closure"/> contains a <see langword="null"/> manifest.
    /// </exception>
    /// <exception cref="System.IO.InvalidDataException">
    /// An embedded manifest does not satisfy the resource-manifest contract.
    /// </exception>
    public ExternalResourceDeclaration(
        ResourceName name,
        ApplicationName application,
        IReadOnlyList<string> endpoints,
        bool optional,
        ResourceManifest? manifest = null,
        IReadOnlyList<ResourceManifest>? closure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        Name = name;
        Application = application;
        ReferencedEndpoints = Copy(endpoints);
        Optional = optional;
        Manifest = manifest is null ? null : ResourceManifestSnapshot.Create(manifest);
        Closure = CopyManifests(Manifest, closure ?? Array.Empty<ResourceManifest>());
        ManifestHash = Manifest is null ? null : ResourceManifestCanonicalizer.ComputeHash(Manifest);
        ClosureHash = ResourceManifestCanonicalizer.ComputeClosureHash(Closure);
    }

    /// <summary>Gets the referenced resource name.</summary>
    public ResourceName Name { get; }

    /// <summary>Gets the application that owns the external.</summary>
    public ApplicationName Application { get; }

    /// <summary>Gets the target resource kind, or <c>external</c> for a manifest-less declaration.</summary>
    public string Kind => Manifest?.Kind ?? "external";

    /// <summary>Gets the endpoint names consumed by the declaring closure.</summary>
    public IReadOnlyList<string> ReferencedEndpoints { get; }

    /// <summary>Gets whether an unresolved declaration may be skipped.</summary>
    public bool Optional { get; }

    /// <summary>Gets the embedded target manifest, when build-time metadata was available.</summary>
    public ResourceManifest? Manifest { get; }

    /// <summary>Gets the embedded transitive closure used by Local realization.</summary>
    public IReadOnlyList<ResourceManifest> Closure { get; }

    /// <summary>Gets the canonical target-manifest hash, when a manifest is embedded.</summary>
    public string? ManifestHash { get; }

    /// <summary>Gets the canonical hash of the embedded closure.</summary>
    public string ClosureHash { get; }

    private static IReadOnlyList<string> Copy(IReadOnlyList<string> source)
    {
        var copy = new string[source.Count];
        for (int index = 0; index < copy.Length; index++)
        {
            copy[index] = source[index];
        }

        return new ReadOnlyCollection<string>(copy);
    }

    private static IReadOnlyList<ResourceManifest> CopyManifests(
        ResourceManifest? root,
        IReadOnlyList<ResourceManifest> source)
    {
        var copy = new List<ResourceManifest>(source.Count + (root is null ? 0 : 1));
        var identities = new HashSet<(ApplicationName Application, ResourceName Resource)>();
        if (root is not null)
        {
            copy.Add(ResourceManifestSnapshot.Create(root));
            identities.Add((root.Application, root.Name));
        }

        for (int index = 0; index < source.Count; index++)
        {
            ResourceManifest manifest = source[index]
                ?? throw new ArgumentException(
                    "An external manifest closure must not contain null entries.",
                    nameof(source));
            if (identities.Add((manifest.Application, manifest.Name)))
            {
                copy.Add(ResourceManifestSnapshot.Create(manifest));
            }
        }

        return new ReadOnlyCollection<ResourceManifest>(copy);
    }
}
