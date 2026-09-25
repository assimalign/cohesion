using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes the declaration and portable code binding of an external model resource.
/// </summary>
public sealed class ApplicationModelExternalDocument
{
    /// <summary>Initializes a serialized external declaration.</summary>
    /// <param name="application">The application that owns the external resource.</param>
    /// <param name="referencedEndpoints">The endpoint names consumed by the declaring model.</param>
    /// <param name="optional">Whether an unresolved external may be skipped.</param>
    /// <param name="hasEmbeddedManifest">Whether the parent resource manifest came from the declaration.</param>
    /// <param name="closure">The embedded transitive manifest closure.</param>
    /// <param name="isRealized">Whether this external was realized into the exporting gateway.</param>
    /// <param name="binding">The portable binding kind: <c>unbound</c>, <c>static</c>, <c>file</c>, or <c>gateway</c>.</param>
    /// <param name="location">The file path or gateway URL used by the binding.</param>
    /// <param name="endpoints">The statically bound endpoints.</param>
    /// <exception cref="ArgumentException">
    /// A required string is empty or <paramref name="binding"/> is unsupported.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// A required string or collection is <see langword="null"/>.
    /// </exception>
    [JsonConstructor]
    public ApplicationModelExternalDocument(
        string application,
        IReadOnlyList<string> referencedEndpoints,
        bool optional,
        bool hasEmbeddedManifest,
        IReadOnlyList<ResourceManifest> closure,
        bool isRealized,
        string binding,
        string? location,
        IReadOnlyList<ApplicationModelExternalEndpointDocument> endpoints)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(application);
        ArgumentNullException.ThrowIfNull(referencedEndpoints);
        ArgumentNullException.ThrowIfNull(closure);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding);
        ArgumentNullException.ThrowIfNull(endpoints);
        if (!IsSupportedBinding(binding))
        {
            throw new ArgumentException(
                $"External binding kind '{binding}' is not supported.",
                nameof(binding));
        }

        Application = application;
        ReferencedEndpoints = CopyStrings(referencedEndpoints);
        Optional = optional;
        HasEmbeddedManifest = hasEmbeddedManifest;
        Closure = CopyManifests(closure);
        IsRealized = isRealized;
        Binding = binding;
        Location = location;
        Endpoints = CopyEndpoints(endpoints);
    }

    /// <summary>Gets the application that owns the external resource.</summary>
    public string Application { get; }

    /// <summary>Gets the endpoint names consumed by the declaring model.</summary>
    public IReadOnlyList<string> ReferencedEndpoints { get; }

    /// <summary>Gets whether an unresolved external may be skipped.</summary>
    public bool Optional { get; }

    /// <summary>Gets whether the declaration contains its original target manifest.</summary>
    public bool HasEmbeddedManifest { get; }

    /// <summary>Gets the embedded transitive manifest closure.</summary>
    public IReadOnlyList<ResourceManifest> Closure { get; }

    /// <summary>Gets whether this external was realized by the describing gateway.</summary>
    public bool IsRealized { get; }

    /// <summary>Gets the portable binding kind.</summary>
    public string Binding { get; }

    /// <summary>Gets the file path or gateway URL used by the binding, when applicable.</summary>
    public string? Location { get; }

    /// <summary>Gets the statically bound endpoints.</summary>
    public IReadOnlyList<ApplicationModelExternalEndpointDocument> Endpoints { get; }

    private static bool IsSupportedBinding(string binding)
        => string.Equals(binding, "unbound", StringComparison.Ordinal)
            || string.Equals(binding, "static", StringComparison.Ordinal)
            || string.Equals(binding, "file", StringComparison.Ordinal)
            || string.Equals(binding, "gateway", StringComparison.Ordinal);

    private static IReadOnlyList<string> CopyStrings(IReadOnlyList<string> source)
    {
        var copy = new string[source.Count];
        for (int index = 0; index < copy.Length; index++)
        {
            copy[index] = source[index]
                ?? throw new ArgumentException("External endpoint names must not contain null entries.", nameof(source));
        }

        return new ReadOnlyCollection<string>(copy);
    }

    private static IReadOnlyList<ResourceManifest> CopyManifests(IReadOnlyList<ResourceManifest> source)
    {
        var copy = new ResourceManifest[source.Count];
        for (int index = 0; index < copy.Length; index++)
        {
            copy[index] = ResourceManifestSnapshot.Create(
                source[index]
                    ?? throw new ArgumentException(
                        "An external closure must not contain null entries.",
                        nameof(source)));
        }

        return new ReadOnlyCollection<ResourceManifest>(copy);
    }

    private static IReadOnlyList<ApplicationModelExternalEndpointDocument> CopyEndpoints(
        IReadOnlyList<ApplicationModelExternalEndpointDocument> source)
    {
        var copy = new ApplicationModelExternalEndpointDocument[source.Count];
        for (int index = 0; index < copy.Length; index++)
        {
            copy[index] = source[index]
                ?? throw new ArgumentException(
                    "External endpoint bindings must not contain null entries.",
                    nameof(source));
        }

        return new ReadOnlyCollection<ApplicationModelExternalEndpointDocument>(copy);
    }
}
