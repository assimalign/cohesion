using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

internal sealed class ContainerResource : PlannedResource
{
    public ContainerResource(
        ApplicationName application,
        ResourceName name,
        string imageReference,
        ContainerResourceOptionsBuilder options)
        : base(CreateManifest(application, name, imageReference, options))
    {
    }

    private static ResourceManifest CreateManifest(
        ApplicationName application,
        ResourceName name,
        string imageReference,
        ContainerResourceOptionsBuilder options)
    {
        var endpoints = new ResourceManifestEndpoint[options.Endpoints.Count];
        for (int index = 0; index < endpoints.Length; index++)
        {
            ResourceEndpoint endpoint = options.Endpoints[index];
            endpoints[index] = new ResourceManifestEndpoint
            {
                Name = endpoint.Name,
                Scheme = endpoint.Scheme,
                Protocol = string.Equals(endpoint.Scheme, "udp", StringComparison.OrdinalIgnoreCase)
                    ? "udp"
                    : "tcp",
                ContainerPort = endpoint.Port,
                Public = endpoint.IsPublic,
            };
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string value) in options.Environment)
        {
            environment.Add(key, value);
        }

        return new ResourceManifest
        {
            Name = name,
            Kind = "Container",
            Application = application,
            ApplicationModel = "Assimalign.Cohesion.ApplicationModel.Gateway",
            Artifact = new ResourceManifestArtifact
            {
                Assembly = imageReference,
                Composable = false,
                Image = imageReference,
            },
            Endpoints = endpoints,
            Probes = new ResourceManifestProbes
            {
                Readiness = CreateProbe(options.ReadinessProbe),
                Startup = CreateProbe(options.StartupProbe),
                Liveness = CreateProbe(options.LivenessProbe),
            },
            // cohesion/resource/v1 requires a control-plane location. An opaque container
            // always has an explicit readiness probe, so gateways do not use this fallback.
            ControlPlane = new ResourceManifestControlPlane
            {
                Endpoint = endpoints[0].Name,
                Path = "/cohesion/v1",
            },
            EnvironmentVariables = environment,
            Lifecycle = new ResourceManifestLifecycle
            {
                Workload = WorkloadKind.Deployment,
                RestartPolicy = options.RestartPolicy.ToString(),
            },
        };
    }

    private static ResourceManifestProbe? CreateProbe(IProbeSpec? probe)
    {
        if (probe is null)
        {
            return null;
        }

        return probe.Kind switch
        {
            ProbeKind.Http => new ResourceManifestProbe
            {
                Endpoint = probe.Endpoint,
                Http = probe.Path,
            },
            ProbeKind.Tcp => new ResourceManifestProbe
            {
                Endpoint = probe.Endpoint,
                Tcp = true,
            },
            ProbeKind.Exec => new ResourceManifestProbe
            {
                Exec = CopyCommand(probe.Command),
            },
            ProbeKind.None => new ResourceManifestProbe
            {
                None = true,
            },
            _ => throw new InvalidOperationException(
                $"Container probe kind '{probe.Kind}' cannot be represented in the resource manifest."),
        };
    }

    private static string[] CopyCommand(IReadOnlyList<string> command)
    {
        var copy = new string[command.Count];
        for (int index = 0; index < copy.Length; index++)
        {
            copy[index] = command[index];
        }

        return copy;
    }
}
