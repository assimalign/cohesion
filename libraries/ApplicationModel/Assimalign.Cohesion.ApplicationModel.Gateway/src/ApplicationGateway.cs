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
public abstract class ApplicationGateway : IMultiModelApplicationGateway
{
    private const string LiteralPrefix = "literal:";
    private const string ParameterPrefix = "parameter:";

    private readonly ApplicationGatewayOptions _options;
    private readonly ExternalResourceController _externalController;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Dictionary<ApplicationResourceKey, IResourceArtifact> _artifacts = new();
    private readonly HashSet<ApplicationResourceKey> _admitted = new();
    private readonly List<RealizedResource> _realized = new();
    private IReadOnlyList<ModelResource> _order = Array.Empty<ModelResource>();
    private IReadOnlyList<IApplicationModel> _activeModels = Array.Empty<IApplicationModel>();
    private IReadOnlyList<ApplicationStateView> _applicationStates =
        Array.Empty<ApplicationStateView>();
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
        _externalController = new ExternalResourceController(_options.ControlPlaneClient);
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
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/> is <see langword="null"/>.
    /// </exception>
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
    /// <exception cref="ArgumentNullException">
    /// <paramref name="descriptor"/> or <paramref name="context"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="descriptor"/> has no realization plan.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> is canceled.
    /// </exception>
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

    /// <summary>
    /// Starts the single observer that feeds observed status for an ordered model collection
    /// into the state managers returned by <see cref="GetApplicationState"/>. The default
    /// dispatches the existing single-model hook for a singleton collection and is otherwise
    /// a no-op.
    /// </summary>
    /// <param name="models">The ordered models being realized.</param>
    /// <param name="cancellationToken">Signals that the observer should not start.</param>
    /// <returns>A task that completes once the observer is running.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="models"/> is <see langword="null"/>.
    /// </exception>
    protected virtual Task StartObserverAsync(
        IReadOnlyList<IApplicationModel> models,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(models);
        return models.Count == 1
            ? StartObserverAsync(models[0], cancellationToken)
            : Task.CompletedTask;
    }

    /// <summary>
    /// Starts the single observer for a singleton gateway session. Existing gateway
    /// implementations can continue to override this hook.
    /// </summary>
    /// <param name="model">The model being realized.</param>
    /// <param name="cancellationToken">Signals that the observer should not start.</param>
    /// <returns>A task that completes once the observer is running.</returns>
    protected virtual Task StartObserverAsync(
        IApplicationModel model,
        CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Stops the observer started for the current gateway session. No-op by default.</summary>
    /// <param name="cancellationToken">Bounds how long the observer may take to stop.</param>
    /// <returns>A task that completes once the observer has stopped.</returns>
    protected virtual Task StopObserverAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Publishes one validated application export after a successful reconcile pass. The
    /// default writes <c>.cohesion/&lt;application&gt;/export.json</c>; platform gateways may
    /// override this seam to publish the same document through their native control plane.
    /// </summary>
    /// <param name="document">The export document to publish.</param>
    /// <param name="cancellationToken">Signals that publication should be abandoned.</param>
    /// <returns>A task that completes after the export is published.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="document"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="System.IO.InvalidDataException">
    /// <paramref name="document"/> violates the application-export contract.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> is canceled.
    /// </exception>
    protected virtual Task PublishApplicationExportAsync(
        ApplicationExportDocument document,
        CancellationToken cancellationToken) =>
        ApplicationExportWriter.WriteAsync(document, _options.ExportDirectory, cancellationToken);

    /// <summary>
    /// Withdraws an application's discovery export after its resources stop or are deleted.
    /// The default removes the local <c>export.json</c>; platform gateways may override this
    /// seam to withdraw the same document from their native control plane.
    /// </summary>
    /// <param name="application">The application whose export must be withdrawn.</param>
    /// <param name="cancellationToken">Bounds export withdrawal.</param>
    /// <returns>A task that completes after the export is no longer discoverable.</returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> is canceled.
    /// </exception>
    protected virtual Task RemoveApplicationExportAsync(
        ApplicationName application,
        CancellationToken cancellationToken) =>
        ApplicationExportWriter.DeleteAsync(application, _options.ExportDirectory, cancellationToken);

    /// <summary>
    /// Gets the application-scoped state view for <paramref name="model"/> in the active
    /// gateway session. Multi-model observers must use this view so equal resource identifiers
    /// in different applications remain isolated.
    /// </summary>
    /// <param name="model">A model in the active gateway session.</param>
    /// <returns>The model's application-scoped observed-state view.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="model"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The gateway has no active session for <paramref name="model"/>.
    /// </exception>
    protected IApplicationResourceStateManager GetApplicationState(IApplicationModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        for (int index = 0; index < _applicationStates.Count; index++)
        {
            if (ReferenceEquals(_applicationStates[index].Model, model))
            {
                return _applicationStates[index].State;
            }
        }

        throw new InvalidOperationException(
            $"Application '{model.Name}' is not part of the active gateway '{Name}' session.");
    }

    void IApplicationGateway.Validate(IApplicationModel model) => Validate(Singleton(model));

    Task IApplicationGateway.StartAsync(
        IApplicationModel model,
        CancellationToken cancellationToken) => StartAsync(Singleton(model), cancellationToken);

    Task IApplicationGateway.ReconcileAsync(
        IApplicationModel model,
        CancellationToken cancellationToken) => ReconcileAsync(Singleton(model), cancellationToken);

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

    Task IApplicationGateway.UninstallAsync(
        IApplicationModel model,
        CancellationToken cancellationToken) => UninstallAsync(Singleton(model), cancellationToken);

    /// <inheritdoc/>
    public void Validate(IReadOnlyList<IApplicationModel> models)
    {
        IApplicationModel[] snapshot = SnapshotModels(models);
        ValidateCore(snapshot);
    }

    /// <inheritdoc/>
    public async Task StartAsync(
        IReadOnlyList<IApplicationModel> models,
        CancellationToken cancellationToken = default)
    {
        IApplicationModel[] snapshot = SnapshotModels(models);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateCore(snapshot);
            await EnsureSessionAsync(snapshot, cancellationToken).ConfigureAwait(false);
            try
            {
                await ReconcilePassAsync(cancellationToken).ConfigureAwait(false);
                // Run-mode cancellation is also the application lifetime signal. Once every
                // resource is ready, finish publishing the matching discovery snapshot; the
                // subsequent StopAsync call withdraws it.
                await PublishApplicationExportsAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                await RollBackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <inheritdoc/>
    public async Task ReconcileAsync(
        IReadOnlyList<IApplicationModel> models,
        CancellationToken cancellationToken = default)
    {
        IApplicationModel[] snapshot = SnapshotModels(models);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateCore(snapshot);
            await EnsureSessionAsync(snapshot, cancellationToken).ConfigureAwait(false);
            await ReconcilePassAsync(cancellationToken).ConfigureAwait(false);
            await PublishApplicationExportsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <inheritdoc/>
    public async Task UninstallAsync(
        IReadOnlyList<IApplicationModel> models,
        CancellationToken cancellationToken = default)
    {
        IApplicationModel[] snapshot = SnapshotModels(models);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateCore(snapshot);
            await UninstallCoreAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void ValidateCore(IReadOnlyList<IApplicationModel> models)
    {
        _options.ValidateCommon();
        var names = new HashSet<ApplicationName>();

        foreach (IApplicationModel model in models)
        {
            if (!names.Add(model.Name))
            {
                throw new InvalidOperationException(
                    $"Application '{model.Name}' occurs more than once in the model collection " +
                    $"supplied to gateway '{Name}'.");
            }

            if (model.Descriptors.Count != model.Plans.Count ||
                model.Descriptors.Count != model.Manifests.Count)
            {
                throw new InvalidOperationException(
                    $"Application '{model.Name}' cannot be validated by gateway '{Name}' because its " +
                    "descriptor, manifest, and plan counts differ.");
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
    }

    private async Task EnsureSessionAsync(
        IReadOnlyList<IApplicationModel> models,
        CancellationToken cancellationToken)
    {
        if (_activeModels.Count != 0)
        {
            if (!SessionMatches(models))
            {
                throw new InvalidOperationException(
                    $"Gateway '{Name}' is already supervising a different model collection. " +
                    "Stop it before realizing another collection.");
            }

            return;
        }

        ResetSession();
        InitializeSession(models);

        try
        {
            foreach (ModelResource item in _order)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IApplicationResource resource = item.Descriptor.Resource;
                IApplicationResourceStateManager state = GetApplicationState(item.Model);
                state.SetState(resource.Id, ResourceLifecycle.Building);
                IResourceArtifact artifact = IsExternalPlan(item.Descriptor.Plan!)
                    ? new ExternalResourceArtifact(resource.Id)
                    : await GatherAsync(resource, cancellationToken).ConfigureAwait(false);
                _artifacts.Add(item.Key, artifact);
            }

            await StartObserverAsync(_activeModels, cancellationToken).ConfigureAwait(false);
            _observerStarted = true;
        }
        catch
        {
            ResetSession();
            throw;
        }
    }

    private async Task ReconcilePassAsync(CancellationToken cancellationToken)
    {
        foreach (ModelResource item in _order)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IApplicationModel model = item.Model;
            IApplicationResourceDescriptor descriptor = item.Descriptor;
            IApplicationResourceStateManager state = GetApplicationState(model);

            if (!DependenciesAreAdmitted(item, out IApplicationResourceDescriptor? unsatisfied))
            {
                state.SetState(
                    descriptor.Resource.Id,
                    ResourceLifecycle.Skipped,
                    $"Dependency '{unsatisfied!.Resource.Name}' was skipped or did not satisfy readiness.");
                continue;
            }

            ResourcePlan plan = descriptor.Plan!;
            IApplicationResourceController controller = ResolveController(plan);
            IReadOnlyList<ResourceDependencyObservation> observedDependencies =
                SnapshotObservedDependencies(model, descriptor, state);
            var context = new ResourceControlContext(
                descriptor,
                model,
                state,
                ResolveDependencies(descriptor),
                _artifacts[item.Key],
                observedDependencies);
            ResourceInputs inputs = await ResolveInputsAsync(
                    descriptor,
                    context,
                    cancellationToken)
                .ConfigureAwait(false);
            context.SetInputs(inputs ?? throw new InvalidOperationException(
                $"Gateway '{Name}' returned null inputs for resource '{descriptor.Resource.Name}'."));

            bool wasAdmitted = _admitted.Contains(item.Key);
            if (!wasAdmitted)
            {
                state.SetState(descriptor.Resource.Id, ResourceLifecycle.Provisioning);
            }

            UpsertRealized(item, controller, context);
            await controller.ReconcileAsync(context, cancellationToken).ConfigureAwait(false);

            if (wasAdmitted)
            {
                continue;
            }

            ResourceLifecycle current = state.GetState(descriptor.Resource.Id);
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

            ResourceLifecycle reached = await state
                .WaitForStateAsync(
                    descriptor.Resource.Id,
                    terminals,
                    budget,
                    cancellationToken)
                .ConfigureAwait(false);

            if (Contains(plan.Workload.Gate.Satisfying, reached))
            {
                _admitted.Add(item.Key);
                continue;
            }

            if (!terminals.Contains(reached))
            {
                state.SetState(
                    descriptor.Resource.Id,
                    ResourceLifecycle.Failed,
                    $"Resource '{descriptor.Resource.Name}' exceeded its readiness budget of " +
                    $"'{budget}' while observed as '{reached}'.");
            }

            MarkDependentsBlocked(item);
            throw new InvalidOperationException(
                $"Resource '{descriptor.Resource.Name}' did not reach Running or another state " +
                $"satisfying the {plan.Workload.Kind} " +
                $"readiness gate (observed '{reached}') in gateway '{Name}'. Reconcile aborted.");
        }
    }

    private async Task PublishApplicationExportsAsync(CancellationToken cancellationToken)
    {
        for (int modelIndex = 0; modelIndex < _activeModels.Count; modelIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IApplicationModel model = _activeModels[modelIndex];
            IApplicationResourceStateManager state = GetApplicationState(model);
            var endpoints = new Dictionary<ResourceName, IReadOnlyList<ApplicationExportEndpoint>>();

            for (int resourceIndex = 0; resourceIndex < model.Descriptors.Count; resourceIndex++)
            {
                IApplicationResource resource = model.Descriptors[resourceIndex].Resource;
                if (IsExternalPlan(model.Plans[resourceIndex]))
                {
                    continue;
                }

                IReadOnlyList<ApplicationExportEndpoint> observed = CreateExportEndpoints(
                    state.GetObservedEndpoints(resource.Id),
                    model.Manifests[resourceIndex]);
                if (observed.Count != 0)
                {
                    endpoints.Add(resource.Name, observed);
                }
            }

            ApplicationExportDocument document = ApplicationExportDocument.Create(
                model,
                _options.ApplicationVersion,
                endpoints,
                _options.TrustKey);
            await PublishApplicationExportAsync(document, cancellationToken).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<ApplicationExportEndpoint> CreateExportEndpoints(
        IReadOnlyList<ResourceEndpoint> observed,
        ResourceManifest manifest)
    {
        var order = new List<string>();
        var addresses = new Dictionary<string, ExportEndpointPair>(StringComparer.Ordinal);
        var declared = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < manifest.Endpoints.Count; index++)
        {
            declared.Add(manifest.Endpoints[index].Name);
        }

        for (int index = 0; index < observed.Count; index++)
        {
            ResourceEndpoint endpoint = observed[index];
            if (!declared.Contains(endpoint.Name) ||
                string.IsNullOrWhiteSpace(endpoint.Scheme) ||
                string.IsNullOrWhiteSpace(endpoint.Host) ||
                endpoint.Port <= 0)
            {
                continue;
            }

            string internalAddress = CreateAuthority(endpoint.Host, endpoint.Port);
            string? publicAddress = endpoint.IsPublic ? CreateAbsoluteAddress(endpoint) : null;
            if (!addresses.TryGetValue(endpoint.Name, out ExportEndpointPair current))
            {
                order.Add(endpoint.Name);
                addresses.Add(
                    endpoint.Name,
                    new ExportEndpointPair(internalAddress, publicAddress, !endpoint.IsPublic));
                continue;
            }

            if (!endpoint.IsPublic && !current.HasInternalObservation)
            {
                current = current with
                {
                    Internal = internalAddress,
                    HasInternalObservation = true,
                };
            }

            if (endpoint.IsPublic && current.Public is null)
            {
                current = current with { Public = publicAddress };
            }

            addresses[endpoint.Name] = current;
        }

        var exported = new ApplicationExportEndpoint[order.Count];
        for (int index = 0; index < exported.Length; index++)
        {
            ExportEndpointPair address = addresses[order[index]];
            exported[index] = new ApplicationExportEndpoint(
                order[index],
                address.Internal,
                address.Public);
        }

        return exported;
    }

    private static string CreateAuthority(string host, int port)
    {
        string formattedHost = host.Contains(":", StringComparison.Ordinal) &&
            !host.StartsWith("[", StringComparison.Ordinal)
                ? $"[{host}]"
                : host;
        return $"{formattedHost}:{port}";
    }

    private static string CreateAbsoluteAddress(ResourceEndpoint endpoint)
        => Uri.CreateEndpoint(endpoint.Scheme, endpoint.Host!, endpoint.Port).ToEndpointString();

    private static bool IsExternalPlan(ResourcePlan plan) =>
        plan.Hints.TryGetValue(ExternalResourceController.PlanHint, out string? external) &&
        bool.TryParse(external, out bool isExternal) &&
        isExternal;

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        bool exportsRemoved = false;
        try
        {
            for (int index = _realized.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RealizedResource realized = _realized[index];
                try
                {
                    realized.Context.State.SetState(
                        realized.Item.Descriptor.Resource.Id,
                        ResourceLifecycle.Stopping);
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
                try
                {
                    await StopObserverAsync(cancellationToken).ConfigureAwait(false);
                    _observerStarted = false;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
            }

            Exception? exportFailure = await RemoveApplicationExportsAsync(cancellationToken)
                .ConfigureAwait(false);
            failure ??= exportFailure;
            exportsRemoved = exportFailure is null;
        }
        finally
        {
            if (exportsRemoved)
            {
                ResetSession();
            }
        }

        if (failure is not null)
        {
            throw new InvalidOperationException(
                $"Gateway '{Name}' could not stop every runtime-scoped resource or withdraw every export.",
                failure);
        }
    }

    private async Task UninstallCoreAsync(
        IReadOnlyList<IApplicationModel> models,
        CancellationToken cancellationToken)
    {
        if (_activeModels.Count != 0 && !SessionMatches(models))
        {
            throw new InvalidOperationException(
                $"Gateway '{Name}' is supervising a different model collection and cannot " +
                "uninstall the supplied collection.");
        }

        Exception? failure = null;
        bool exportsRemoved = false;
        try
        {
            if (_activeModels.Count == 0)
            {
                InitializeSession(models);
                await StartObserverAsync(_activeModels, cancellationToken).ConfigureAwait(false);
                _observerStarted = true;
            }

            for (int index = _order.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ModelResource item = _order[index];
                IApplicationResourceDescriptor descriptor = item.Descriptor;
                IApplicationResourceStateManager state = GetApplicationState(item.Model);
                IApplicationResourceController controller = ResolveController(descriptor.Plan!);
                ResourceControlContext context = FindContext(item.Key)
                    ?? CreateUninstallContext(item, state);

                try
                {
                    state.SetState(descriptor.Resource.Id, ResourceLifecycle.Stopping);
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
                try
                {
                    await StopObserverAsync(cancellationToken).ConfigureAwait(false);
                    _observerStarted = false;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
            }

            Exception? exportFailure = await RemoveApplicationExportsAsync(cancellationToken)
                .ConfigureAwait(false);
            failure ??= exportFailure;
            exportsRemoved = exportFailure is null;
        }
        finally
        {
            if (exportsRemoved)
            {
                ResetSession();
            }
        }

        if (failure is not null)
        {
            throw new InvalidOperationException(
                $"Gateway '{Name}' could not uninstall every resource or withdraw every export.",
                failure);
        }
    }

    private async Task<Exception?> RemoveApplicationExportsAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        for (int index = 0; index < _activeModels.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await RemoveApplicationExportAsync(
                    _activeModels[index].Name,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Export withdrawal is best-effort across applications, while retaining
                // the first error so the active session can be retried by the caller.
                failure ??= exception;
            }
        }

        return failure;
    }

    private async Task RollBackAsync(CancellationToken cancellationToken)
    {
        for (int index = _realized.Count - 1; index >= 0; index--)
        {
            RealizedResource realized = _realized[index];
            try
            {
                realized.Context.State.SetState(
                    realized.Item.Descriptor.Resource.Id,
                    ResourceLifecycle.Stopping);
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
                _observerStarted = false;
            }
            catch (Exception)
            {
                // Preserve the original startup failure; observer shutdown is best-effort.
            }
        }

        Exception? exportFailure = await RemoveApplicationExportsAsync(CancellationToken.None)
            .ConfigureAwait(false);
        if (exportFailure is null)
        {
            ResetSession();
        }
    }

    private bool DependenciesAreAdmitted(
        ModelResource item,
        out IApplicationResourceDescriptor? unsatisfied)
    {
        IApplicationResourceDescriptor descriptor = item.Descriptor;
        IApplicationResourceStateManager state = GetApplicationState(item.Model);
        foreach (IApplicationResourceDescriptor dependency in descriptor.Dependencies)
        {
            var dependencyKey = new ApplicationResourceKey(
                item.Model.Name,
                dependency.Resource.Id);
            if (_admitted.Contains(dependencyKey))
            {
                continue;
            }

            ResourceLifecycle lifecycle = state.GetState(dependency.Resource.Id);
            if (dependency.Plan is ResourcePlan plan
                && Contains(plan.Workload.Gate.Satisfying, lifecycle))
            {
                _admitted.Add(dependencyKey);
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
        IApplicationResourceDescriptor descriptor,
        IApplicationResourceStateManager state)
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
            ResourceLifecycle lifecycle = state.GetState(dependency.Id);
            observations.Add(
                new ResourceDependencyObservation(
                    reference.Application,
                    reference.Resource,
                    lifecycle,
                    reference.Endpoints,
                    state.GetObservedEndpoints(dependency.Id),
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

        yield return _externalController;

        for (int index = 0; index < Controllers.Count; index++)
        {
            yield return Controllers[index];
        }
    }

    private ResourceControlContext CreateUninstallContext(
        ModelResource item,
        IApplicationResourceStateManager state) =>
        new(
            item.Descriptor,
            item.Model,
            state,
            ResolveDependencies(item.Descriptor),
            new UnavailableResourceArtifact(item.Descriptor.Resource.Id),
            Array.Empty<ResourceDependencyObservation>());

    private ResourceControlContext? FindContext(ApplicationResourceKey key)
    {
        for (int index = 0; index < _realized.Count; index++)
        {
            if (_realized[index].Item.Key == key)
            {
                return _realized[index].Context;
            }
        }

        return null;
    }

    private void UpsertRealized(
        ModelResource item,
        IApplicationResourceController controller,
        ResourceControlContext context)
    {
        for (int index = 0; index < _realized.Count; index++)
        {
            if (_realized[index].Item.Key == item.Key)
            {
                _realized[index] = new RealizedResource(item, controller, context);
                return;
            }
        }

        _realized.Add(new RealizedResource(item, controller, context));
    }

    private void ResetSession()
    {
        for (int index = 0; index < _applicationStates.Count; index++)
        {
            _applicationStates[index].OwnedState?.Dispose();
        }

        _artifacts.Clear();
        _admitted.Clear();
        _realized.Clear();
        _order = Array.Empty<ModelResource>();
        _activeModels = Array.Empty<IApplicationModel>();
        _applicationStates = Array.Empty<ApplicationStateView>();
        _observerStarted = false;
    }

    private void InitializeSession(IReadOnlyList<IApplicationModel> models)
    {
        var activeModels = new IApplicationModel[models.Count];
        var applicationStates = new ApplicationStateView[models.Count];
        var order = new List<ModelResource>();
        bool scopeResourceIds = models.Count > 1;

        try
        {
            for (int modelIndex = 0; modelIndex < models.Count; modelIndex++)
            {
                IApplicationModel model = models[modelIndex];
                activeModels[modelIndex] = model;

                if (scopeResourceIds)
                {
                    var scopedState = new ApplicationScopedResourceStateManager(
                        State,
                        model.Name,
                        model.Resources);
                    applicationStates[modelIndex] =
                        new ApplicationStateView(model, scopedState, scopedState);
                }
                else
                {
                    applicationStates[modelIndex] =
                        new ApplicationStateView(model, State, null);
                }

                IReadOnlyList<IApplicationResourceDescriptor> modelOrder =
                    OrderTopologically(model.Descriptors);
                foreach (IApplicationResourceDescriptor descriptor in modelOrder)
                {
                    order.Add(new ModelResource(model, descriptor));
                }
            }
        }
        catch
        {
            for (int index = 0; index < applicationStates.Length; index++)
            {
                applicationStates[index].OwnedState?.Dispose();
            }

            throw;
        }

        _activeModels = activeModels;
        _applicationStates = applicationStates;
        _order = order;
    }

    private bool SessionMatches(IReadOnlyList<IApplicationModel> models)
    {
        if (_activeModels.Count != models.Count)
        {
            return false;
        }

        for (int index = 0; index < models.Count; index++)
        {
            if (!ReferenceEquals(_activeModels[index], models[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static IApplicationModel[] SnapshotModels(IReadOnlyList<IApplicationModel> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (models.Count == 0)
        {
            throw new ArgumentException(
                "At least one application model is required.",
                nameof(models));
        }

        var snapshot = new IApplicationModel[models.Count];
        for (int index = 0; index < models.Count; index++)
        {
            IApplicationModel? model = models[index];
            if (model is null)
            {
                throw new ArgumentNullException($"{nameof(models)}[{index}]");
            }

            snapshot[index] = model;
        }

        return snapshot;
    }

    private static IReadOnlyList<IApplicationModel> Singleton(IApplicationModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new[] { model };
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

    private void MarkDependentsBlocked(ModelResource failed)
    {
        foreach (ModelResource candidate in _order)
        {
            if (ReferenceEquals(candidate.Model, failed.Model)
                && !ReferenceEquals(candidate.Descriptor, failed.Descriptor)
                && DependsOnTransitively(candidate.Descriptor, failed.Descriptor))
            {
                GetApplicationState(candidate.Model).SetState(
                    candidate.Descriptor.Resource.Id,
                    ResourceLifecycle.Blocked,
                    $"Dependency '{failed.Descriptor.Resource.Name}' did not become ready.");
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

    private readonly record struct ApplicationResourceKey(
        ApplicationName Application,
        ResourceId Resource);

    private readonly record struct ModelResource(
        IApplicationModel Model,
        IApplicationResourceDescriptor Descriptor)
    {
        public ApplicationResourceKey Key => new(Model.Name, Descriptor.Resource.Id);
    }

    private readonly record struct ApplicationStateView(
        IApplicationModel Model,
        IApplicationResourceStateManager State,
        IDisposable? OwnedState);

    private readonly record struct RealizedResource(
        ModelResource Item,
        IApplicationResourceController Controller,
        ResourceControlContext Context);

    private readonly record struct ExportEndpointPair(
        string Internal,
        string? Public,
        bool HasInternalObservation);

    private sealed class UnavailableResourceArtifact : IResourceArtifact
    {
        public UnavailableResourceArtifact(ResourceId resource)
        {
            Resource = resource;
        }

        public ResourceId Resource { get; }
    }
}
