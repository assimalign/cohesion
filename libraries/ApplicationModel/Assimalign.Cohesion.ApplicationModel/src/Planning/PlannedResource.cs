using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;

using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Base class for manifest-backed resources. It preserves the legacy executable, endpoint,
/// and mount capability surfaces while using <see cref="GenericPlanner"/> as the inherited
/// realization-plan fallback.
/// </summary>
/// <remarks>
/// The resource also keeps what the application's code declares about it through
/// <see cref="ResourceConfigurationExtensions"/>: its deployment configuration and its platform extension data.
/// <see cref="IApplicationBuilder.Build"/> copies both into the resource's <see cref="ResourcePlan"/>.
/// </remarks>
public abstract class PlannedResource :
    IPlannedResource,
    IExecutableResource,
    IEndpointResource,
    IMountResource
{
    private readonly Dictionary<ResourceSectionPath, JsonNode?> _configuration = new();
    private readonly Dictionary<ResourceExtensionKey, JsonElement> _extensions = new();
    private IReadOnlyList<ResourceEndpoint> _endpoints = Array.Empty<ResourceEndpoint>();
    private IReadOnlyList<ResourceMount> _mounts = Array.Empty<ResourceMount>();

    /// <summary>Initializes a manifest-backed resource with optional deployer overrides.</summary>
    /// <param name="manifest">The build-produced resource manifest.</param>
    /// <param name="options">
    /// The typed deployer options, or <see langword="null"/> to use
    /// <see cref="ResourceOptions"/> defaults.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is <see langword="null"/>.</exception>
    protected PlannedResource(ResourceManifest manifest, IResourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        Options = options ?? new ResourceOptions();
        UpdateManifest(manifest);
    }

    /// <summary>
    /// Replaces the manifest snapshot and its derived resource capabilities.
    /// </summary>
    /// <param name="manifest">The updated build-produced resource manifest.</param>
    private protected void UpdateManifest(ResourceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        Manifest = ResourceManifestSnapshot.Create(manifest);

        var endpointCopy = new ResourceEndpoint[Manifest.Endpoints.Count];
        for (int index = 0; index < Manifest.Endpoints.Count; index++)
        {
            ResourceManifestEndpoint endpoint = Manifest.Endpoints[index];
            endpointCopy[index] = new ResourceEndpoint(
                endpoint.Name,
                endpoint.Scheme,
                endpoint.ContainerPort,
                endpoint.Public);
        }

        _endpoints = new ReadOnlyCollection<ResourceEndpoint>(endpointCopy);

        var mountCopy = new ResourceMount[Manifest.Mounts.Count];
        for (int index = 0; index < Manifest.Mounts.Count; index++)
        {
            ResourceManifestMount mount = Manifest.Mounts[index];
            mountCopy[index] = new ResourceMount(mount.Name, mount.ContainerPath, mount.Kind);
        }

        _mounts = new ReadOnlyCollection<ResourceMount>(mountCopy);
    }

    /// <inheritdoc />
    public ResourceName Name => Manifest.Name;

    /// <inheritdoc />
    public ResourceManifest Manifest { get; private set; } = null!;

    /// <inheritdoc />
    public virtual string PlannerName => nameof(GenericPlanner);

    /// <inheritdoc />
    public IResourceOptions Options { get; }

    /// <inheritdoc />
    public string Artifact => Manifest.Artifact.Assembly;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> EnvironmentVariables => Manifest.EnvironmentVariables;

    /// <inheritdoc />
    public IReadOnlyList<ResourceEndpoint> Endpoints => _endpoints;

    /// <inheritdoc />
    public IReadOnlyList<ResourceMount> Mounts => _mounts;

    /// <inheritdoc />
    public virtual ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!Manifest.Name.Equals(context.Manifest.Name))
        {
            throw new InvalidOperationException(
                $"Planning context resource '{context.Manifest.Name}' does not match '{Manifest.Name}'.");
        }

        return GenericPlanner.CreatePlan(context);
    }

    // Composes a section's patches in declaration order; a JSON null patch is kept, and resets the section.
    internal void Configure(ResourceSectionPath section, JsonNode? patch)
    {
        _configuration[section] = _configuration.TryGetValue(section, out JsonNode? existing)
            ? JsonMergePatchComposition.Compose(existing, patch)
            : patch?.DeepClone();
    }

    internal void SetExtension(ResourceExtensionKey key, JsonElement value) => _extensions[key] = value.Clone();

    internal bool TryGetExtension(ResourceExtensionKey key, out JsonElement value) => _extensions.TryGetValue(key, out value);

    // The deployment layer as the plan carries it, keyed by section path in declaration order.
    internal IReadOnlyDictionary<string, JsonElement> GetConfiguration()
    {
        var configuration = new Dictionary<string, JsonElement>(_configuration.Count, StringComparer.Ordinal);
        foreach ((ResourceSectionPath section, JsonNode? patch) in _configuration)
        {
            configuration.Add(section.Value, ToElement(patch));
        }

        return configuration;
    }

    internal IReadOnlyDictionary<string, JsonElement> GetExtensions()
    {
        var extensions = new Dictionary<string, JsonElement>(_extensions.Count, StringComparer.Ordinal);
        foreach ((ResourceExtensionKey key, JsonElement value) in _extensions)
        {
            extensions.Add(key.Value, value);
        }

        return extensions;
    }

    private static JsonElement ToElement(JsonNode? node)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            if (node is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                node.WriteTo(writer);
            }
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
