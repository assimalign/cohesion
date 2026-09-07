using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Base class for manifest-backed resources. It preserves the legacy executable, endpoint,
/// and mount capability surfaces while using <see cref="GenericPlanner"/> as the inherited
/// realization-plan fallback.
/// </summary>
public abstract class PlannedResource :
    IPlannedResource,
    IExecutableResource,
    IEndpointResource,
    IMountResource
{
    private readonly IReadOnlyList<ResourceEndpoint> endpoints;
    private readonly IReadOnlyList<ResourceMount> mounts;

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

        Manifest = ResourceManifestSnapshot.Create(manifest);
        Options = options ?? new ResourceOptions();

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

        endpoints = new ReadOnlyCollection<ResourceEndpoint>(endpointCopy);

        var mountCopy = new ResourceMount[Manifest.Mounts.Count];
        for (int index = 0; index < Manifest.Mounts.Count; index++)
        {
            ResourceManifestMount mount = Manifest.Mounts[index];
            mountCopy[index] = new ResourceMount(mount.Name, mount.ContainerPath, mount.Kind);
        }

        mounts = new ReadOnlyCollection<ResourceMount>(mountCopy);
    }

    /// <inheritdoc />
    public ResourceName Name => Manifest.Name;

    /// <inheritdoc />
    public ResourceManifest Manifest { get; }

    /// <inheritdoc />
    public virtual string PlannerName => nameof(GenericPlanner);

    /// <inheritdoc />
    public IResourceOptions Options { get; }

    /// <inheritdoc />
    public string Artifact => Manifest.Artifact.Assembly;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> EnvironmentVariables => Manifest.EnvironmentVariables;

    /// <inheritdoc />
    public IReadOnlyList<ResourceEndpoint> Endpoints => endpoints;

    /// <inheritdoc />
    public IReadOnlyList<ResourceMount> Mounts => mounts;

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
}
