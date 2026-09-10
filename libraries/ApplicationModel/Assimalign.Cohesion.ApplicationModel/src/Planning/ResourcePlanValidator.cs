using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Validates a v1 resource plan against the manifest facts from which it was produced.
/// </summary>
public static class ResourcePlanValidator
{
    /// <summary>Validates a plan against its planning context, including typed overrides.</summary>
    /// <param name="plan">The plan to validate.</param>
    /// <param name="context">The immutable planning inputs used to produce the plan.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The plan violates the v1 contract.</exception>
    public static void Validate(ResourcePlan plan, PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Validate(plan, context.Manifest);

        int expectedReplicas = context.Options.Replicas ?? context.Manifest.Lifecycle.Replicas;
        Require(
            plan.Workload.Replicas == expectedReplicas,
            $"Plan replicas '{plan.Workload.Replicas}' do not match the requested value '{expectedReplicas}'.");

        for (int index = 0; index < context.Manifest.Mounts.Count; index++)
        {
            ResourceManifestMount mount = context.Manifest.Mounts[index];
            if (mount.Kind is not ResourceMountKind.Volume)
            {
                continue;
            }

            VolumeSpec volume = FindVolume(plan.Volumes, mount.Name, plan.Resource);
            string? expectedSize = context.Options.Storage.Size ?? mount.Size;
            Require(
                string.Equals(volume.Size, expectedSize, StringComparison.Ordinal),
                $"Plan volume '{volume.Name}' size '{volume.Size}' does not match requested size '{expectedSize}'.");
        }

        ValidateEnvironment(plan, context);
    }

    /// <summary>Validates a plan against its immutable resource manifest.</summary>
    /// <param name="plan">The plan to validate.</param>
    /// <param name="manifest">The source resource manifest.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The plan violates the v1 contract.</exception>
    public static void Validate(ResourcePlan plan, ResourceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(manifest);

        Require(
            string.Equals(plan.Schema, ResourcePlan.CurrentSchema, StringComparison.Ordinal),
            $"Plan schema '{plan.Schema}' is not supported. Expected '{ResourcePlan.CurrentSchema}'.");
        Require(
            string.Equals(manifest.Schema, ResourceManifest.SchemaV1, StringComparison.Ordinal),
            $"Manifest schema '{manifest.Schema}' is not supported. Expected '{ResourceManifest.SchemaV1}'.");
        Require(
            plan.Resource.Equals(manifest.Name),
            $"Plan resource '{plan.Resource}' does not match manifest resource '{manifest.Name}'.");
        Require(
            string.Equals(plan.Kind, manifest.Kind, StringComparison.Ordinal),
            $"Plan kind '{plan.Kind}' does not match manifest kind '{manifest.Kind}'.");
        Require(
            plan.Workload.Kind == manifest.Lifecycle.Workload,
            $"Plan workload kind '{plan.Workload.Kind}' does not match manifest workload " +
            $"'{manifest.Lifecycle.Workload}'.");

        Require(plan.Workload.Replicas > 0, "Plan replicas must be greater than zero.");
        if (manifest.Lifecycle.MaxReplicas is int maxReplicas)
        {
            Require(
                plan.Workload.Replicas <= maxReplicas,
                $"Plan replicas '{plan.Workload.Replicas}' exceed manifest maxReplicas '{maxReplicas}'.");
        }

        Require(
            plan.Workload.StableIdentity == (plan.Workload.Kind is WorkloadKind.StatefulSet),
            "StableIdentity must be true exactly when the workload kind is StatefulSet.");
        Require(
            plan.Workload.StopGraceSeconds == manifest.Lifecycle.StopGraceSeconds,
            $"Plan stop grace '{plan.Workload.StopGraceSeconds}' does not match manifest stop grace " +
            $"'{manifest.Lifecycle.StopGraceSeconds}'.");
        Require(plan.Workload.StopGraceSeconds > 0, "Plan stop grace must be greater than zero.");
        ValidateRestartPolicy(plan.Workload, manifest.Lifecycle);

        ValidateGate(plan.Workload);

        Require(
            plan.Container.Artifact == ArtifactRef.Self,
            $"Plan artifact '{plan.Container.Artifact}' is not supported. " +
            $"'{ArtifactRef.Self}' is the only artifact reference in {ResourcePlan.CurrentSchema}.");
        Require(
            string.Equals(plan.Container.Name, manifest.Name.ToString(), StringComparison.Ordinal),
            $"Plan container '{plan.Container.Name}' does not match resource '{manifest.Name}'.");

        ValidateControlPlane(plan.ControlPlane, manifest.ControlPlane);
        ValidateEndpointBindings(plan, manifest);
        ValidateMountBindings(plan, manifest);
        ValidateVolumes(plan, manifest);
        ValidateServicesAndExposures(plan, manifest);
        ValidateProbes(plan, manifest);
    }

    private static void ValidateGate(WorkloadSpec workload)
    {
        ReadinessGate expected = ReadinessGate.For(workload.Kind);
        Require(
            SetEquals(workload.Gate.Terminals, expected.Terminals),
            $"Workload '{workload.Kind}' has an invalid readiness terminal set.");
        Require(
            SetEquals(workload.Gate.Satisfying, expected.Satisfying),
            $"Workload '{workload.Kind}' has an invalid readiness satisfying set.");
    }

    private static void ValidateRestartPolicy(
        WorkloadSpec workload,
        ResourceManifestLifecycle lifecycle)
    {
        string restartPolicy = RequireNotNull(
            workload.RestartPolicy,
            "Plan restart policy must not be null.");
        if (IsLegacyOmitted(restartPolicy))
        {
            return;
        }

        Require(
            !string.IsNullOrWhiteSpace(restartPolicy),
            "Plan restart policy must not be whitespace.");
        Require(
            IsSupportedRestartPolicy(restartPolicy),
            $"Plan restart policy '{restartPolicy}' is not supported; expected 'OnFailure', " +
            "'Always', or 'Never'.");
        Require(
            string.Equals(restartPolicy, lifecycle.RestartPolicy, StringComparison.Ordinal),
            $"Plan restart policy '{restartPolicy}' does not match manifest restart policy " +
            $"'{lifecycle.RestartPolicy}'.");
    }

    private static void ValidateControlPlane(
        ControlPlaneSpec controlPlane,
        ResourceManifestControlPlane manifestControlPlane)
    {
        string endpoint = RequireNotNull(
            controlPlane.Endpoint,
            "Plan control-plane endpoint must not be null.");
        string path = RequireNotNull(
            controlPlane.Path,
            "Plan control-plane path must not be null.");
        bool endpointOmitted = IsLegacyOmitted(endpoint);
        bool pathOmitted = IsLegacyOmitted(path);

        Require(
            endpointOmitted == pathOmitted,
            "Plan control-plane endpoint and path must either both be declared or both be omitted by a legacy plan.");

        if (endpointOmitted)
        {
            return;
        }

        Require(
            !string.IsNullOrWhiteSpace(endpoint),
            "Plan control-plane endpoint must not be whitespace.");
        Require(
            !string.IsNullOrWhiteSpace(path),
            "Plan control-plane path must not be whitespace.");
        Require(
            string.Equals(endpoint, manifestControlPlane.Endpoint, StringComparison.Ordinal),
            $"Plan control-plane endpoint '{endpoint}' does not match manifest control-plane " +
            $"endpoint '{manifestControlPlane.Endpoint}'.");
        Require(
            string.Equals(path, manifestControlPlane.Path, StringComparison.Ordinal),
            $"Plan control-plane path '{path}' does not match manifest control-plane path " +
            $"'{manifestControlPlane.Path}'.");
    }

    private static void ValidateEnvironment(ResourcePlan plan, PlanContext context)
    {
        RequireEnvironmentValue(
            plan,
            ResourceEnvironment.Application,
            context.Manifest.Application.ToString());
        RequireEnvironmentValue(
            plan,
            ResourceEnvironment.Resource,
            context.Manifest.Name.ToString());
        RequireEnvironmentValue(
            plan,
            ResourceEnvironment.Environment,
            context.Environment.Name.ToString());

        foreach ((string name, string value) in context.Manifest.EnvironmentVariables)
        {
            if (IsFrozenIdentityVariable(name))
            {
                continue;
            }

            RequireEnvironmentValue(plan, name, value);
        }
    }

    private static void RequireEnvironmentValue(ResourcePlan plan, string name, string expected)
    {
        Require(
            plan.Container.Environment.TryGetValue(name, out string? actual) &&
            string.Equals(actual, expected, StringComparison.Ordinal),
            $"Plan environment variable '{name}' must have value '{expected}'.");
    }

    private static bool IsFrozenIdentityVariable(string name) =>
        string.Equals(name, ResourceEnvironment.Application, StringComparison.Ordinal) ||
        string.Equals(name, ResourceEnvironment.Resource, StringComparison.Ordinal) ||
        string.Equals(name, ResourceEnvironment.Environment, StringComparison.Ordinal);

    private static void ValidateEndpointBindings(ResourcePlan plan, ResourceManifest manifest)
    {
        Require(
            plan.Container.Ports.Count == manifest.Endpoints.Count,
            $"Plan port-binding count '{plan.Container.Ports.Count}' does not match manifest endpoint count " +
            $"'{manifest.Endpoints.Count}'.");

        var endpointNames = new HashSet<string>(StringComparer.Ordinal);
        bool? legacySchemesOmitted = null;
        for (int index = 0; index < manifest.Endpoints.Count; index++)
        {
            ResourceManifestEndpoint endpoint = manifest.Endpoints[index];
            Require(
                endpointNames.Add(endpoint.Name),
                $"Manifest endpoint '{endpoint.Name}' is declared more than once.");

            PortBinding binding = FindPort(plan.Container.Ports, endpoint.Name, plan.Resource);
            Require(
                binding.ContainerPort == endpoint.ContainerPort,
                $"Endpoint '{endpoint.Name}' is bound to port '{binding.ContainerPort}', not manifest port " +
                $"'{endpoint.ContainerPort}'.");
            Require(
                string.Equals(binding.Protocol, endpoint.Protocol, StringComparison.Ordinal),
                $"Endpoint '{endpoint.Name}' protocol '{binding.Protocol}' does not match manifest protocol " +
                $"'{endpoint.Protocol}'.");

            string scheme = RequireNotNull(
                binding.Scheme,
                $"Endpoint '{endpoint.Name}' scheme must not be null.");
            bool schemeOmitted = IsLegacyOmitted(scheme);
            legacySchemesOmitted ??= schemeOmitted;
            Require(
                legacySchemesOmitted == schemeOmitted,
                "Plan endpoint schemes must either all be declared or all be omitted by a legacy plan.");

            if (!schemeOmitted)
            {
                Require(
                    !string.IsNullOrWhiteSpace(scheme),
                    $"Endpoint '{endpoint.Name}' scheme must not be whitespace.");
                Require(
                    Uri.CheckSchemeName(scheme),
                    $"Endpoint '{endpoint.Name}' scheme '{scheme}' is not a valid URI scheme.");
                Require(
                    string.Equals(scheme, endpoint.Scheme, StringComparison.Ordinal),
                    $"Endpoint '{endpoint.Name}' scheme '{scheme}' does not match manifest scheme " +
                    $"'{endpoint.Scheme}'.");
            }
        }
    }

    private static void ValidateMountBindings(ResourcePlan plan, ResourceManifest manifest)
    {
        Require(
            plan.Container.Mounts.Count == manifest.Mounts.Count,
            $"Plan mount-binding count '{plan.Container.Mounts.Count}' does not match manifest mount count " +
            $"'{manifest.Mounts.Count}'.");

        var mountNames = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < manifest.Mounts.Count; index++)
        {
            ResourceManifestMount mount = manifest.Mounts[index];
            Require(
                mountNames.Add(mount.Name),
                $"Manifest mount '{mount.Name}' is declared more than once.");

            MountBinding binding = FindMount(plan.Container.Mounts, mount.Name, plan.Resource);
            Require(
                string.Equals(binding.ContainerPath, mount.ContainerPath, StringComparison.Ordinal),
                $"Mount '{mount.Name}' path '{binding.ContainerPath}' does not match manifest path " +
                $"'{mount.ContainerPath}'.");
            Require(
                binding.Kind == mount.Kind,
                $"Mount '{mount.Name}' kind '{binding.Kind}' does not match manifest kind '{mount.Kind}'.");
            Require(
                string.Equals(binding.Source, mount.Source, StringComparison.Ordinal),
                $"Mount '{mount.Name}' source does not match its manifest source.");
        }
    }

    private static void ValidateVolumes(ResourcePlan plan, ResourceManifest manifest)
    {
        int volumeMountCount = 0;
        for (int index = 0; index < manifest.Mounts.Count; index++)
        {
            if (manifest.Mounts[index].Kind is ResourceMountKind.Volume)
            {
                volumeMountCount++;
            }
        }

        Require(
            plan.Volumes.Count == volumeMountCount,
            $"Plan volume count '{plan.Volumes.Count}' does not match manifest Volume mount count " +
            $"'{volumeMountCount}'.");

        var volumeNames = new HashSet<string>(StringComparer.Ordinal);
        bool hasPerReplicaClaim = false;
        for (int index = 0; index < plan.Volumes.Count; index++)
        {
            VolumeSpec volume = plan.Volumes[index];
            Require(volumeNames.Add(volume.Name), $"Plan volume '{volume.Name}' is declared more than once.");
            Require(
                volume.Kind is ResourceMountKind.Volume,
                $"Plan volume '{volume.Name}' has unsupported mount kind '{volume.Kind}'.");
            Require(!string.IsNullOrWhiteSpace(volume.Size), $"Plan volume '{volume.Name}' has no storage size.");
            Require(
                volume.PerReplicaClaim,
                $"Plan volume '{volume.Name}' must be declared as a per-replica claim in {ResourcePlan.CurrentSchema}.");

            hasPerReplicaClaim = true;
            ResourceManifestMount mount = FindManifestMount(manifest.Mounts, volume.Name, plan.Resource);
            Require(
                mount.Kind is ResourceMountKind.Volume,
                $"Plan volume '{volume.Name}' does not bind a manifest Volume mount.");
        }

        Require(
            (plan.Workload.Kind is WorkloadKind.StatefulSet) == hasPerReplicaClaim,
            "Workload kind StatefulSet must be used if and only if the plan declares a per-replica claim.");
    }

    private static void ValidateServicesAndExposures(ResourcePlan plan, ResourceManifest manifest)
    {
        int expectedExposureCount = 0;
        for (int index = 0; index < manifest.Endpoints.Count; index++)
        {
            ResourceManifestEndpoint endpoint = manifest.Endpoints[index];
            ServiceSpec service = FindEndpointService(plan.Services, endpoint.Name, plan.Resource);

            Require(!service.Headless, $"Endpoint service '{service.Name}' cannot be headless.");
            Require(!service.Governing, $"Endpoint service '{service.Name}' cannot govern workload identity.");
            Require(
                service.Port == endpoint.ContainerPort,
                $"Service '{service.Name}' port does not match endpoint '{endpoint.Name}'.");
            Require(
                string.Equals(service.Protocol, endpoint.Protocol, StringComparison.Ordinal),
                $"Service '{service.Name}' protocol does not match endpoint '{endpoint.Name}'.");

            if (endpoint.Public)
            {
                expectedExposureCount++;
                ExposureSpec exposure = FindExposure(plan.Exposures, endpoint.Name, plan.Resource);
                Require(
                    string.Equals(exposure.Service, service.Name, StringComparison.Ordinal),
                    $"Exposure '{exposure.Name}' does not target endpoint service '{service.Name}'.");
                Require(
                    string.Equals(exposure.Scheme, endpoint.Scheme, StringComparison.Ordinal),
                    $"Exposure '{exposure.Name}' scheme does not match endpoint '{endpoint.Name}'.");
                Require(
                    string.Equals(exposure.Protocol, endpoint.Protocol, StringComparison.Ordinal),
                    $"Exposure '{exposure.Name}' protocol does not match endpoint '{endpoint.Name}'.");
                Require(
                    exposure.Port == endpoint.ContainerPort,
                    $"Exposure '{exposure.Name}' port does not match endpoint '{endpoint.Name}'.");
            }
            else
            {
                Require(
                    CountExposures(plan.Exposures, endpoint.Name) is 0,
                    $"Private endpoint '{endpoint.Name}' must not have a public exposure.");
            }
        }

        Require(
            plan.Exposures.Count == expectedExposureCount,
            $"Plan exposure count '{plan.Exposures.Count}' does not match public endpoint count " +
            $"'{expectedExposureCount}'.");

        bool expectsGoverningService = plan.Volumes.Count > 0;
        int governingServiceCount = 0;
        int endpointServiceCount = 0;
        for (int index = 0; index < plan.Services.Count; index++)
        {
            ServiceSpec service = plan.Services[index];
            if (service.Endpoint is not null)
            {
                endpointServiceCount++;
                continue;
            }

            Require(
                service.Headless && service.Governing,
                $"Portless service '{service.Name}' must be a headless governing service.");
            Require(service.Port is null, $"Governing service '{service.Name}' must not declare a port.");
            governingServiceCount++;
        }

        Require(
            endpointServiceCount == manifest.Endpoints.Count,
            "A plan must declare exactly one service per manifest endpoint.");
        Require(
            governingServiceCount == (expectsGoverningService ? 1 : 0),
            expectsGoverningService
                ? "A plan with per-replica claims must declare exactly one headless governing service."
                : "A plan without per-replica claims must not declare a headless governing service.");
    }

    private static void ValidateProbes(ResourcePlan plan, ResourceManifest manifest)
    {
        int expectedCount = 0;
        ValidateProbe(plan, manifest, "readiness", manifest.Probes.Readiness, ref expectedCount);
        ValidateProbe(plan, manifest, "liveness", manifest.Probes.Liveness, ref expectedCount);
        ValidateProbe(plan, manifest, "startup", manifest.Probes.Startup, ref expectedCount);

        Require(
            plan.Container.Probes.Count == expectedCount,
            "Plan probes must map manifest probes one-to-one without additional roles.");
    }

    private static void ValidateProbe(
        ResourcePlan plan,
        ResourceManifest manifest,
        string role,
        ResourceManifestProbe? manifestProbe,
        ref int expectedCount)
    {
        int matches = 0;
        ProbeMapping? mapping = null;
        for (int index = 0; index < plan.Container.Probes.Count; index++)
        {
            ProbeMapping candidate = plan.Container.Probes[index];
            if (string.Equals(candidate.Role, role, StringComparison.Ordinal))
            {
                matches++;
                mapping = candidate;
            }
        }

        if (manifestProbe is null)
        {
            Require(matches is 0, $"Plan contains an undeclared {role} probe.");
            return;
        }

        expectedCount++;
        Require(matches is 1 && mapping is not null, $"Manifest {role} probe must map exactly once.");
        ProbeMapping actual = mapping!;
        Require(
            string.Equals(actual.Endpoint, manifestProbe.Endpoint, StringComparison.Ordinal),
            $"Plan {role} probe endpoint does not match the manifest.");

        if (actual.Endpoint is not null)
        {
            FindPort(plan.Container.Ports, actual.Endpoint, plan.Resource);
        }

        ProbeKind expectedKind;
        string? expectedValue = null;
        IReadOnlyList<string> expectedCommand = Array.Empty<string>();
        int shapeCount = 0;

        if (manifestProbe.Http is not null)
        {
            expectedKind = ProbeKind.Http;
            expectedValue = manifestProbe.Http;
            shapeCount++;
        }
        else
        {
            expectedKind = default;
        }

        if (manifestProbe.Tcp is true)
        {
            expectedKind = ProbeKind.Tcp;
            shapeCount++;
        }

        if (manifestProbe.Exec is not null)
        {
            expectedKind = ProbeKind.Exec;
            expectedCommand = manifestProbe.Exec;
            shapeCount++;
        }

        if (manifestProbe.Grpc is not null)
        {
            expectedKind = ProbeKind.Grpc;
            expectedValue = manifestProbe.Grpc;
            shapeCount++;
        }

        if (manifestProbe.None is true)
        {
            expectedKind = ProbeKind.None;
            shapeCount++;
        }

        Require(shapeCount is 1, $"Manifest {role} probe must declare exactly one mechanism.");
        Require(actual.Kind == expectedKind, $"Plan {role} probe mechanism does not match the manifest.");
        Require(
            string.Equals(actual.Value, expectedValue, StringComparison.Ordinal),
            $"Plan {role} probe value does not match the manifest.");
        Require(
            SequenceEqual(actual.Command, expectedCommand),
            $"Plan {role} exec command does not match the manifest.");
    }

    private static PortBinding FindPort(
        IReadOnlyList<PortBinding> bindings,
        string endpoint,
        ResourceName resource)
    {
        PortBinding? match = null;
        int matches = 0;
        for (int index = 0; index < bindings.Count; index++)
        {
            if (string.Equals(bindings[index].Endpoint, endpoint, StringComparison.Ordinal))
            {
                match = bindings[index];
                matches++;
            }
        }

        Require(matches is 1 && match is not null, $"Endpoint '{endpoint}' on resource '{resource}' must bind exactly once.");
        return match!;
    }

    private static MountBinding FindMount(
        IReadOnlyList<MountBinding> bindings,
        string mount,
        ResourceName resource)
    {
        MountBinding? match = null;
        int matches = 0;
        for (int index = 0; index < bindings.Count; index++)
        {
            if (string.Equals(bindings[index].Mount, mount, StringComparison.Ordinal))
            {
                match = bindings[index];
                matches++;
            }
        }

        Require(matches is 1 && match is not null, $"Mount '{mount}' on resource '{resource}' must bind exactly once.");
        return match!;
    }

    private static VolumeSpec FindVolume(
        IReadOnlyList<VolumeSpec> volumes,
        string name,
        ResourceName resource)
    {
        VolumeSpec? match = null;
        int matches = 0;
        for (int index = 0; index < volumes.Count; index++)
        {
            if (string.Equals(volumes[index].Name, name, StringComparison.Ordinal))
            {
                match = volumes[index];
                matches++;
            }
        }

        Require(matches is 1 && match is not null, $"Volume '{name}' on resource '{resource}' must be declared exactly once.");
        return match!;
    }

    private static ResourceManifestMount FindManifestMount(
        IReadOnlyList<ResourceManifestMount> mounts,
        string name,
        ResourceName resource)
    {
        ResourceManifestMount? match = null;
        int matches = 0;
        for (int index = 0; index < mounts.Count; index++)
        {
            if (string.Equals(mounts[index].Name, name, StringComparison.Ordinal))
            {
                match = mounts[index];
                matches++;
            }
        }

        Require(matches is 1 && match is not null, $"Volume '{name}' on resource '{resource}' must bind one manifest mount.");
        return match!;
    }

    private static ServiceSpec FindEndpointService(
        IReadOnlyList<ServiceSpec> services,
        string endpoint,
        ResourceName resource)
    {
        ServiceSpec? match = null;
        int matches = 0;
        for (int index = 0; index < services.Count; index++)
        {
            if (string.Equals(services[index].Endpoint, endpoint, StringComparison.Ordinal))
            {
                match = services[index];
                matches++;
            }
        }

        Require(matches is 1 && match is not null, $"Endpoint '{endpoint}' on resource '{resource}' must have exactly one service.");
        return match!;
    }

    private static ExposureSpec FindExposure(
        IReadOnlyList<ExposureSpec> exposures,
        string endpoint,
        ResourceName resource)
    {
        ExposureSpec? match = null;
        int matches = 0;
        for (int index = 0; index < exposures.Count; index++)
        {
            if (string.Equals(exposures[index].Endpoint, endpoint, StringComparison.Ordinal))
            {
                match = exposures[index];
                matches++;
            }
        }

        Require(matches is 1 && match is not null, $"Public endpoint '{endpoint}' on resource '{resource}' must have exactly one exposure.");
        return match!;
    }

    private static int CountExposures(IReadOnlyList<ExposureSpec> exposures, string endpoint)
    {
        int matches = 0;
        for (int index = 0; index < exposures.Count; index++)
        {
            if (string.Equals(exposures[index].Endpoint, endpoint, StringComparison.Ordinal))
            {
                matches++;
            }
        }

        return matches;
    }

    private static bool SetEquals<T>(IReadOnlyList<T> left, IReadOnlyList<T> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var values = new HashSet<T>(left);
        return values.Count == left.Count && values.SetEquals(right);
    }

    private static bool SequenceEqual<T>(IReadOnlyList<T> left, IReadOnlyList<T> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var comparer = EqualityComparer<T>.Default;
        for (int index = 0; index < left.Count; index++)
        {
            if (!comparer.Equals(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLegacyOmitted(string value) => value.Length is 0;

    private static bool IsSupportedRestartPolicy(string value) =>
        string.Equals(value, "OnFailure", StringComparison.Ordinal) ||
        string.Equals(value, "Always", StringComparison.Ordinal) ||
        string.Equals(value, "Never", StringComparison.Ordinal);

    private static string RequireNotNull(string? value, string message)
    {
        if (value is null)
        {
            throw new InvalidOperationException(message);
        }

        return value;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
