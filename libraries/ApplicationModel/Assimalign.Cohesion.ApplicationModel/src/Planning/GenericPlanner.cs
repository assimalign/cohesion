using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Maps the generic manifest traits to the v1 platform-neutral realization-plan contract.
/// Resource-area planners inherit this behavior unless their kind requires additional
/// platform-neutral decisions.
/// </summary>
public static class GenericPlanner
{
    private static readonly IReadOnlyList<string> _emptyCommand = Array.Empty<string>();

    /// <summary>Creates a plan from generic resource-manifest traits.</summary>
    /// <param name="context">The manifest, typed options, environment, and references to plan.</param>
    /// <returns>The platform-neutral resource plan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The manifest contains contradictory workload and mount facts, or an invalid probe shape.
    /// </exception>
    public static ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ResourceManifest manifest = context.Manifest;
        WorkloadKind workloadKind = manifest.Lifecycle.Workload;
        bool hasVolumeMount = HasVolumeMount(manifest.Mounts);

        if (hasVolumeMount && workloadKind is not WorkloadKind.StatefulSet)
        {
            throw new InvalidOperationException(
                $"Resource '{manifest.Name}' declares a Volume mount but lifecycle workload " +
                $"'{workloadKind}'. Volume mounts require an explicit StatefulSet workload.");
        }

        int replicas = context.Options.Replicas ?? manifest.Lifecycle.Replicas;

        var ports = new PortBinding[manifest.Endpoints.Count];
        var services = new List<ServiceSpec>(manifest.Endpoints.Count + (hasVolumeMount ? 1 : 0));
        var exposures = new List<ExposureSpec>();

        for (int index = 0; index < manifest.Endpoints.Count; index++)
        {
            ResourceManifestEndpoint endpoint = manifest.Endpoints[index];
            string serviceName = $"{manifest.Name}-{endpoint.Name}";

            ports[index] = new PortBinding(
                endpoint.Name,
                endpoint.ContainerPort,
                endpoint.Protocol,
                endpoint.Scheme,
                endpoint.Certificate ?? string.Empty);

            services.Add(new ServiceSpec(
                serviceName,
                endpoint.Name,
                endpoint.ContainerPort,
                endpoint.Protocol,
                Headless: false,
                Governing: false));

            if (endpoint.Public)
            {
                exposures.Add(new ExposureSpec(
                    $"{serviceName}-public",
                    endpoint.Name,
                    serviceName,
                    endpoint.Scheme,
                    endpoint.Protocol,
                    endpoint.ContainerPort));
            }
        }

        var mounts = new MountBinding[manifest.Mounts.Count];
        var volumes = new List<VolumeSpec>();

        for (int index = 0; index < manifest.Mounts.Count; index++)
        {
            ResourceManifestMount mount = manifest.Mounts[index];

            mounts[index] = new MountBinding(
                mount.Name,
                mount.ContainerPath,
                mount.Kind,
                mount.Source);

            if (mount.Kind is ResourceMountKind.Volume)
            {
                string? size = context.Options.Storage.Size ?? mount.Size;
                if (string.IsNullOrWhiteSpace(size))
                {
                    throw new InvalidOperationException(
                        $"Volume mount '{mount.Name}' on resource '{manifest.Name}' has no storage size.");
                }

                volumes.Add(new VolumeSpec(
                    mount.Name,
                    mount.Kind,
                    size,
                    PerReplicaClaim: true));
            }
        }

        if (hasVolumeMount)
        {
            services.Add(new ServiceSpec(
                $"{manifest.Name}-headless",
                Endpoint: null,
                Port: null,
                Protocol: "tcp",
                Headless: true,
                Governing: true));
        }

        IReadOnlyList<ProbeMapping> probes = MapProbes(manifest);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string name, string value) in manifest.EnvironmentVariables)
        {
            environment[name] = value;
        }

        environment[ResourceEnvironment.Application] = manifest.Application.ToString();
        environment[ResourceEnvironment.Resource] = manifest.Name.ToString();
        environment[ResourceEnvironment.Environment] = context.Environment.Name.ToString();

        var container = new ContainerSpec(
            manifest.Name.ToString(),
            ArtifactRef.Self,
            ports,
            mounts,
            environment,
            probes);

        var workload = new WorkloadSpec(
            workloadKind,
            replicas,
            StableIdentity: workloadKind is WorkloadKind.StatefulSet,
            ReadinessGate.For(workloadKind),
            manifest.Lifecycle.StopGraceSeconds,
            manifest.Lifecycle.RestartPolicy);

        return new ResourcePlan(
            ResourcePlan.CurrentSchema,
            manifest.Name,
            manifest.Kind,
            workload,
            container,
            volumes,
            services,
            exposures,
            new Dictionary<string, string>(StringComparer.Ordinal),
            new ControlPlaneSpec(
                manifest.ControlPlane.Endpoint,
                manifest.ControlPlane.Path));
    }

    private static bool HasVolumeMount(IReadOnlyList<ResourceManifestMount> mounts)
    {
        for (int index = 0; index < mounts.Count; index++)
        {
            if (mounts[index].Kind is ResourceMountKind.Volume)
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<ProbeMapping> MapProbes(ResourceManifest manifest)
    {
        var probes = new List<ProbeMapping>(3);
        AddProbe(probes, "readiness", manifest.Probes.Readiness, manifest.Name);
        AddProbe(probes, "liveness", manifest.Probes.Liveness, manifest.Name);
        AddProbe(probes, "startup", manifest.Probes.Startup, manifest.Name);
        return probes;
    }

    private static void AddProbe(
        ICollection<ProbeMapping> mappings,
        string role,
        ResourceManifestProbe? probe,
        ResourceName resource)
    {
        if (probe is null)
        {
            return;
        }

        int shapeCount = 0;
        ProbeKind kind = default;
        string? value = null;
        IReadOnlyList<string> command = _emptyCommand;

        if (probe.Http is not null)
        {
            shapeCount++;
            kind = ProbeKind.Http;
            value = probe.Http;
        }

        if (probe.Tcp is true)
        {
            shapeCount++;
            kind = ProbeKind.Tcp;
        }

        if (probe.Exec is not null)
        {
            shapeCount++;
            kind = ProbeKind.Exec;
            command = probe.Exec;
        }

        if (probe.Grpc is not null)
        {
            shapeCount++;
            kind = ProbeKind.Grpc;
            value = probe.Grpc;
        }

        if (probe.None is true)
        {
            shapeCount++;
            kind = ProbeKind.None;
        }

        if (shapeCount is not 1)
        {
            throw new InvalidOperationException(
                $"The {role} probe for resource '{resource}' must declare exactly one probe mechanism.");
        }

        mappings.Add(new ProbeMapping(role, probe.Endpoint, kind, value, command));
    }
}
