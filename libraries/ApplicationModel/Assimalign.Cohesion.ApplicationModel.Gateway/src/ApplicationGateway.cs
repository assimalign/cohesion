using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// The guided base for an <see cref="IApplicationGateway"/>. It implements the generic
/// realization algorithm once — gather artifacts, provision resources in dependency order
/// gating on readiness, then tear down in reverse order — and leaves the platform specifics
/// (how to gather an artifact, which controllers to route to, how observed state is fed) to
/// derived gateways.
/// </summary>
/// <remarks>
/// The interface members are implemented explicitly and forward to strongly-typed
/// <c>protected</c> hooks, per the repository's interface-first-with-guided-base convention.
/// A single observer (started by <see cref="StartObserverAsync"/>) is expected to be the only
/// writer of observed status into <see cref="State"/>; controllers only apply desired state.
/// </remarks>
public abstract class ApplicationGateway : IApplicationGateway
{
    private static readonly IReadOnlySet<ResourceLifecycle> ReadyOrFailed =
        new HashSet<ResourceLifecycle> { ResourceLifecycle.Running, ResourceLifecycle.Failed };

    private readonly List<Provisioned> _provisioned = new();

    /// <inheritdoc/>
    public abstract ResourceName Name { get; }

    /// <summary>The controllers this gateway routes resources to, in priority order.</summary>
    protected abstract IReadOnlyList<IApplicationResourceController> Controllers { get; }

    /// <summary>The level-triggered observed-state store shared by controllers and the observer.</summary>
    protected abstract IApplicationResourceStateManager State { get; }

    /// <summary>Produces or locates the deployable artifact for a resource.</summary>
    /// <param name="resource">The resource to gather an artifact for.</param>
    /// <param name="cancellationToken">Signals that gathering should be abandoned.</param>
    /// <returns>The gathered artifact.</returns>
    protected abstract Task<IResourceArtifact> GatherAsync(IApplicationResource resource, CancellationToken cancellationToken);

    /// <summary>The maximum time to wait for a resource to reach <see cref="ResourceLifecycle.Running"/>.</summary>
    protected virtual TimeSpan ReadinessBudget => TimeSpan.FromSeconds(60);

    /// <summary>Starts the single observer that feeds observed status into <see cref="State"/>. No-op by default.</summary>
    /// <param name="model">The model being realized.</param>
    /// <param name="cancellationToken">Signals that the observer should not start.</param>
    /// <returns>A task that completes once the observer is running.</returns>
    protected virtual Task StartObserverAsync(IApplicationModel model, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Stops the observer started by <see cref="StartObserverAsync"/>. No-op by default.</summary>
    /// <param name="cancellationToken">Bounds how long the observer may take to stop.</param>
    /// <returns>A task that completes once the observer has stopped.</returns>
    protected virtual Task StopObserverAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    async Task IApplicationGateway.StartAsync(IApplicationModel model, CancellationToken cancellationToken)
        => await StartCoreAsync(model, cancellationToken).ConfigureAwait(false);

    async Task IApplicationGateway.StopAsync(CancellationToken cancellationToken)
        => await StopCoreAsync(cancellationToken).ConfigureAwait(false);

    private async Task StartCoreAsync(IApplicationModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        IReadOnlyList<IApplicationResourceDescriptor> order = OrderTopologically(model.Descriptors);

        var artifacts = new Dictionary<ResourceId, IResourceArtifact>();
        foreach (IApplicationResourceDescriptor descriptor in order)
        {
            IApplicationResource resource = descriptor.Resource;
            State.SetState(resource.Id, ResourceLifecycle.Building);
            artifacts[resource.Id] = await GatherAsync(resource, cancellationToken).ConfigureAwait(false);
        }

        await StartObserverAsync(model, cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (IApplicationResourceDescriptor descriptor in order)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IApplicationResource resource = descriptor.Resource;
                IApplicationResourceController controller = ResolveController(resource);
                var context = new ResourceControlContext(
                    resource, model, State, ResolveDependencies(descriptor), artifacts[resource.Id]);

                State.SetState(resource.Id, ResourceLifecycle.Provisioning);
                await controller.ReconcileAsync(context, cancellationToken).ConfigureAwait(false);
                _provisioned.Add(new Provisioned(descriptor, controller, context));

                ResourceLifecycle reached = await State
                    .WaitForStateAsync(resource.Id, ReadyOrFailed, ReadinessBudget, cancellationToken)
                    .ConfigureAwait(false);

                if (reached != ResourceLifecycle.Running)
                {
                    MarkDependentsBlocked(order, descriptor);
                    throw new InvalidOperationException(
                        $"Resource '{resource.Name}' did not reach Running (observed '{reached}'). Startup aborted.");
                }
            }
        }
        catch
        {
            // Best-effort teardown of whatever was provisioned before the failure.
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        for (int i = _provisioned.Count - 1; i >= 0; i--)
        {
            Provisioned p = _provisioned[i];
            try
            {
                State.SetState(p.Descriptor.Resource.Id, ResourceLifecycle.Stopping);
                await p.Controller.DeleteAsync(p.Context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Teardown is best-effort; continue stopping the remaining resources.
            }
        }

        _provisioned.Clear();
        await StopObserverAsync(cancellationToken).ConfigureAwait(false);
    }

    private IApplicationResourceController ResolveController(IApplicationResource resource)
    {
        foreach (IApplicationResourceController controller in Controllers)
        {
            if (controller.CanControl(resource))
            {
                return controller;
            }
        }

        throw new InvalidOperationException(
            $"No controller in gateway '{Name}' can realize resource '{resource.Name}'.");
    }

    private static IReadOnlyList<IApplicationResource> ResolveDependencies(IApplicationResourceDescriptor descriptor)
    {
        if (descriptor.Dependencies.Count == 0)
        {
            return Array.Empty<IApplicationResource>();
        }

        var dependencies = new IApplicationResource[descriptor.Dependencies.Count];
        for (int i = 0; i < dependencies.Length; i++)
        {
            dependencies[i] = descriptor.Dependencies[i].Resource;
        }

        return dependencies;
    }

    private void MarkDependentsBlocked(
        IReadOnlyList<IApplicationResourceDescriptor> order,
        IApplicationResourceDescriptor failed)
    {
        foreach (IApplicationResourceDescriptor descriptor in order)
        {
            if (ReferenceEquals(descriptor, failed))
            {
                continue;
            }

            if (DependsOnTransitively(descriptor, failed))
            {
                State.SetState(
                    descriptor.Resource.Id,
                    ResourceLifecycle.Blocked,
                    $"Dependency '{failed.Resource.Name}' did not become ready.");
            }
        }
    }

    private static bool DependsOnTransitively(IApplicationResourceDescriptor node, IApplicationResourceDescriptor target)
    {
        foreach (IApplicationResourceDescriptor dependency in node.Dependencies)
        {
            if (ReferenceEquals(dependency, target) || DependsOnTransitively(dependency, target))
            {
                return true;
            }
        }

        return false;
    }

    // Depth-first post-order: a descriptor is emitted after all of its dependencies. The model
    // is validated acyclic at build time, so no cycle guard is required here.
    private static IReadOnlyList<IApplicationResourceDescriptor> OrderTopologically(
        IReadOnlyList<IApplicationResourceDescriptor> descriptors)
    {
        var ordered = new List<IApplicationResourceDescriptor>(descriptors.Count);
        var seen = new HashSet<IApplicationResourceDescriptor>();

        foreach (IApplicationResourceDescriptor descriptor in descriptors)
        {
            Visit(descriptor, seen, ordered);
        }

        return ordered;

        static void Visit(
            IApplicationResourceDescriptor node,
            HashSet<IApplicationResourceDescriptor> seen,
            List<IApplicationResourceDescriptor> ordered)
        {
            if (!seen.Add(node))
            {
                return;
            }

            foreach (IApplicationResourceDescriptor dependency in node.Dependencies)
            {
                Visit(dependency, seen, ordered);
            }

            ordered.Add(node);
        }
    }

    private readonly record struct Provisioned(
        IApplicationResourceDescriptor Descriptor,
        IApplicationResourceController Controller,
        IResourceControlContext Context);
}
