using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes the single resource container compiled from a v1 realization plan.
/// </summary>
public sealed record ContainerSpec
{
    /// <summary>Initializes a container specification.</summary>
    /// <param name="name">The container name.</param>
    /// <param name="artifact">The deployable artifact reference.</param>
    /// <param name="ports">Endpoint-to-container-port bindings.</param>
    /// <param name="mounts">Manifest-mount-to-container-path bindings.</param>
    /// <param name="environment">Environment variables supplied to the container.</param>
    /// <param name="probes">One-to-one mappings of the manifest probes.</param>
    /// <exception cref="ArgumentNullException">Any collection argument is <see langword="null"/>.</exception>
    public ContainerSpec(
        string name,
        ArtifactRef artifact,
        IReadOnlyList<PortBinding> ports,
        IReadOnlyList<MountBinding> mounts,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<ProbeMapping> probes)
    {
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(mounts);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(probes);

        Name = name;
        Artifact = artifact;
        Ports = Copy(ports);
        Mounts = Copy(mounts);
        Probes = Copy(probes);

        var environmentCopy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string value) in environment)
        {
            environmentCopy.Add(key, value);
        }

        Environment = new ReadOnlyDictionary<string, string>(environmentCopy);
    }

    /// <summary>Gets the container name.</summary>
    public string Name { get; }

    /// <summary>Gets the deployable artifact reference.</summary>
    public ArtifactRef Artifact { get; }

    /// <summary>Gets the immutable endpoint-to-port bindings.</summary>
    public IReadOnlyList<PortBinding> Ports { get; }

    /// <summary>Gets the immutable manifest-mount-to-path bindings.</summary>
    public IReadOnlyList<MountBinding> Mounts { get; }

    /// <summary>Gets the immutable environment-variable map.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; }

    /// <summary>Gets the immutable probe mappings.</summary>
    public IReadOnlyList<ProbeMapping> Probes { get; }

    private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> source)
    {
        var copy = new T[source.Count];
        for (int index = 0; index < source.Count; index++)
        {
            copy[index] = source[index];
        }

        return new ReadOnlyCollection<T>(copy);
    }
}
