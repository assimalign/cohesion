using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;

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
public abstract partial class ApplicationGateway :
    IMultiModelApplicationGateway,
    IApplicationSetExternalResourceResolver,
    IApplicationGatewayCommandHandler,
    IApplicationTrustGateway,
    IResourceCommandCredentialProvider
{
    private const string LiteralPrefix = "literal:";
    private const string ParameterPrefix = "parameter:";
    private const string TrustedIssuersFileName = "trusted-issuers.json";

    private readonly ApplicationGatewayOptions _options;
    private readonly ExternalResourceController _externalController;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _credentialGate = new();
    private readonly object _trustStatesGate = new();
    private readonly Dictionary<ApplicationResourceKey, IResourceArtifact> _artifacts = new();
    private readonly Dictionary<ApplicationName, IReadOnlyDictionary<string, string>> _parameters = new();
    private readonly Dictionary<ApplicationName, ApplicationTrustState> _trust = new();
    private readonly Dictionary<BootstrapCredentialKey, string> _bootstrapCredentials = new();
    private readonly Dictionary<ApplicationName, IApplicationGatewayControlPlane> _controlPlanes = new();
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
        _externalController = new ExternalResourceController(CreateControlPlaneClient);
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
    /// Validates resource-specific platform requirements that are not represented by the
    /// platform-neutral plan. The default accepts the resource.
    /// </summary>
    /// <param name="model">The application model that owns the resource.</param>
    /// <param name="descriptor">The immutable resource descriptor.</param>
    /// <param name="plan">The validated realization plan.</param>
    /// <exception cref="InvalidOperationException">
    /// The resource cannot be realized by this gateway.
    /// </exception>
    protected virtual void ValidateResource(
        IApplicationModel model,
        IApplicationResourceDescriptor descriptor,
        ResourcePlan plan)
    {
    }

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
    /// readiness. The default resolves <c>literal:</c>, <c>parameter:</c>, and
    /// <c>&lt;resource&gt;:&lt;key&gt;</c> sources and uses the resource's ES256 bootstrap credential
    /// for the current reconcile pass.
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
    protected virtual async ValueTask<ResourceInputs> ResolveInputsAsync(
        IApplicationResourceDescriptor descriptor,
        IResourceControlContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(context);

        ResourcePlan plan = descriptor.Plan ?? throw new InvalidOperationException(
            $"Resource '{descriptor.Resource.Name}' has no realization plan.");
        var resolved = new Dictionary<string, ResourceMountInput>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, string> parameters = GetParameters(context.Model.Name);

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
                resolved.Add(mount.Mount, mount.Kind == ResourceMountKind.Configuration
                    ? ResourceMountInput.Resolved(
                        source,
                        Encoding.UTF8.GetBytes(source[LiteralPrefix.Length..]))
                    : ResourceMountInput.Unresolved(
                        source,
                        $"Literal source '{source}' is allowed only for Configuration mounts; " +
                        $"mount '{mount.Mount}' on resource '{plan.Resource}' is '{mount.Kind}'."));
                continue;
            }

            if (source.StartsWith(ParameterPrefix, StringComparison.Ordinal))
            {
                string parameter = source[ParameterPrefix.Length..];
                if (mount.Kind == ResourceMountKind.Volume)
                {
                    resolved.Add(
                        mount.Mount,
                        ResourceMountInput.Unresolved(
                            source,
                            $"Volume mount '{mount.Mount}' on resource '{plan.Resource}' cannot declare a source."));
                }
                else if (string.IsNullOrWhiteSpace(parameter))
                {
                    resolved.Add(
                        mount.Mount,
                        ResourceMountInput.Unresolved(
                            source,
                            $"Parameter source for mount '{mount.Mount}' on resource '{plan.Resource}' has no name."));
                }
                else if (parameters.TryGetValue(parameter, out string? value))
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

            if (!TryParseResourceSource(source, out ResourceName sourceResource, out string? sourceKey))
            {
                resolved.Add(
                    mount.Mount,
                    ResourceMountInput.Unresolved(
                        source,
                        $"Mount source '{source}' on resource '{plan.Resource}' must use " +
                        "parameter:<name>, literal:<value>, or <resource>:<key>."));
                continue;
            }

            if (!TryResolveSourceResource(
                    context,
                    sourceResource,
                    out ResourceManifest? sourceManifest,
                    out ResourceDependencyObservation? observation,
                    out string? sourceFailure))
            {
                resolved.Add(
                    mount.Mount,
                    ResourceMountInput.Unresolved(source, sourceFailure!));
                continue;
            }

            if (!TryResolveControlPlaneEndpoint(
                    sourceManifest!,
                    observation!,
                    out Uri? endpoint,
                    out string? endpointFailure))
            {
                resolved.Add(
                    mount.Mount,
                    ResourceMountInput.Unresolved(source, endpointFailure!));
                continue;
            }

            if (!CanSendCredential(context.Model, endpoint!, out string? securityFailure))
            {
                resolved.Add(
                    mount.Mount,
                    ResourceMountInput.Unresolved(source, securityFailure!));
                continue;
            }

            string credential = GetOrIssueBootstrapToken(
                context.Model,
                sourceManifest!.Name);
            try
            {
                ResourceMountInput input = await ResolveStoreSourceAsync(
                        context,
                        plan,
                        mount,
                        source,
                        sourceKey!,
                        sourceManifest,
                        endpoint!,
                        credential,
                        cancellationToken)
                    .ConfigureAwait(false);
                resolved.Add(mount.Mount, input);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is HttpRequestException or InvalidDataException or JsonException or
                    NotSupportedException or ArgumentException)
            {
                resolved.Add(
                    mount.Mount,
                    ResourceMountInput.Unresolved(
                        source,
                        $"Mount '{mount.Mount}' on resource '{plan.Resource}' could not resolve " +
                        $"'{source}' through '{sourceManifest.Name}': {exception.Message}"));
            }
        }

        string bootstrapCredential = GetOrIssueBootstrapToken(
            context.Model,
            descriptor.Resource.Name);
        ApplicationTrustState trust = GetTrustState(context.Model.Name);
        return new ResourceInputs(
            resolved,
            Encoding.ASCII.GetBytes(bootstrapCredential),
            trust.PublicKey);
    }

    private async ValueTask<ResourceMountInput> ResolveStoreSourceAsync(
        IResourceControlContext context,
        ResourcePlan plan,
        MountBinding mount,
        string source,
        string key,
        ResourceManifest sourceManifest,
        Uri endpoint,
        string credential,
        CancellationToken cancellationToken)
    {
        if (mount.Kind == ResourceMountKind.Secret &&
            string.Equals(sourceManifest.Kind, "SecretStore", StringComparison.OrdinalIgnoreCase))
        {
            if (IsCertificateMount(context, mount.Mount))
            {
                try
                {
                    string certificate = await _options.StoreClient
                        .ReadCertificateAsync(endpoint, credential, key, cancellationToken)
                        .ConfigureAwait(false);
                    return ResourceMountInput.Resolved(source, Encoding.UTF8.GetBytes(certificate));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (
                    exception is HttpRequestException or InvalidDataException or NotSupportedException)
                {
                    return ResourceMountInput.Unresolved(
                        source,
                        $"Certificate '{key}' for mount '{mount.Mount}' on resource " +
                        $"'{plan.Resource}' is not available from SecretStore " +
                        $"'{sourceManifest.Name}': {exception.Message}");
                }
            }

            ReadOnlyMemory<byte> secret = await _options.StoreClient
                .ReadSecretAsync(endpoint, credential, key, cancellationToken)
                .ConfigureAwait(false);
            return ResourceMountInput.Resolved(source, secret);
        }

        if (mount.Kind == ResourceMountKind.Configuration &&
            string.Equals(sourceManifest.Kind, "ConfigurationStore", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyDictionary<string, string?> configuration = await _options.StoreClient
                .ReadConfigurationAsync(endpoint, credential, key, cancellationToken)
                .ConfigureAwait(false);
            return ResourceMountInput.Resolved(source, SerializeConfiguration(configuration));
        }

        return ResourceMountInput.Unresolved(
            source,
            $"Mount '{mount.Mount}' on resource '{plan.Resource}' is '{mount.Kind}', but source " +
            $"resource '{sourceManifest.Name}' is kind '{sourceManifest.Kind}'. Secret mounts " +
            "require SecretStore and Configuration mounts require ConfigurationStore.");
    }

    private static bool TryParseResourceSource(
        string source,
        out ResourceName resource,
        out string? key)
    {
        int separator = source.IndexOf(':');
        if (separator <= 0 || separator == source.Length - 1 ||
            source.IndexOf(':', separator + 1) >= 0)
        {
            resource = default;
            key = null;
            return false;
        }

        string resourceName = source[..separator];
        key = source[(separator + 1)..];
        if (string.IsNullOrWhiteSpace(resourceName) || string.IsNullOrWhiteSpace(key))
        {
            resource = default;
            key = null;
            return false;
        }

        resource = (ResourceName)resourceName;
        return true;
    }

    private static bool TryResolveSourceResource(
        IResourceControlContext context,
        ResourceName sourceResource,
        out ResourceManifest? manifest,
        out ResourceDependencyObservation? observation,
        out string? failure)
    {
        manifest = null;
        observation = null;
        failure = null;

        for (int index = 0; index < context.ObservedDependencies.Count; index++)
        {
            ResourceDependencyObservation candidate = context.ObservedDependencies[index];
            if (candidate.Resource != sourceResource)
            {
                continue;
            }

            if (observation is not null)
            {
                failure = $"Mount source resource '{sourceResource}' is ambiguous across application references.";
                return false;
            }

            observation = candidate;
        }

        if (observation is null)
        {
            failure = $"Mount source resource '{sourceResource}' is not a declared dependency of " +
                $"resource '{context.Resource.Name}'. Add a resource reference so its observed endpoint " +
                "is available before source resolution.";
            return false;
        }

        for (int index = 0; index < context.Model.Manifests.Count; index++)
        {
            ResourceManifest candidate = context.Model.Manifests[index];
            if (candidate.Name == observation.Resource && candidate.Application == observation.Application)
            {
                manifest = candidate;
                break;
            }
        }

        if (manifest is null)
        {
            failure = $"Mount source resource '{observation.Application}/{observation.Resource}' has no " +
                "manifest in the active application model.";
            return false;
        }

        if (observation.State != ResourceLifecycle.Running)
        {
            failure = $"Mount source resource '{observation.Application}/{observation.Resource}' is " +
                $"'{observation.State}', not Running.";
            return false;
        }

        return true;
    }

    private static bool TryResolveControlPlaneEndpoint(
        ResourceManifest manifest,
        ResourceDependencyObservation observation,
        out Uri? endpoint,
        out string? failure)
    {
        for (int index = 0; index < observation.Endpoints.Count; index++)
        {
            ResourceEndpoint candidate = observation.Endpoints[index];
            if (!string.Equals(
                    candidate.Name,
                    manifest.ControlPlane.Endpoint,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (!Uri.TryCreateEndpoint(
                    candidate.Scheme,
                    candidate.Host,
                    candidate.Port,
                    path: null,
                    out endpoint))
            {
                break;
            }

            failure = null;
            return true;
        }

        endpoint = null;
        failure = $"Mount source resource '{manifest.Application}/{manifest.Name}' has no observed " +
            $"'{manifest.ControlPlane.Endpoint}' control-plane endpoint.";
        return false;
    }

    private static bool IsCertificateMount(IResourceControlContext context, string mount)
    {
        int descriptorIndex = IndexOfDescriptor(context.Model.Descriptors, context.Descriptor);
        ResourceManifest manifest = context.Model.Manifests[descriptorIndex];
        for (int index = 0; index < manifest.Endpoints.Count; index++)
        {
            if (string.Equals(manifest.Endpoints[index].Certificate, mount, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] SerializeConfiguration(
        IReadOnlyDictionary<string, string?> configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var keys = new List<string>(configuration.Keys);
        keys.Sort(StringComparer.Ordinal);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            for (int index = 0; index < keys.Count; index++)
            {
                string key = keys[index];
                string? value = configuration[key];
                if (value is null)
                {
                    writer.WriteNull(key);
                }
                else
                {
                    writer.WriteString(key, value);
                }
            }

            writer.WriteEndObject();
        }

        return stream.ToArray();
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
    /// Resolves the address on which an application's configured gateway control plane binds.
    /// The default requests an ephemeral IPv4 loopback port.
    /// </summary>
    /// <param name="model">The application whose control plane will be served.</param>
    /// <param name="cancellationToken">Signals that address resolution should be abandoned.</param>
    /// <returns>The absolute HTTP endpoint to bind.</returns>
    protected virtual ValueTask<Uri> ResolveControlPlaneAddressAsync(
        IApplicationModel model,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new Uri("http://127.0.0.1:0", UriKind.Absolute));
    }

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

    /// <summary>
    /// Resolves an application's own SecretStore endpoint when it is not available from the
    /// active observed-state view. Platform gateways override this for stable native service
    /// discovery, including one-shot trust commands that do not open a reconcile session.
    /// </summary>
    /// <param name="model">The application that owns the SecretStore.</param>
    /// <param name="store">The application's own SecretStore manifest.</param>
    /// <param name="endpoint">The platform-native control-plane endpoint when resolved.</param>
    /// <returns><see langword="true"/> when an endpoint was resolved; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="model"/> or <paramref name="store"/> is <see langword="null"/>.
    /// </exception>
    protected virtual bool TryResolveOwnSecretStoreEndpoint(
        IApplicationModel model,
        ResourceManifest store,
        out Uri? endpoint)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(store);
        endpoint = null;
        return false;
    }

    /// <inheritdoc/>
    public IReadOnlyList<TrustedIssuer> GetTrustedIssuers(ApplicationName application)
    {
        lock (_trustStatesGate)
        {
            return GetTrustStateCore(application).Snapshot();
        }
    }

    /// <inheritdoc/>
    string IResourceCommandCredentialProvider.GetResourceCommandCredential(
        ApplicationName application,
        ResourceName resource)
    {
        for (int index = 0; index < _activeModels.Count; index++)
        {
            IApplicationModel model = _activeModels[index];
            if (model.Name == application)
            {
                return GetOrIssueBootstrapToken(model, resource);
            }
        }

        throw new InvalidOperationException(
            $"Application '{application}' is not active in gateway '{Name}'.");
    }

    /// <inheritdoc/>
    public async Task ExecuteCommandAsync(
        IApplicationModel model,
        GatewayCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(command);

        if (command.Mode == GatewayRunMode.TrustIssue)
        {
            string token = await IssueDeveloperTokenAsync(
                    model,
                    command.DeveloperName!,
                    cancellationToken)
                .ConfigureAwait(false);
            await Console.Out.WriteLineAsync(token.AsMemory(), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (command.Mode == GatewayRunMode.TrustAdd)
        {
            ApplicationExportDocument export = await ReadPeerExportAsync(
                    model,
                    command.ExportSource!,
                    cancellationToken)
                .ConfigureAwait(false);
            await AddTrustedIssuerAsync(
                    model,
                    command.PeerName!,
                    export,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        throw new NotSupportedException(
            $"Gateway '{Name}' does not implement command mode '{command.Mode}'.");
    }

    /// <inheritdoc/>
    public async Task<string> IssueDeveloperTokenAsync(
        IApplicationModel model,
        string developerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(developerName);

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _options.ValidateCommon();
            ApplicationTrustState trust = await EnsureTrustStateAsync(model, cancellationToken)
                .ConfigureAwait(false);
            return trust.Issue(
                "cohesion-export",
                developerName,
                _options.DeveloperTokenLifetime,
                _options.TimeProvider.GetUtcNow());
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <inheritdoc/>
    public async Task AddTrustedIssuerAsync(
        IApplicationModel model,
        string peerName,
        ApplicationExportDocument export,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(peerName);
        ArgumentNullException.ThrowIfNull(export);
        _ = export.ToModel();

        if (!string.Equals(peerName, export.Application, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Trust peer '{peerName}' does not match export application '{export.Application}'.");
        }

        if (string.Equals(export.Application, model.Name.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Application '{model.Name}' cannot add its own export as a peer trust grant.");
        }

        JsonElement publicKey = export.TrustKey ?? throw new InvalidDataException(
            $"Application export '{export.Application}' has no trustKey.");
        var issuer = new TrustedIssuer(export.Application, publicKey);

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _options.ValidateCommon();
            ApplicationTrustState trust = await EnsureTrustStateAsync(model, cancellationToken)
                .ConfigureAwait(false);
            bool stored = false;
            if (TryGetOwnSecretStoreEndpoint(model, out ResourceManifest? store, out Uri? endpoint))
            {
                if (!CanSendCredential(model, endpoint!, out string? securityFailure))
                {
                    throw new InvalidOperationException(securityFailure);
                }

                string credential = GetOrIssueBootstrapToken(model, store!.Name);
                try
                {
                    await _options.StoreClient.StoreTrustedIssuerAsync(
                            endpoint!,
                            credential,
                            model.Owner,
                            issuer.Issuer,
                            Encoding.UTF8.GetBytes(issuer.PublicKey.GetRawText()),
                            cancellationToken)
                        .ConfigureAwait(false);
                    stored = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (
                    model.Environment.IsDevelopment &&
                    exception is HttpRequestException or InvalidDataException or NotSupportedException)
                {
                    // Development is the only environment allowed to fall back to the
                    // application-local trusted-issuers document.
                }
            }

            if (!stored)
            {
                if (!model.Environment.IsDevelopment)
                {
                    throw new InvalidOperationException(
                        $"Application '{model.Name}' has no reachable own SecretStore endpoint " +
                        $"for trust grant '{peerName}'. Local fallback is Development-only.");
                }

                await TrustedIssuerDocument.WriteFileAsync(
                        GetTrustedIssuersPath(model.Name),
                        MergeTrustedIssuers(trust.Snapshot(), issuer),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            trust.AddOrReplace(issuer);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <inheritdoc/>
    public async Task RotateTrustKeyAsync(
        IApplicationModel model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _options.ValidateCommon();
            ApplicationTrustState trust = await EnsureTrustStateAsync(model, cancellationToken)
                .ConfigureAwait(false);
            GatewayTrustKey replacement = await RotateTrustKeyCoreAsync(
                    model.Name,
                    cancellationToken)
                .ConfigureAwait(false);
            trust.ReplaceKey(replacement);
            RemoveBootstrapCredentials(model.Name);
            if (model.Environment.IsDevelopment)
            {
                await TrustedIssuerDocument.WriteFileAsync(
                        GetTrustedIssuersPath(model.Name),
                        trust.Snapshot(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task<ApplicationExportDocument> ReadPeerExportAsync(
        IApplicationModel model,
        string source,
        CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out Uri? endpoint) &&
            (string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            if (!CanSendCredential(model, endpoint, out string? securityFailure))
            {
                throw new InvalidOperationException(securityFailure);
            }

            IControlPlaneClient client = _options.ControlPlaneClient ?? throw new InvalidOperationException(
                $"Trust export source '{source}' is a control-plane URI, but no " +
                $"{nameof(ApplicationGatewayOptions.ControlPlaneClient)} is configured.");
            return await client.GetApplicationAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ApplicationExportDocument.Load(Path.GetFullPath(source));
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
                await RefreshTrustedIssuersAsync(cancellationToken).ConfigureAwait(false);
                // Run-mode cancellation is also the application lifetime signal. Once every
                // resource is ready, finish publishing the matching discovery snapshot; the
                // subsequent StopAsync call withdraws it.
                await PublishApplicationExportsAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
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
            await RefreshTrustedIssuersAsync(cancellationToken).ConfigureAwait(false);
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

    /// <inheritdoc/>
    public ValueTask<ExternalResourceResolution> ResolveInSetAsync(
        IReadOnlyList<IApplicationModel> models,
        ExternalResourceResolutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        ExternalResourceDeclaration declaration = context.Declaration;
        for (int modelIndex = 0; modelIndex < models.Count; modelIndex++)
        {
            IApplicationModel model = models[modelIndex];
            if (model.Name != declaration.Application)
            {
                continue;
            }

            for (int resourceIndex = 0; resourceIndex < model.Resources.Count; resourceIndex++)
            {
                IApplicationResource resource = model.Resources[resourceIndex];
                if (resource.Name != declaration.Name
                    || IsExternalPlan(model.Plans[resourceIndex]))
                {
                    continue;
                }

                IReadOnlyList<ResourceEndpoint> endpoints =
                    GetApplicationState(model).GetObservedEndpoints(resource.Id);
                if (endpoints.Count == 0)
                {
                    return ValueTask.FromResult(ExternalResourceResolution.Unresolved(
                        $"Sibling application '{model.Name}' has not observed endpoints for " +
                        $"resource '{resource.Name}' yet."));
                }

                return ValueTask.FromResult(new ExternalResourceResolution(
                    true,
                    endpoints,
                    ResourceManifestCanonicalizer.ComputeHash(model.Manifests[resourceIndex]),
                    ApplicationExportDocument.CurrentSchemaVersion,
                    $"Resolved directly from sibling application '{model.Name}'."));
            }

            return ValueTask.FromResult(ExternalResourceResolution.Unresolved(
                $"Sibling application '{model.Name}' does not contain resource " +
                $"'{declaration.Name}'."));
        }

        return ValueTask.FromResult(ExternalResourceResolution.Unresolved(
            $"Application '{declaration.Application}' is not a sibling in this application set."));
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

                ValidateResource(model, descriptor, plan);
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
            if (!SessionMatches(models) &&
                !await TryReplaceCommandDeclarationsAsync(models, cancellationToken).ConfigureAwait(false))
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
            await InitializeApplicationStateAsync(models, cancellationToken).ConfigureAwait(false);

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
        // Every pass starts a fresh credential generation. A resource receives exactly one
        // token for the pass, and all gateway-side calls to that resource reuse that token.
        lock (_credentialGate)
        {
            _bootstrapCredentials.Clear();
        }

        await RefreshAvailableTrustedIssuersAsync(cancellationToken).ConfigureAwait(false);

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
                await ApplyResourceCommandsAsync(item, cancellationToken).ConfigureAwait(false);
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
                await ApplyResourceCommandsAsync(item, cancellationToken).ConfigureAwait(false);
                _admitted.Add(item.Key);
                if (IsOwnSecretStore(model, descriptor))
                {
                    await RefreshTrustedIssuersAsync(
                            model,
                            requireEndpoint: true,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

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
            var commands = new List<ResourceCommandObservation>();

            for (int resourceIndex = 0; resourceIndex < model.Descriptors.Count; resourceIndex++)
            {
                IApplicationResource resource = model.Descriptors[resourceIndex].Resource;
                commands.AddRange(state.GetCommandObservations(resource.Id));
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
                GetTrustState(model.Name).PublicJwk,
                commands);
            await PublishApplicationExportAsync(document, cancellationToken).ConfigureAwait(false);

            if (_options.ControlPlane is not null)
            {
                IApplicationGatewayControlPlane controlPlane = await GetOrStartControlPlaneAsync(
                        model,
                        state,
                        cancellationToken)
                    .ConfigureAwait(false);
                await controlPlane.PublishAsync(document, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<IApplicationGatewayControlPlane> GetOrStartControlPlaneAsync(
        IApplicationModel model,
        IApplicationResourceStateManager state,
        CancellationToken cancellationToken)
    {
        if (_controlPlanes.TryGetValue(model.Name, out IApplicationGatewayControlPlane? existing))
        {
            return existing;
        }

        IApplicationGatewayControlPlaneFactory factory = _options.ControlPlane
            ?? throw new InvalidOperationException("No gateway control-plane factory is configured.");
        IApplicationGatewayControlPlane controlPlane = factory.Create(model.Name)
            ?? throw new InvalidOperationException(
                $"The gateway control-plane factory returned null for application '{model.Name}'.");
        Uri address = await ResolveControlPlaneAddressAsync(model, cancellationToken).ConfigureAwait(false);
        if (!address.IsAbsoluteUri ||
            (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"Gateway control-plane address '{address}' must be an absolute HTTP endpoint.");
        }

        try
        {
            await controlPlane.StartAsync(
                    address,
                    model,
                    state,
                    this,
                    cancellationToken)
                .ConfigureAwait(false);
            _controlPlanes.Add(model.Name, controlPlane);
            return controlPlane;
        }
        catch
        {
            try
            {
                await controlPlane.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Preserve the actionable startup failure.
            }

            throw;
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

            Exception? controlPlaneFailure = await StopControlPlanesAsync(cancellationToken)
                .ConfigureAwait(false);
            failure ??= controlPlaneFailure;

            Exception? exportFailure = await RemoveApplicationExportsAsync(cancellationToken)
                .ConfigureAwait(false);
            failure ??= exportFailure;
            exportsRemoved = controlPlaneFailure is null && exportFailure is null;
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
                foreach (IApplicationModel model in models)
                {
                    if (model.Commands.Count != 0)
                    {
                        await EnsureTrustStateAsync(model, cancellationToken).ConfigureAwait(false);
                    }
                }
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
                    await DeleteResourceCommandsAsync(item, cancellationToken).ConfigureAwait(false);
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

            Exception? controlPlaneFailure = await StopControlPlanesAsync(cancellationToken)
                .ConfigureAwait(false);
            failure ??= controlPlaneFailure;

            Exception? exportFailure = await RemoveApplicationExportsAsync(cancellationToken)
                .ConfigureAwait(false);
            failure ??= exportFailure;
            exportsRemoved = controlPlaneFailure is null && exportFailure is null;
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

    private async Task<Exception?> StopControlPlanesAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        for (int index = 0; index < _activeModels.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplicationName application = _activeModels[index].Name;
            if (!_controlPlanes.TryGetValue(application, out IApplicationGatewayControlPlane? controlPlane))
            {
                continue;
            }

            try
            {
                await controlPlane.StopAsync(cancellationToken).ConfigureAwait(false);
                _controlPlanes.Remove(application);
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

        return failure;
    }

    private async Task RollBackAsync(CancellationToken cancellationToken)
    {
        for (int index = _realized.Count - 1; index >= 0; index--)
        {
            RealizedResource realized = _realized[index];
            try
            {
                await RollBackResourceCommandsAsync(realized.Item, cancellationToken).ConfigureAwait(false);
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

        Exception? controlPlaneFailure = await StopControlPlanesAsync(CancellationToken.None)
            .ConfigureAwait(false);
        Exception? exportFailure = await RemoveApplicationExportsAsync(CancellationToken.None)
            .ConfigureAwait(false);
        if (controlPlaneFailure is null && exportFailure is null)
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

    private async Task InitializeApplicationStateAsync(
        IReadOnlyList<IApplicationModel> models,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < models.Count; index++)
        {
            IApplicationModel model = models[index];
            await EnsureTrustStateAsync(model, cancellationToken).ConfigureAwait(false);
            _parameters.Add(
                model.Name,
                await ReadParametersAsync(model, cancellationToken).ConfigureAwait(false));
        }
    }

    private async Task<ApplicationTrustState> EnsureTrustStateAsync(
        IApplicationModel model,
        CancellationToken cancellationToken)
    {
        lock (_trustStatesGate)
        {
            if (_trust.TryGetValue(model.Name, out ApplicationTrustState? existing))
            {
                return existing;
            }
        }

        GatewayTrustKey key = await LoadOrCreateTrustKeyAsync(
                model.Name,
                cancellationToken)
            .ConfigureAwait(false);
        var created = new ApplicationTrustState(model.Name, Name, key);
        try
        {
            if (model.Environment.IsDevelopment)
            {
                string path = GetTrustedIssuersPath(model.Name);
                if (File.Exists(path))
                {
                    byte[] content = await File.ReadAllBytesAsync(path, cancellationToken)
                        .ConfigureAwait(false);
                    try
                    {
                        created.ReplacePeers(TrustedIssuerDocument.Parse(content));
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(content);
                    }
                }
            }

            lock (_trustStatesGate)
            {
                if (_trust.TryGetValue(model.Name, out ApplicationTrustState? existing))
                {
                    created.Dispose();
                    return existing;
                }

                _trust.Add(model.Name, created);
                return created;
            }
        }
        catch
        {
            created.Dispose();
            throw;
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadParametersAsync(
        IApplicationModel model,
        CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        string path = _options.ParameterFile is null
            ? Path.Combine(GetApplicationDirectory(model.Name), "parameters.json")
            : Path.GetFullPath(_options.ParameterFile);
        if (File.Exists(path))
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            byte[] persisted = await File.ReadAllBytesAsync(path, cancellationToken)
                .ConfigureAwait(false);
            byte[]? plaintext = null;
            try
            {
                plaintext = OperatingSystem.IsWindows()
                    ? ProtectedData.Unprotect(
                        persisted,
                        optionalEntropy: null,
                        DataProtectionScope.CurrentUser)
                    : persisted;
                using JsonDocument document = JsonDocument.Parse(plaintext);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException(
                        $"Gateway parameter file '{path}' must contain a JSON object.");
                }

                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidDataException(
                            $"Gateway parameter '{property.Name}' in '{path}' must be a string.");
                    }

                    parameters.Add(property.Name, property.Value.GetString()!);
                }
            }
            catch (CryptographicException exception) when (OperatingSystem.IsWindows())
            {
                throw new InvalidDataException(
                    $"Gateway parameter file '{path}' could not be decrypted for the current user.",
                    exception);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(persisted);
                if (plaintext is not null && !ReferenceEquals(plaintext, persisted))
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
        }

        foreach ((string name, string value) in _options.Parameters)
        {
            parameters[name] = value;
        }

        return parameters;
    }

    private async Task RefreshTrustedIssuersAsync(CancellationToken cancellationToken)
    {
        for (int index = 0; index < _activeModels.Count; index++)
        {
            await RefreshTrustedIssuersAsync(
                    _activeModels[index],
                    requireEndpoint: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task RefreshAvailableTrustedIssuersAsync(CancellationToken cancellationToken)
    {
        for (int index = 0; index < _activeModels.Count; index++)
        {
            await RefreshTrustedIssuersAsync(
                    _activeModels[index],
                    requireEndpoint: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task RefreshTrustedIssuersAsync(
        IApplicationModel model,
        bool requireEndpoint,
        CancellationToken cancellationToken)
    {
        if (!TryGetOwnSecretStoreEndpoint(model, out ResourceManifest? store, out Uri? endpoint))
        {
            if (requireEndpoint && store is not null && !model.Environment.IsDevelopment)
            {
                throw new InvalidOperationException(
                    $"Application '{model.Name}' could not load TrustedIssuers because its own " +
                    $"SecretStore '{store.Name}' has no observed control-plane endpoint.");
            }

            return;
        }

        if (!CanSendCredential(model, endpoint!, out string? securityFailure))
        {
            if (model.Environment.IsDevelopment)
            {
                return;
            }

            throw new InvalidOperationException(securityFailure);
        }

        string credential = GetOrIssueBootstrapToken(model, store!.Name);
        try
        {
            ReadOnlyMemory<byte> content = await _options.StoreClient
                .ReadSecretAsync(endpoint!, credential, TrustedIssuersFileName, cancellationToken)
                .ConfigureAwait(false);
            GetTrustState(model.Name).ReplacePeers(TrustedIssuerDocument.Parse(content));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception) when (
            exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // A production application with no peer grants has no persisted document yet.
            // Development retains its explicitly permitted local fallback until the
            // application's store contains a replacement document.
            if (!model.Environment.IsDevelopment)
            {
                GetTrustState(model.Name).ReplacePeers(Array.Empty<TrustedIssuer>());
            }
        }
        catch (Exception exception) when (
            model.Environment.IsDevelopment &&
            exception is HttpRequestException or InvalidDataException or JsonException or
                NotSupportedException)
        {
            // The signed design allows only Development to retain its local fallback while
            // the application's own SecretStore is unavailable.
        }
        catch (Exception exception) when (
            exception is HttpRequestException or InvalidDataException or JsonException or
                NotSupportedException)
        {
            throw new InvalidOperationException(
                $"Application '{model.Name}' could not load TrustedIssuers from its own " +
                $"SecretStore '{store.Name}'.",
                exception);
        }
    }

    private static bool IsOwnSecretStore(
        IApplicationModel model,
        IApplicationResourceDescriptor descriptor)
    {
        for (int index = 0; index < model.Descriptors.Count; index++)
        {
            if (model.Descriptors[index].Resource.Id == descriptor.Resource.Id)
            {
                ResourceManifest manifest = model.Manifests[index];
                return manifest.Application == model.Name &&
                    string.Equals(manifest.Kind, "SecretStore", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    private bool TryGetOwnSecretStoreEndpoint(
        IApplicationModel model,
        out ResourceManifest? store,
        out Uri? endpoint)
    {
        for (int index = 0; index < model.Manifests.Count; index++)
        {
            ResourceManifest candidate = model.Manifests[index];
            if (candidate.Application != model.Name ||
                !string.Equals(candidate.Kind, "SecretStore", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            store = candidate;
            if (_activeModels.Count != 0)
            {
                IApplicationResourceStateManager state = GetApplicationState(model);
                IApplicationResource resource = model.Descriptors[index].Resource;
                if (state.GetState(resource.Id) == ResourceLifecycle.Running)
                {
                    IReadOnlyList<ResourceEndpoint> observed = state.GetObservedEndpoints(resource.Id);
                    for (int endpointIndex = 0; endpointIndex < observed.Count; endpointIndex++)
                    {
                        ResourceEndpoint candidateEndpoint = observed[endpointIndex];
                        if (string.Equals(
                                candidateEndpoint.Name,
                                candidate.ControlPlane.Endpoint,
                                StringComparison.Ordinal) &&
                            Uri.TryCreateEndpoint(
                                candidateEndpoint.Scheme,
                                candidateEndpoint.Host,
                                candidateEndpoint.Port,
                                path: null,
                                out endpoint))
                        {
                            return true;
                        }
                    }
                }
            }

            if (TryResolveOwnSecretStoreEndpoint(model, candidate, out endpoint))
            {
                if (endpoint is null || !endpoint.IsEndpoint)
                {
                    throw new InvalidOperationException(
                        $"Gateway '{Name}' resolved invalid own SecretStore endpoint '{endpoint}' " +
                        $"for application '{model.Name}'.");
                }

                return true;
            }

            if (model.Environment.IsDevelopment)
            {
                for (int endpointIndex = 0; endpointIndex < candidate.Endpoints.Count; endpointIndex++)
                {
                    ResourceManifestEndpoint declared = candidate.Endpoints[endpointIndex];
                    if (string.Equals(
                            declared.Name,
                            candidate.ControlPlane.Endpoint,
                            StringComparison.Ordinal) &&
                        declared.DevPort is int devPort &&
                        Uri.TryCreateEndpoint(
                            declared.Scheme,
                            "127.0.0.1",
                            devPort,
                            path: null,
                            out endpoint))
                    {
                        return true;
                    }
                }
            }

            endpoint = null;
            return false;
        }

        store = null;
        endpoint = null;
        return false;
    }

    private string GetOrIssueBootstrapToken(IApplicationModel model, ResourceName audience)
    {
        lock (_credentialGate)
        {
            var key = new BootstrapCredentialKey(model.Name, audience);
            if (_bootstrapCredentials.TryGetValue(key, out string? existing))
            {
                return existing;
            }

            ApplicationTrustState trust = GetTrustState(model.Name);
            string issued = trust.Issue(
                audience.ToString(),
                Name.ToString(),
                _options.BootstrapCredentialLifetime,
                _options.TimeProvider.GetUtcNow());
            _bootstrapCredentials.Add(key, issued);
            return issued;
        }
    }

    private async Task<GatewayTrustKey> LoadOrCreateTrustKeyAsync(
        ApplicationName application,
        CancellationToken cancellationToken)
    {
        IGatewayTrustKeyRepository repository = _options.TrustKeyRepository
            ?? new GatewayTrustKeyStore(GetStateRoot());
        ECDsa signingKey = await repository
            .LoadOrCreateAsync(application, Name, cancellationToken)
            .ConfigureAwait(false);
        return CreateOwnedTrustKey(signingKey);
    }

    private async Task<GatewayTrustKey> RotateTrustKeyCoreAsync(
        ApplicationName application,
        CancellationToken cancellationToken)
    {
        IGatewayTrustKeyRepository repository = _options.TrustKeyRepository
            ?? new GatewayTrustKeyStore(GetStateRoot());
        ECDsa signingKey = await repository
            .RotateAsync(application, Name, cancellationToken)
            .ConfigureAwait(false);
        return CreateOwnedTrustKey(signingKey);
    }

    private static GatewayTrustKey CreateOwnedTrustKey(ECDsa signingKey)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        try
        {
            return new GatewayTrustKey(signingKey);
        }
        catch
        {
            signingKey.Dispose();
            throw;
        }
    }

    private static bool CanSendCredential(
        IApplicationModel model,
        Uri endpoint,
        out string? failure)
    {
        if (string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            (model.Environment.IsDevelopment &&
             string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             endpoint.IsLoopback))
        {
            failure = null;
            return true;
        }

        failure = $"Application '{model.Name}' refuses to send a bearer credential to " +
            $"non-TLS endpoint '{endpoint}'. Use HTTPS, or loopback HTTP in Development.";
        return false;
    }

    private IControlPlaneClient? CreateControlPlaneClient(IApplicationModel model)
    {
        IControlPlaneClient? client = _options.ControlPlaneClient;
        if (client is not IAuthenticatedControlPlaneClient authenticated)
        {
            return client;
        }

        ApplicationTrustState trust = GetTrustState(model.Name);
        string credential = trust.Issue(
            "cohesion-export",
            Name.ToString(),
            _options.DeveloperTokenLifetime,
            _options.TimeProvider.GetUtcNow(),
            allowControlPlaneCommands: true);
        return new AuthenticatedControlPlaneClient(
            authenticated,
            credential,
            GetTrustedIssuers(model.Name));
    }

    private void RemoveBootstrapCredentials(ApplicationName application)
    {
        lock (_credentialGate)
        {
            var keys = new List<BootstrapCredentialKey>();
            foreach (BootstrapCredentialKey key in _bootstrapCredentials.Keys)
            {
                if (key.Application == application)
                {
                    keys.Add(key);
                }
            }

            for (int index = 0; index < keys.Count; index++)
            {
                _bootstrapCredentials.Remove(keys[index]);
            }
        }
    }

    private ApplicationTrustState GetTrustState(ApplicationName application)
    {
        lock (_trustStatesGate)
        {
            return GetTrustStateCore(application);
        }
    }

    private ApplicationTrustState GetTrustStateCore(ApplicationName application)
    {
        return _trust.TryGetValue(application, out ApplicationTrustState? trust)
            ? trust
            : throw new InvalidOperationException(
                $"Trust has not been initialized for application '{application}' on gateway '{Name}'.");
    }

    private IReadOnlyDictionary<string, string> GetParameters(ApplicationName application)
    {
        return _parameters.TryGetValue(application, out IReadOnlyDictionary<string, string>? parameters)
            ? parameters
            : _options.Parameters as IReadOnlyDictionary<string, string>
                ?? new Dictionary<string, string>(_options.Parameters, StringComparer.Ordinal);
    }

    private string GetStateRoot()
    {
        string? stateDirectory = (_options as LocalGatewayOptions)?.StateDirectory;
        return Path.GetFullPath(
            stateDirectory ??
            _options.ExportDirectory ??
            Path.Combine(Environment.CurrentDirectory, ".cohesion"));
    }

    private string GetApplicationDirectory(ApplicationName application) =>
        Path.Combine(GetStateRoot(), application.ToString());

    private string GetTrustedIssuersPath(ApplicationName application) =>
        Path.Combine(GetApplicationDirectory(application), "trust", TrustedIssuersFileName);

    private static IReadOnlyList<TrustedIssuer> MergeTrustedIssuers(
        IReadOnlyList<TrustedIssuer> current,
        TrustedIssuer added)
    {
        var issuers = new Dictionary<string, TrustedIssuer>(StringComparer.Ordinal);
        for (int index = 0; index < current.Count; index++)
        {
            TrustedIssuer issuer = current[index];
            issuers[issuer.Issuer] = issuer;
        }

        issuers[added.Issuer] = added;
        var names = new List<string>(issuers.Keys);
        names.Sort(StringComparer.Ordinal);
        var result = new TrustedIssuer[names.Count];
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = issuers[names[index]];
        }

        return result;
    }

    private void ResetSession()
    {
        lock (_trustStatesGate)
        {
            foreach (ApplicationTrustState trust in _trust.Values)
            {
                trust.Dispose();
            }

            _trust.Clear();
        }

        for (int index = 0; index < _applicationStates.Count; index++)
        {
            _applicationStates[index].OwnedState?.Dispose();
        }

        lock (_credentialGate)
        {
            _bootstrapCredentials.Clear();
        }
        _controlPlanes.Clear();
        _parameters.Clear();
        _artifacts.Clear();
        _admitted.Clear();
        _appliedCommands.Clear();
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
                    OrderTopologically(model);
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
        IApplicationModel model)
    {
        IReadOnlyList<IApplicationResourceDescriptor> descriptors = model.Descriptors;
        var ordered = new List<IApplicationResourceDescriptor>(descriptors.Count);
        var seen = new HashSet<IApplicationResourceDescriptor>();

        // A production gateway learns peer keys from its own SecretStore. Prefer that
        // resource among otherwise independent roots so a first-pass remote resolution
        // never observes the self-only trust snapshot. Visit still honors every explicit
        // dependency of the store before admitting it.
        foreach (IApplicationResourceDescriptor descriptor in descriptors)
        {
            if (IsOwnSecretStore(model, descriptor))
            {
                Visit(descriptor, seen, ordered);
            }
        }

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

    private readonly record struct BootstrapCredentialKey(
        ApplicationName Application,
        ResourceName Resource);

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
