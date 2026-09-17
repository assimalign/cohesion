using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal sealed class LocalExecutableResource : IExecutableResource, IEndpointResource
{
    public LocalExecutableResource(
        ResourceName name,
        string path,
        ExecutableResourceOptionsBuilder options)
    {
        Name = name;
        Path = path;
        ReadyMarker = options.ReadyMarker;
        ReadinessProbe = options.ReadinessProbe;
        StartupProbe = options.StartupProbe;
        LivenessProbe = options.LivenessProbe;
        RestartPolicy = options.RestartPolicy;

        var endpointCopy = new ResourceEndpoint[options.Endpoints.Count];
        for (int index = 0; index < endpointCopy.Length; index++)
        {
            endpointCopy[index] = options.Endpoints[index];
        }

        Endpoints = new ReadOnlyCollection<ResourceEndpoint>(endpointCopy);

        var environmentCopy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string value) in options.Environment)
        {
            environmentCopy.Add(key, value);
        }

        EnvironmentVariables = new ReadOnlyDictionary<string, string>(environmentCopy);
    }

    public ResourceName Name { get; }

    public string Path { get; }

    public string? ReadyMarker { get; }

    public IProbeSpec? ReadinessProbe { get; }

    public IProbeSpec? StartupProbe { get; }

    public IProbeSpec? LivenessProbe { get; }

    public RestartPolicy RestartPolicy { get; }

    public string Artifact => Path;

    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; }

    public IReadOnlyList<ResourceEndpoint> Endpoints { get; }
}
