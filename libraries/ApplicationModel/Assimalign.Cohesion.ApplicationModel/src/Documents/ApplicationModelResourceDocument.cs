using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

using Assimalign.Cohesion.ApplicationModel.Internal;

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
    /// <param name="resourceGroup">The optional application-owned logical group.</param>
    /// <param name="surface">
    /// The resource's <c>cohesion/runtime/v1</c> runtime surface, which is attached to <see cref="Manifest"/>. When it
    /// is omitted, the surface already attached to <paramref name="manifest"/> is kept and serialized.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="name"/>, <paramref name="dependencies"/>, <paramref name="manifest"/>,
    /// or <paramref name="plan"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="System.IO.InvalidDataException"><paramref name="surface"/> violates the runtime-surface contract.</exception>
    /// <exception cref="JsonException"><paramref name="surface"/> is not a JSON document.</exception>
    [JsonConstructor]
    public ApplicationModelResourceDocument(
        string name,
        IReadOnlyList<string> dependencies,
        ResourceManifest manifest,
        ResourcePlan plan,
        ApplicationModelExternalDocument? external = null,
        string? resourceGroup = null,
        JsonElement? surface = null)
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
        if (surface is JsonElement described && described.ValueKind != JsonValueKind.Null)
        {
            Manifest = Manifest with { Surface = ResourceSurface.Parse(described.GetRawText()) };
        }

        if (Manifest.Surface is ResourceSurface attached)
        {
            using JsonDocument document = JsonDocument.Parse(attached.Export());
            Surface = document.RootElement.Clone();
        }

        Plan = plan;
        External = external;
        ResourceGroup = resourceGroup;
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

    /// <summary>Gets the optional logical resource group.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResourceGroup { get; }

    /// <summary>
    /// Gets the resource's runtime surface document, or <see langword="null"/> when its build described none. The
    /// manifest wire form cannot carry it, so the document carries it beside the manifest.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Surface { get; }
}
