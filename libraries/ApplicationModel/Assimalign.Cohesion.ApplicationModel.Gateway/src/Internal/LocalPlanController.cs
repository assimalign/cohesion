using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Compiles the local subset of a resource plan and realizes it as a supervised process,
/// private claim directories, and loopback services.
/// </summary>
internal sealed class LocalPlanController : IApplicationResourceController
{
    private readonly LocalGatewayOptions _options;
    private readonly LocalResourcePreparer _preparer;
    private readonly LocalGatewayProcessSupervisor _supervisor;

    public LocalPlanController(
        LocalGatewayOptions options,
        LocalResourcePreparer preparer,
        LocalGatewayProcessSupervisor supervisor)
    {
        _options = options;
        _preparer = preparer;
        _supervisor = supervisor;
    }

    public bool CanRealize(ResourcePlan plan, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!string.Equals(plan.Schema, ResourcePlan.CurrentSchema, StringComparison.Ordinal))
        {
            reason = $"Local compiler does not support plan schema '{plan.Schema}'.";
            return false;
        }

        if (plan.Container.Artifact != ArtifactRef.Self)
        {
            reason = $"Local compiler supports only artifact '{ArtifactRef.Self}'.";
            return false;
        }

        if (plan.Workload.Replicas != 1)
        {
            reason = "Local compiler realizes exactly one process per resource and cannot realize " +
                $"replica count '{plan.Workload.Replicas}'.";
            return false;
        }

        foreach (ProbeMapping probe in plan.Container.Probes)
        {
            if (probe.Kind is ProbeKind.Grpc)
            {
                reason = "Local compiler does not support gRPC health probes. Use HTTP, TCP, exec, or none.";
                return false;
            }
        }

        reason = null;
        return true;
    }

    public async Task ReconcileAsync(
        IResourceControlContext context,
        CancellationToken cancellationToken = default)
    {
        IExecutableArtifact artifact = context.GetArtifact<IExecutableArtifact>();
        LocalPlanCompilation compilation = Compile(
            context.Plan,
            artifact,
            context.Inputs,
            context.ObservedDependencies,
            _options);
        LocalResourceConfiguration configuration = await _preparer
            .PrepareAsync(context, compilation, cancellationToken)
            .ConfigureAwait(false);
        await _supervisor.StartAsync(configuration, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(
        IResourceControlContext context,
        CancellationToken cancellationToken = default) =>
        _supervisor.StopAsync(context.Resource, cancellationToken);

    public async Task DeleteAsync(
        IResourceControlContext context,
        CancellationToken cancellationToken = default)
    {
        await _supervisor
            .UninstallAsync(
                context.Resource,
                TimeSpan.FromSeconds(context.Plan.Workload.StopGraceSeconds),
                cancellationToken)
            .ConfigureAwait(false);
        await _preparer.UninstallAsync(context, cancellationToken).ConfigureAwait(false);
    }

    internal static LocalPlanCompilation Compile(
        ResourcePlan plan,
        IExecutableArtifact artifact,
        ResourceInputs inputs,
        IReadOnlyList<ResourceDependencyObservation> observedDependencies,
        LocalGatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(observedDependencies);
        ArgumentNullException.ThrowIfNull(options);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string name, string value) in plan.Container.Environment)
        {
            environment.Add(name, value);
        }

        environment[ResourceEnvironment.Gateway] = "local";
        environment[ResourceEnvironment.ContentRoot] =
            Path.GetDirectoryName(artifact.ExecutablePath) ?? ".";

        foreach (MountBinding mount in plan.Container.Mounts)
        {
            if (!inputs.Mounts.TryGetValue(mount.Mount, out ResourceMountInput? input))
            {
                throw new InvalidOperationException(
                    $"Resource '{plan.Resource}' has no resolved input for mount '{mount.Mount}'.");
            }

            if (!input.IsResolved)
            {
                throw new InvalidOperationException(
                    input.UnresolvedReason
                    ?? $"Mount '{mount.Mount}' on resource '{plan.Resource}' is unresolved.");
            }
        }

        ObservedDependencyEnvironment.Apply(observedDependencies, environment);

        // Reading this immutable option is intentional: compilation receives all platform
        // options as an input and performs no platform I/O or mutation.
        _ = options.LivenessFailureThreshold;
        return new LocalPlanCompilation(plan, artifact, inputs, environment);
    }
}
