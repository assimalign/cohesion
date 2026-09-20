using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;

internal sealed class InProcessPlanController : IApplicationResourceController
{
    private readonly InProcessContextFactory _contexts;
    private readonly IInProcessMemberSupervisor _supervisor;
    private readonly ResourceContext _outerContext;

    internal InProcessPlanController(
        InProcessContextFactory contexts,
        IInProcessMemberSupervisor supervisor,
        ResourceContext outerContext)
    {
        _contexts = contexts;
        _supervisor = supervisor;
        _outerContext = outerContext;
    }

    public bool CanRealize(ResourcePlan plan, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!string.Equals(plan.Schema, ResourcePlan.CurrentSchema, StringComparison.Ordinal))
        {
            reason = $"InProcess compiler does not support plan schema '{plan.Schema}'.";
            return false;
        }
        if (plan.Container.Artifact != ArtifactRef.Self)
        {
            reason = $"InProcess compiler supports only artifact '{ArtifactRef.Self}'.";
            return false;
        }
        if (plan.Workload.Kind is WorkloadKind.DaemonSet or WorkloadKind.Job)
        {
            reason = $"InProcess compiler cannot realize workload kind '{plan.Workload.Kind}' for resource '{plan.Resource}'.";
            return false;
        }
        if (plan.Workload.Replicas != 1)
        {
            reason = "InProcess compiler realizes exactly one host per resource and cannot realize "
                + $"replica count '{plan.Workload.Replicas}'.";
            return false;
        }
        foreach (PortBinding port in plan.Container.Ports)
        {
            if (!string.Equals(port.Protocol, "tcp", StringComparison.OrdinalIgnoreCase))
            {
                reason = $"InProcess compiler cannot honor '{port.Protocol}' endpoint '{port.Endpoint}' on resource '{plan.Resource}'.";
                return false;
            }
        }
        foreach (ProbeMapping probe in plan.Container.Probes)
        {
            if (probe.Kind is ProbeKind.Exec or ProbeKind.Grpc)
            {
                reason = $"InProcess compiler cannot isolate {probe.Kind} probe '{probe.Role}' on resource '{plan.Resource}'.";
                return false;
            }
        }
        foreach (ExposureSpec exposure in plan.Exposures)
        {
            if (!CanHonorExposure(plan, exposure))
            {
                reason = $"InProcess compiler cannot honor public exposure '{exposure.Name}' on resource "
                    + $"'{plan.Resource}' because it must map exactly to one loopback TCP endpoint service.";
                return false;
            }
        }

        reason = null;
        return true;
    }

    private static bool CanHonorExposure(ResourcePlan plan, ExposureSpec exposure)
    {
        if (string.IsNullOrWhiteSpace(exposure.Name) ||
            string.IsNullOrWhiteSpace(exposure.Scheme) ||
            !string.Equals(exposure.Protocol, "tcp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        PortBinding? matchingPort = null;
        foreach (PortBinding port in plan.Container.Ports)
        {
            if (!string.Equals(port.Endpoint, exposure.Endpoint, StringComparison.Ordinal))
            {
                continue;
            }
            if (matchingPort is not null)
            {
                return false;
            }

            matchingPort = port;
        }

        if (matchingPort is null ||
            matchingPort.ContainerPort != exposure.Port ||
            !string.Equals(matchingPort.Protocol, exposure.Protocol, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ServiceSpec? matchingService = null;
        foreach (ServiceSpec service in plan.Services)
        {
            if (!string.Equals(service.Name, exposure.Service, StringComparison.Ordinal))
            {
                continue;
            }
            if (matchingService is not null)
            {
                return false;
            }

            matchingService = service;
        }

        return matchingService is not null &&
            string.Equals(matchingService.Endpoint, exposure.Endpoint, StringComparison.Ordinal) &&
            matchingService.Port == exposure.Port &&
            string.Equals(matchingService.Protocol, exposure.Protocol, StringComparison.OrdinalIgnoreCase) &&
            !matchingService.Headless &&
            !matchingService.Governing;
    }

    public async Task ReconcileAsync(
        IResourceControlContext context,
        CancellationToken cancellationToken = default)
    {
        InProcessResourceArtifact artifact = context.GetArtifact<InProcessResourceArtifact>();
        InProcessPlanCompilation compilation = Compile(
            context.Plan,
            artifact,
            context.Inputs,
            context.ObservedDependencies,
            context.GetTelemetry());
        InProcessMemberConfiguration configuration = await _contexts
            .CreateAsync(context, compilation, _outerContext, cancellationToken)
            .ConfigureAwait(false);
        await _supervisor.StartAsync(configuration, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(
        IResourceControlContext context,
        CancellationToken cancellationToken = default) =>
        _supervisor.StopAsync(context, cancellationToken);

    public async Task DeleteAsync(
        IResourceControlContext context,
        CancellationToken cancellationToken = default)
    {
        await _supervisor.StopAsync(context, cancellationToken).ConfigureAwait(false);
        await _contexts
            .DeleteAsync(context.Model.Name, context.Resource.Name, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static InProcessPlanCompilation Compile(
        ResourcePlan plan,
        InProcessResourceArtifact artifact,
        ResourceInputs inputs,
        IReadOnlyList<ResourceDependencyObservation> observedDependencies,
        IResourceTelemetry? telemetry = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(observedDependencies);

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

        var environment = new Dictionary<string, string>(plan.Container.Environment, StringComparer.Ordinal);
        telemetry.ApplyEnvironment(environment);
        return new InProcessPlanCompilation(
            plan,
            artifact,
            inputs,
            observedDependencies,
            environment,
            telemetry);
    }
}
