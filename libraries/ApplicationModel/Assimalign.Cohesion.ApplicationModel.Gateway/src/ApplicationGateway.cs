using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// The guided base for an <see cref="IApplicationGateway"/>. It validates immutable plans,
/// gathers artifacts, resolves late-bound inputs, and invokes plan controllers in dependency
/// order while applying each plan's readiness gate.
/// </summary>
/// <remarks>
/// Registered controllers from <see cref="ApplicationGatewayOptions.Controllers"/> are
/// consulted before the platform's built-in controllers. Reconciliation is level-triggered;
/// readiness is admitted once per start and later <see cref="ResourceLifecycle.Degraded"/>
/// observations never re-gate dependents.
/// </remarks>
public abstract class ApplicationGateway : IApplicationGateway
{
    private const string LiteralPrefix = "literal:";
    private const string ParameterPrefix = "parameter:";

    private readonly ApplicationGatewayOptions _options;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Dictionary<ResourceId, IResourceArtifact> _artifacts = new();
    private readonly HashSet<ResourceId> _admitted = new();
    private readonly List<RealizedResource> _realized = new();
    private IReadOnlyList<IApplicationResourceDescriptor> _order =
        Array.Empty<IApplicationResourceDescriptor>();
    private IApplicationModel? _activeModel;
    private bool _observerStarted;

    /// <summary>Initializes the gateway with empty common options.</summary>
    protected ApplicationGateway()
        : this(new ApplicationGatewayOptions())
    {
    }

    /// <summary>Initializes the gateway with common controller, input, and readiness options.</summary>
    /// <param name="options">The common gateway options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    protected ApplicationGateway(ApplicationGatewayOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc/>
    public abstract ResourceName Name { get; }

    /// <summary>Gets the common gateway options supplied at construction.</summary>
    protected ApplicationGatewayOptions Options => _options;

    /// <summary>The built-in platform controllers, in compiler priority order.</summary>
    protected abstract IReadOnlyList<IApplicationResourceController> Controllers { get; }

    /// <summary>The level-triggered observed-state store shared by controllers and the observer.</summary>
    protected abstract IApplicationResourceStateManager State { get; }

    /// <summary>Produces or locates the deployable artifact for a resource.</summary>
    /// <param name="resource">The resource to gather an artifact for.</param>
    /// <param name="cancellationToken">Signals that gathering should be abandoned.</param>
    /// <returns>The gathered artifact.</returns>
    protected abstract Task<IResourceArtifact> GatherAsync(
        IApplicationResource resource,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets the default readiness budget. Existing gateway subclasses may override this
    /// common value; new implementations can override <see cref="GetReadinessBudget"/>
    /// when the budget varies by resource plan.
    /// </summary>
    protected virtual TimeSpan ReadinessBudget => _options.ReadinessBudget;

    /// <summary>
    /// Gets the readiness budget for one plan. The default applies
    /// <see cref="ReadinessBudget"/> independently to each resource.
    /// </summary>
    /// <param name="plan">The resource plan about to be gated.</param>
    /// <returns>The readiness budget for this resource.</returns>
    protected virtual TimeSpan GetReadinessBudget(ResourcePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return ReadinessBudget;
    }

    /// <summary>
    /// Resolves late-bound inputs after the descriptor's dependencies have passed initial
    /// readiness. The default resolves <c>literal:</c> and <c>parameter:</c> sources and
    /// returns typed unresolved values for sources requiring a gateway-specific resolver.
    /// </summary>
    /// <param name="descriptor">The built descriptor being reconciled.</param>
    /// <param name="context">The control context for this reconcile pass.</param>
    /// <param name="cancellationToken">Signals that input resolution should be abandoned.</param>
    /// <returns>The resolved inputs for this pass.</returns>
    protected virtual ValueTask<ResourceInputs> ResolveInputsAsync(
        IApplicationResourceDescriptor descriptor,
        IResourceControlContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(context);

        ResourcePlan plan = descriptor.Plan ?? throw new InvalidOperationException(
            $"Resource '{descriptor.Resource.Name}' has no realization plan.");
        var resolved = new Dictionary<string, ResourceMountInput>(StringComparer.Ordinal);

        foreach (MountBinding mount in plan.Container.Mounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? source = mount.Source;

            if (string.IsNullOrEmpty(source))
            {
                resolved.Add(
                    mount.Mount,
                    ResourceMountInput.Resolved(source, ReadOnlyMemory<byte>.Empty));
                continue;
            }

            if (source.StartsWith(LiteralPrefix, StringComparison.Ordinal))
            {
                resolved.Add(
                    mount.Mount,
                    ResourceMountInput.Resolved(
                        source,
                        Encoding.UTF8.GetBytes(source[LiteralPrefix.Length..])));
                continue;
            }

            if (source.StartsWith(ParameterPrefix, StringComparison.Ordinal))
            {
                string parameter = source[ParameterPrefix.Length..];
                if (_options.Parameters.TryGetValue(parameter, out string? value))
                {
                    resolved.Add(
                        mount.Mount,
                        ResourceMountInput.Resolved(source, Encoding.UTF8.GetBytes(value)));
                }
                else
                {
                    resolved.Add(
                        mount.Mount,
                        ResourceMountInput.Unresolved(
                            source,
                            $"Parameter '{parameter}' required by mount '{mount.Mount}' on resource " +
                            $"'{plan.Resource}' is not bound for gateway '{Name}'."));
                }

                continue;
            }

            resolved.Add(
                mount.Mount,
                ResourceMountInput.Unresolved(
                    source,
                    $"Gateway '{Name}' has no resolver for mount source '{source}' on resource " +
                    $"'{plan.Resource}'."));
        }

        return ValueTask.FromResult(
            new ResourceInputs(resolved, ReadOnlyMemory<byte>.Empty));
    }

    /// <summary>Starts the single observer that feeds observed status into <see cref="State"/>. No-op by default.</summary>
    /// <param name="model">The model being realized.</param>
    /// <param name="cancellationToken">Signals that the observer should not start.</param>
    /// <returns>A task that completes once the observer is running.</returns>
    protected virtual Task StartObserverAsync(
        IApplicationModel model,
        CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Stops the observer started by <see cref="StartObserverAsync"/>. No-op by default.</summary>
    /// <param name="cancellationToken">Bounds how long the observer may take to stop.</param>
    /// <returns>A task that completes once the observer has stopped.</returns>
    protected virtual Task StopObserverAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    void IApplicationGateway.Validate(IApplicationModel model) => ValidateCore(model);

    async Task IApplicationGateway.StartAsync(
        IApplicationModel model,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateCore(model);
            await EnsureSessionAsync(model, cancellationToken).ConfigureAwait(false);
            try
            {
                await ReconcilePassAsync(model, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await RollBackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    async Task IApplicationGateway.ReconcileAsync(
        IApplicationModel model,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateCore(model);
            await EnsureSessionAsync(model, cancellationToken).ConfigureAwait(false);
            await ReconcilePassAsync(model, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    async Task IApplicationGateway.StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    async Task IApplicationGateway.UninstallAsync(
        IApplicationModel model,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateCore(model);
            await UninstallCoreAsync(model, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void ValidateCore(IApplicationModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _options.ValidateCommon();

        if (model.Descriptors.Count != model.Plans.Count)
        {
            throw new InvalidOperationException(
                $"Application '{model.Name}' cannot be validated by gateway '{Name}' because its " +
                "descriptor and plan counts differ.");
        }

        for (int index = 0; index < model.Descriptors.Count; index++)
        {
            IApplicationResourceDescriptor descriptor = model.Descriptors[index];
            ResourcePlan plan = descriptor.Plan ?? throw new InvalidOperationException(
                $"Resource '{descriptor.Resource.Name}' has no plan for gateway '{Name}'.");

            if (!ReferenceEquals(plan, model.Plans[index]) && plan != model.Plans[index])
            {
                throw new InvalidOperationException(
                    $"Resource '{descriptor.Resource.Name}' has a descriptor plan that does not match " +
                    $"the application plan supplied to gateway '{Name}'.");
            }

            ResolveController(plan);
        }
    }

    private async Task EnsureSessionAsync(
        IApplicationModel model,
        CancellationToken cancellationToken)
    {
        if (_activeModel is not null)
        {
            if (!ReferenceEquals(_activeModel, model))
            {
                throw new InvalidOperationException(
                    $"Gateway '{Name}' is already supervising application '{_activeModel.Name}'. " +
                    "Stop it before realizing another model.");
            }

            return;
        }

        ResetSession();
        _activeModel = model;
        _order = OrderTopologically(model.Descriptors);

        try
        {
            foreach (IApplicationResourceDescriptor descriptor in _order)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IApplicationResource resource = descriptor.Resource;
                State.SetState(resource.Id, ResourceLifecycle.Building);
                _artifacts.Add(
                    resource.Id,
                    await GatherAsync(resource, cancellationToken).ConfigureAwait(false));
            }

            await StartObserverAsync(model, cancellationToken).ConfigureAwait(false);
            _observerStarted = true;
        }
        catch
        {
            ResetSession();
            throw;
        }
    }

    private async Task ReconcilePassAsync(
        IApplicationModel model,
        CancellationToken cancellationToken)
    {
        foreach (IApplicationResourceDescriptor descriptor in _order)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!DependenciesAreAdmitted(descriptor, out IApplicationResourceDescriptor? unsatisfied))
            {
                State.SetState(
                    descriptor.Resource.Id,
                    ResourceLifecycle.Skipped,
                    $"Dependency '{unsatisfied!.Resource.Name}' was skipped or did not satisfy readiness.");
                continue;
            }

            ResourcePlan plan = descriptor.Plan!;
            IApplicationResourceController controller = ResolveController(plan);
            IReadOnlyList<ResourceDependencyObservation> observedDependencies =
                SnapshotObservedDependencies(model, descriptor);
            var context = new ResourceControlContext(
                descriptor,
                model,
                State,
                ResolveDependencies(descriptor),
                _artifacts[descriptor.Resource.Id],
                observedDependencies);
            ResourceInputs inputs = await ResolveInputsAsync(
                    descriptor,
                    context,
                    cancellationToken)
                .ConfigureAwait(false);
            context.SetInputs(inputs ?? throw new InvalidOperationException(
                $"Gateway '{Name}' returned null inputs for resource '{descriptor.Resource.Name}'."));

            bool wasAdmitted = _admitted.Contains(descriptor.Resource.Id);
            if (!wasAdmitted)
            {
                State.SetState(descriptor.Resource.Id, ResourceLifecycle.Provisioning);
            }

            UpsertRealized(descriptor, controller, context);
            await controller.ReconcileAsync(context, cancellationToken).ConfigureAwait(false);

            if (wasAdmitted)
            {
                continue;
            }

            ResourceLifecycle current = State.GetState(descriptor.Resource.Id);
            if (current is ResourceLifecycle.Skipped)
            {
                continue;
            }

            IReadOnlySet<ResourceLifecycle> terminals =
                new HashSet<ResourceLifecycle>(plan.Workload.Gate.Terminals);
            TimeSpan budget = GetReadinessBudget(plan);
            if (budget <= TimeSpan.Zero)
            {
                throw new InvalidOperationException(
                    $"Gateway '{Name}' returned a non-positive readiness budget for resource " +
                    $"'{plan.Resource}'.");
            }

            ResourceLifecycle reached = await State
                .WaitForStateAsync(
                    descriptor.Resource.Id,
                    terminals,
                    budget,
                    cancellationToken)
                .ConfigureAwait(false);

            if (Contains(plan.Workload.Gate.Satisfying, reached))
            {
                _admitted.Add(descriptor.Resource.Id);
                continue;
            }

            MarkDependentsBlocked(_order, descriptor);
            throw new InvalidOperationException(
                $"Resource '{descriptor.Resource.Name}' did not reach Running or another state " +
                $"satisfying the {plan.Workload.Kind} " +
                $"readiness gate (observed '{reached}') in gateway '{Name}'. Reconcile aborted.");
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            for (int index = _realized.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RealizedResource realized = _realized[index];
                try
                {
                    State.SetState(realized.Descriptor.Resource.Id, ResourceLifecycle.Stopping);
                    await realized.Controller
                        .StopAsync(realized.Context, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // Stop remains best-effort across independent resources, but the caller
                    // receives the first failure after every remaining resource is attempted.
                    failure ??= exception;
                }
            }

            if (_observerStarted)
            {
                await StopObserverAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ResetSession();
        }

        if (failure is not null)
        {
            throw new InvalidOperationException(
                $"Gateway '{Name}' could not stop every runtime-scoped resource.",
                failure);
        }
    }

    private async Task UninstallCoreAsync(
        IApplicationModel model,
        CancellationToken cancellationToken)
    {
        if (_activeModel is not null && !ReferenceEquals(_activeModel, model))
        {
            throw new InvalidOperationException(
                $"Gateway '{Name}' is supervising application '{_activeModel.Name}' and cannot " +
                $"uninstall '{model.Name}'.");
        }

        Exception? failure = null;
        try
        {
            if (_activeModel is null)
            {
                _activeModel = model;
                _order = OrderTopologically(model.Descriptors);
                await StartObserverAsync(model, cancellationToken).ConfigureAwait(false);
                _observerStarted = true;
            }

            for (int index = _order.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IApplicationResourceDescriptor descriptor = _order[index];
                IApplicationResourceController controller = ResolveController(descriptor.Plan!);
                ResourceControlContext context = FindContext(descriptor.Resource.Id)
                    ?? CreateUninstallContext(model, descriptor);

                try
                {
                    State.SetState(descriptor.Resource.Id, ResourceLifecycle.Stopping);
                    await controller.DeleteAsync(context, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // Uninstall is best-effort across resources, but failures are reported.
                    failure ??= exception;
                }
            }

            if (_observerStarted)
            {
                await StopObserverAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ResetSession();
        }

        if (failure is not null)
        {
            throw new InvalidOperationException(
                $"Gateway '{Name}' could not uninstall every resource.",
                failure);
        }
    }

    private async Task RollBackAsync(CancellationToken cancellationToken)
    {
        for (int index = _realized.Count - 1; index >= 0; index--)
        {
            RealizedResource realized = _realized[index];
            try
            {
                State.SetState(realized.Descriptor.Resource.Id, ResourceLifecycle.Stopping);
                await realized.Controller
                    .DeleteAsync(realized.Context, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Preserve the original startup failure; rollback is deliberately best-effort.
            }
        }

        if (_observerStarted)
        {
            try
            {
                await StopObserverAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Preserve the original startup failure; observer shutdown is best-effort.
            }
        }

        ResetSession();
    }

    private bool DependenciesAreAdmitted(
        IApplicationResourceDescriptor descriptor,
        out IApplicationResourceDescriptor? unsatisfied)
    {
        foreach (IApplicationResourceDescriptor dependency in descriptor.Dependencies)
        {
            if (_admitted.Contains(dependency.Resource.Id))
            {
                continue;
            }

            ResourceLifecycle state = State.GetState(dependency.Resource.Id);
            if (dependency.Plan is ResourcePlan plan
                && Contains(plan.Workload.Gate.Satisfying, state))
            {
                _admitted.Add(dependency.Resource.Id);
                continue;
            }

            unsatisfied = dependency;
            return false;
        }

        unsatisfied = null;
        return true;
    }

    private IReadOnlyList<ResourceDependencyObservation> SnapshotObservedDependencies(
        IApplicationModel model,
        IApplicationResourceDescriptor descriptor)
    {
        int descriptorIndex = IndexOfDescriptor(model.Descriptors, descriptor);
        ResourceManifest manifest = model.Manifests[descriptorIndex];
        if (manifest.References.Count == 0)
        {
            return Array.Empty<ResourceDependencyObservation>();
        }

        var observations = new List<ResourceDependencyObservation>(manifest.References.Count);
        foreach (ResourceManifestReference reference in manifest.References)
        {
            int dependencyIndex = IndexOfManifest(model.Manifests, reference.Application, reference.Resource);
            if (dependencyIndex < 0)
            {
                observations.Add(
                    new ResourceDependencyObservation(
                        reference.Application,
                        reference.Resource,
                        ResourceLifecycle.Unknown,
                        reference.Endpoints,
                        Array.Empty<ResourceEndpoint>(),
                        reference.Optional));
                continue;
            }

            IApplicationResource dependency = model.Descriptors[dependencyIndex].Resource;
            ResourceLifecycle state = State.GetState(dependency.Id);
            observations.Add(
                new ResourceDependencyObservation(
                    reference.Application,
                    reference.Resource,
                    state,
                    reference.Endpoints,
                    State.GetObservedEndpoints(dependency.Id),
                    reference.Optional));
        }

        return observations;
    }

    private IApplicationResourceController ResolveController(ResourcePlan plan)
    {
        var reasons = new List<string>();
        foreach (IApplicationResourceController controller in EnumerateControllers())
        {
            bool canRealize;
            string? reason;
            try
            {
                canRealize = controller.CanRealize(plan, out reason);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Controller '{controller.GetType().Name}' failed while validating resource " +
                    $"'{plan.Resource}' for gateway '{Name}'.",
                    exception);
            }

            if (canRealize)
            {
                return controller;
            }

            if (!string.IsNullOrWhiteSpace(reason))
            {
                reasons.Add(reason);
            }
        }

        string detail = reasons.Count == 0
            ? "No registered or platform controller accepted the plan."
            : string.Join(" ", reasons);
        throw new InvalidOperationException(
            $"Resource '{plan.Resource}' cannot be realized by gateway '{Name}'. {detail}");
    }

    private IEnumerable<IApplicationResourceController> EnumerateControllers()
    {
        for (int index = 0; index < _options.Controllers.Count; index++)
        {
            yield return _options.Controllers[index];
        }

        for (int index = 0; index < Controllers.Count; index++)
        {
            yield return Controllers[index];
        }
    }

    private ResourceControlContext CreateUninstallContext(
        IApplicationModel model,
        IApplicationResourceDescriptor descriptor) =>
        new(
            descriptor,
            model,
            State,
            ResolveDependencies(descriptor),
            new UnavailableResourceArtifact(descriptor.Resource.Id),
            Array.Empty<ResourceDependencyObservation>());

    private ResourceControlContext? FindContext(ResourceId resource)
    {
        for (int index = 0; index < _realized.Count; index++)
        {
            if (_realized[index].Descriptor.Resource.Id == resource)
            {
                return _realized[index].Context;
            }
        }

        return null;
    }

    private void UpsertRealized(
        IApplicationResourceDescriptor descriptor,
        IApplicationResourceController controller,
        ResourceControlContext context)
    {
        for (int index = 0; index < _realized.Count; index++)
        {
            if (_realized[index].Descriptor.Resource.Id == descriptor.Resource.Id)
            {
                _realized[index] = new RealizedResource(descriptor, controller, context);
                return;
            }
        }

        _realized.Add(new RealizedResource(descriptor, controller, context));
    }

    private void ResetSession()
    {
        _artifacts.Clear();
        _admitted.Clear();
        _realized.Clear();
        _order = Array.Empty<IApplicationResourceDescriptor>();
        _activeModel = null;
        _observerStarted = false;
    }

    private static IReadOnlyList<IApplicationResource> ResolveDependencies(
        IApplicationResourceDescriptor descriptor)
    {
        if (descriptor.Dependencies.Count == 0)
        {
            return Array.Empty<IApplicationResource>();
        }

        var dependencies = new IApplicationResource[descriptor.Dependencies.Count];
        for (int index = 0; index < dependencies.Length; index++)
        {
            dependencies[index] = descriptor.Dependencies[index].Resource;
        }

        return dependencies;
    }

    private void MarkDependentsBlocked(
        IReadOnlyList<IApplicationResourceDescriptor> order,
        IApplicationResourceDescriptor failed)
    {
        foreach (IApplicationResourceDescriptor descriptor in order)
        {
            if (!ReferenceEquals(descriptor, failed)
                && DependsOnTransitively(descriptor, failed))
            {
                State.SetState(
                    descriptor.Resource.Id,
                    ResourceLifecycle.Blocked,
                    $"Dependency '{failed.Resource.Name}' did not become ready.");
            }
        }
    }

    private static bool DependsOnTransitively(
        IApplicationResourceDescriptor node,
        IApplicationResourceDescriptor target)
    {
        foreach (IApplicationResourceDescriptor dependency in node.Dependencies)
        {
            if (ReferenceEquals(dependency, target)
                || DependsOnTransitively(dependency, target))
            {
                return true;
            }
        }

        return false;
    }

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

    private static int IndexOfDescriptor(
        IReadOnlyList<IApplicationResourceDescriptor> descriptors,
        IApplicationResourceDescriptor target)
    {
        for (int index = 0; index < descriptors.Count; index++)
        {
            if (ReferenceEquals(descriptors[index], target))
            {
                return index;
            }
        }

        throw new InvalidOperationException(
            $"Resource '{target.Resource.Name}' is not part of the application model being realized.");
    }

    private static int IndexOfManifest(
        IReadOnlyList<ResourceManifest> manifests,
        ApplicationName application,
        ResourceName resource)
    {
        for (int index = 0; index < manifests.Count; index++)
        {
            if (manifests[index].Application == application
                && manifests[index].Name == resource)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool Contains(
        IReadOnlyList<ResourceLifecycle> values,
        ResourceLifecycle value)
    {
        for (int index = 0; index < values.Count; index++)
        {
            if (values[index] == value)
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct RealizedResource(
        IApplicationResourceDescriptor Descriptor,
        IApplicationResourceController Controller,
        ResourceControlContext Context);

    private sealed class UnavailableResourceArtifact : IResourceArtifact
    {
        public UnavailableResourceArtifact(ResourceId resource)
        {
            Resource = resource;
        }

        public ResourceId Resource { get; }
    }
}
