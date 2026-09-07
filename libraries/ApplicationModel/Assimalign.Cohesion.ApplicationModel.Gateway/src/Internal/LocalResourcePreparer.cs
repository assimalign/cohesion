using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal sealed class LocalResourcePreparer
{
    internal const string ResourceReadyMarker = "cohesion-resource: ready";

    private readonly LocalPortStore _ports;
    private readonly LocalMountMaterializer _mounts;
    private readonly LocalGatewayOptions _options;

    public LocalResourcePreparer(
        LocalPortStore ports,
        LocalMountMaterializer mounts,
        LocalGatewayOptions options)
    {
        _ports = ports;
        _mounts = mounts;
        _options = options;
    }

    public async Task<LocalResourceConfiguration> PrepareAsync(
        IResourceControlContext context,
        LocalPlanCompilation compilation,
        CancellationToken cancellationToken)
    {
        IApplicationResource resource = context.Resource;
        IExecutableArtifact artifact = compilation.Artifact;
        var environment = new Dictionary<string, string>(
            compilation.Environment,
            StringComparer.Ordinal);

        IReadOnlyList<ResourceEndpoint> declaredEndpoints = GetDeclaredEndpoints(resource);
        IReadOnlyList<ResourceEndpoint> observedEndpoints = await _ports.ResolveAsync(
            context.Model.Name,
            resource.Name,
            declaredEndpoints,
            environment,
            cancellationToken).ConfigureAwait(false);

        await _mounts.MaterializeAsync(
            context.Model.Name,
            resource,
            compilation.Plan,
            compilation.Inputs,
            environment,
            cancellationToken).ConfigureAwait(false);

        if (resource is LocalExecutableResource localExecutable)
        {
            bool markerIsReadiness = localExecutable.ReadyMarker is not null
                && (localExecutable.ReadinessProbe is null
                    || localExecutable.ReadinessProbe.Kind == ProbeKind.None);

            return new LocalResourceConfiguration(
                resource,
                artifact,
                environment,
                observedEndpoints,
                Normalize(localExecutable.StartupProbe),
                Normalize(localExecutable.ReadinessProbe),
                Normalize(localExecutable.LivenessProbe),
                localExecutable.ReadyMarker,
                markerIsReadiness,
                localExecutable.RestartPolicy,
                _options.StopGrace,
                useStopEvent: false);
        }

        if (resource is not IManifestResource manifestResource)
        {
            throw new InvalidOperationException(
                $"Executable resource '{resource.Name}' has no resource manifest. Add it with AddExecutable(name, path, options).");
        }

        ResourceManifest manifest = manifestResource.Manifest;
        IProbeSpec defaultReadinessProbe = CreateDefaultControlPlaneProbe(
            manifest.ControlPlane,
            "readyz");
        IProbeSpec defaultLivenessProbe = CreateDefaultControlPlaneProbe(
            manifest.ControlPlane,
            "livez");

        return new LocalResourceConfiguration(
            resource,
            artifact,
            environment,
            observedEndpoints,
            MapManifestProbe(manifest.Probes.Startup, defaultReadinessProbe),
            MapManifestProbe(manifest.Probes.Readiness, defaultReadinessProbe),
            MapManifestProbe(manifest.Probes.Liveness, defaultLivenessProbe),
            ResourceReadyMarker,
            markerIsReadiness: false,
            ParseRestartPolicy(manifest.Lifecycle.RestartPolicy, resource.Name),
            TimeSpan.FromSeconds(manifest.Lifecycle.StopGraceSeconds),
            useStopEvent: true);
    }

    public async Task UninstallAsync(
        IResourceControlContext context,
        CancellationToken cancellationToken)
    {
        await _mounts
            .DeleteAsync(context.Model.Name, context.Resource.Name, cancellationToken)
            .ConfigureAwait(false);
        await _ports
            .DeleteAsync(context.Model.Name, context.Resource.Name, cancellationToken)
            .ConfigureAwait(false);
    }

    private static IProbeSpec CreateDefaultControlPlaneProbe(
        ResourceManifestControlPlane controlPlane,
        string rolePath)
    {
        string path = controlPlane.Path.EndsWith("/", StringComparison.Ordinal)
            ? controlPlane.Path + rolePath
            : controlPlane.Path + "/" + rolePath;
        return ProbeSpec.Http(controlPlane.Endpoint, path);
    }

    private static IReadOnlyList<ResourceEndpoint> GetDeclaredEndpoints(IApplicationResource resource)
    {
        if (resource is IManifestResource manifestResource)
        {
            IReadOnlyList<ResourceManifestEndpoint> manifestEndpoints = manifestResource.Manifest.Endpoints;
            var endpoints = new ResourceEndpoint[manifestEndpoints.Count];
            for (int index = 0; index < endpoints.Length; index++)
            {
                ResourceManifestEndpoint endpoint = manifestEndpoints[index];
                endpoints[index] = new ResourceEndpoint(
                    endpoint.Name,
                    endpoint.Scheme,
                    endpoint.ContainerPort,
                    endpoint.Public);
            }

            return endpoints;
        }

        return resource is IEndpointResource endpointResource
            ? endpointResource.Endpoints
            : Array.Empty<ResourceEndpoint>();
    }

    private static IProbeSpec? MapManifestProbe(
        ResourceManifestProbe? probe,
        IProbeSpec defaultProbe)
    {
        if (probe is null)
        {
            return defaultProbe;
        }

        if (probe.None is true)
        {
            return null;
        }

        if (probe.Http is not null)
        {
            return ProbeSpec.Http(probe.Endpoint!, probe.Http);
        }

        if (probe.Tcp is true)
        {
            return ProbeSpec.Tcp(probe.Endpoint!);
        }

        if (probe.Exec is not null)
        {
            string[] arguments = new string[probe.Exec.Count - 1];
            for (int index = 0; index < arguments.Length; index++)
            {
                arguments[index] = probe.Exec[index + 1];
            }

            return ProbeSpec.Exec(probe.Exec[0], arguments);
        }

        if (probe.Grpc is not null)
        {
            throw new NotSupportedException(
                "The local gateway does not yet support gRPC health probes. Use an HTTP, TCP, exec, or none probe.");
        }

        throw new InvalidDataException("The resource manifest contains an invalid probe declaration.");
    }

    private static IProbeSpec? Normalize(IProbeSpec? probe)
        => probe is null || probe.Kind == ProbeKind.None ? null : probe;

    private static RestartPolicy ParseRestartPolicy(string value, ResourceName resource)
    {
        if (Enum.TryParse(value, ignoreCase: false, out RestartPolicy policy)
            && Enum.IsDefined(policy))
        {
            return policy;
        }

        throw new InvalidDataException(
            $"Resource '{resource}' declares unsupported restart policy '{value}'. Expected OnFailure, Always, or Never.");
    }
}
