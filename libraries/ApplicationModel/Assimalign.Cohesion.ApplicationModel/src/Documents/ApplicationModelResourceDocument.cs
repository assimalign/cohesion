using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes one resource in a serialized Cohesion application model.
/// </summary>
public sealed class ApplicationModelResourceDocument
{
    /// <summary>
    /// Initializes a serialized application-model resource.
    /// </summary>
    /// <param name="name">The resource name.</param>
    /// <param name="dependencies">The resource names that must be satisfied first.</param>
    /// <param name="manifest">The resource manifest.</param>
    /// <param name="plan">The platform-neutral realization plan.</param>
    /// <param name="external">The external declaration and binding, when this is an external resource.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="name"/>, <paramref name="dependencies"/>, <paramref name="manifest"/>,
    /// or <paramref name="plan"/> is <see langword="null"/>.
    /// </exception>
    [JsonConstructor]
    public ApplicationModelResourceDocument(
        string name,
        IReadOnlyList<string> dependencies,
        ResourceManifest manifest,
        ResourcePlan plan,
        ApplicationModelExternalDocument? external = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(plan);

        var dependencyCopy = new string[dependencies.Count];
        for (int index = 0; index < dependencyCopy.Length; index++)
        {
            dependencyCopy[index] = dependencies[index];
        }

        Name = name;
        Dependencies = new ReadOnlyCollection<string>(dependencyCopy);
        Manifest = ResourceManifestSnapshot.Create(manifest);
        Plan = plan;
        External = external;
    }

    /// <summary>Gets the resource name.</summary>
    public string Name { get; }

    /// <summary>Gets the resource names that must be satisfied first.</summary>
    public IReadOnlyList<string> Dependencies { get; }

    /// <summary>Gets the resource manifest.</summary>
    public ResourceManifest Manifest { get; }

    /// <summary>Gets the platform-neutral realization plan.</summary>
    public ResourcePlan Plan { get; }

    /// <summary>Gets the external declaration and binding, when this is an external resource.</summary>
    public ApplicationModelExternalDocument? External { get; }
}
