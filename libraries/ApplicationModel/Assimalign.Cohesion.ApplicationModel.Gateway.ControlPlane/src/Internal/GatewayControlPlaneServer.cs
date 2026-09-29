using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;

using CohesionHttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using HttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Internal;

internal sealed class GatewayControlPlaneServer : IApplicationGatewayControlPlane
{
    private const long MaximumCommandBytes = 1024 * 1024;

    private readonly ApplicationName _application;
    private readonly string? _metadataPath;
    private readonly TimeProvider _timeProvider;
    private readonly IReadOnlyList<IResourceCommandDispatcher> _dispatchers;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _commandDispatch = new(1, 1);
    private readonly object _documentGate = new();
    private readonly object _commandGate = new();
    private readonly object _connectionGate = new();
    private readonly Dictionary<ResourceName, Dictionary<string, ControlPlaneCommandObservation>> _commands = new();
    // The latest observation may reject deletion while the provider still holds the owned key.
    private readonly Dictionary<(ResourceName Resource, string Id), (string Kind, string Key, string Owner)>
        _commandOwnership = new();
    private readonly HashSet<Task> _connections = new();
    private readonly ControlPlaneRouter _router;

    private ApplicationExportDocument? _document;
    private IApplicationModel? _model;
    private IApplicationResourceStateManager? _state;
    private ITrustedIssuerProvider? _trustedIssuers;
    private IReadOnlyList<IApplicationCallerAuthenticator> _callers = Array.Empty<IApplicationCallerAuthenticator>();
    private IResourceCommandCredentialProvider? _commandCredentials;
    private IResourceTransportTrustProvider? _transportTrust;
    private TcpConnectionListener? _tcpListener;
    private HttpConnectionListener? _httpListener;
    private CancellationTokenSource? _serverCancellation;
    private Task? _acceptLoop;
    private Uri? _address;
    private EventHandler? _processExitHandler;

    public GatewayControlPlaneServer(
        ApplicationName application,
        string? metadataDirectory,
        TimeProvider timeProvider,
        IReadOnlyList<IResourceCommandDispatcher> dispatchers)
    {
        _application = application;
        _metadataPath = metadataDirectory is null
            ? null
            : GetMetadataPath(metadataDirectory, application);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _dispatchers = dispatchers ?? throw new ArgumentNullException(nameof(dispatchers));
        // Tried in this order, which is the order Web.Routing's inbound-precedence sort gave these
        // templates before the dependency was removed: each template has a distinct segment count,
        // and PUT precedes DELETE on the shared command template, so a 405 lists "PUT, DELETE".
        _router = new ControlPlaneRouter(
            new ControlPlaneRoute(
                CohesionHttpMethod.Get,
                "/cohesion/v1/application",
                HandleApplicationAsync),
            new ControlPlaneRoute(
                CohesionHttpMethod.Get,
                "/cohesion/v1/resources/{name}",
                HandleResourceAsync),
            new ControlPlaneRoute(
                CohesionHttpMethod.Get,
                "/cohesion/v1/resources/{name}/commands",
                HandleGetCommandsAsync),
            new ControlPlaneRoute(
                CohesionHttpMethod.Put,
                "/cohesion/v1/resources/{name}/commands/{id}",
                HandlePutCommandAsync),
            new ControlPlaneRoute(
                CohesionHttpMethod.Delete,
                "/cohesion/v1/resources/{name}/commands/{id}",
                HandleDeleteCommandAsync));
    }

    public Uri Address => _address ?? throw new InvalidOperationException(
        $"Control plane for application '{_application}' has not started.");

    public async Task StartAsync(
        Uri address,
        IApplicationModel model,
        IApplicationResourceStateManager state,
        ITrustedIssuerProvider trustedIssuers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(trustedIssuers);
        if (model.Name != _application)
        {
            throw new ArgumentException(
                $"Control plane for '{_application}' cannot serve application '{model.Name}'.",
                nameof(model));
        }

        IPEndPoint listenEndpoint = GetListenEndpoint(address);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_httpListener is not null)
            {
                throw new InvalidOperationException(
                    $"Control plane for application '{_application}' is already running.");
            }

            DeleteMetadata();
            TcpConnectionListener tcpListener = TcpConnectionListener.Create(
                options => options.EndPoint = listenEndpoint);
            HttpConnectionListener httpListener = HttpConnectionListener.Create(options =>
                options.UseHttp1(
                    tcpListener,
                    http1 => http1.Limits.MaxRequestBodySize = MaximumCommandBytes));

            try
            {
                await httpListener.BindAsync(cancellationToken).ConfigureAwait(false);
                var bound = (IPEndPoint)tcpListener.EndPoint;
                _model = model;
                _state = state;
                _trustedIssuers = trustedIssuers;
                _callers = SnapshotCallers(model);
                _commandCredentials = trustedIssuers as IResourceCommandCredentialProvider;
                _transportTrust = trustedIssuers as IResourceTransportTrustProvider;
                _tcpListener = tcpListener;
                _httpListener = httpListener;
                _serverCancellation = new CancellationTokenSource();
                _address = Uri.CreateEndpoint(Uri.UriSchemeHttp, address.IdnHost, bound.Port);
                _processExitHandler = (_, _) => DeleteMetadata();
                AppDomain.CurrentDomain.ProcessExit += _processExitHandler;
                _acceptLoop = AcceptLoopAsync(httpListener, _serverCancellation.Token);
            }
            catch
            {
                await httpListener.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task PublishAsync(
        ApplicationExportDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        _ = document.ToModel();
        if (!string.Equals(document.Application, _application.ToString(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Control plane for '{_application}' cannot publish export '{document.Application}'.",
                nameof(document));
        }

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = Address;
            ApplicationExportDocument? previous;
            lock (_documentGate)
            {
                previous = _document;
                _document = document;
            }

            try
            {
                await WriteMetadataAsync(document, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                lock (_documentGate)
                {
                    if (ReferenceEquals(_document, document))
                    {
                        _document = previous;
                    }
                }

                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        CancellationTokenSource? serverCancellation = _serverCancellation;
        HttpConnectionListener? listener = _httpListener;
        Task? acceptLoop = _acceptLoop;
        try
        {
            serverCancellation?.Cancel();
            if (listener is not null)
            {
                await listener.DisposeAsync().ConfigureAwait(false);
            }

            if (acceptLoop is not null)
            {
                await acceptLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            Task[] connections;
            lock (_connectionGate)
            {
                connections = new Task[_connections.Count];
                _connections.CopyTo(connections);
            }

            if (connections.Length != 0)
            {
                await Task.WhenAll(connections).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                if (_processExitHandler is not null)
                {
                    AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
                    _processExitHandler = null;
                }

                serverCancellation?.Dispose();
                _serverCancellation = null;
                _httpListener = null;
                _tcpListener = null;
                _acceptLoop = null;
                _address = null;
                _model = null;
                _state = null;
                _trustedIssuers = null;
                _callers = Array.Empty<IApplicationCallerAuthenticator>();
                _commandCredentials = null;
                lock (_documentGate)
                {
                    _document = null;
                }

                lock (_commandGate)
                {
                    _commands.Clear();
                    _commandOwnership.Clear();
                }

                DeleteMetadata();
            }
            finally
            {
                _lifecycle.Release();
            }
        }
    }

    private async Task AcceptLoopAsync(
        HttpConnectionListener listener,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IHttpConnection connection = await listener.AcceptOrListenAsync(cancellationToken)
                    .ConfigureAwait(false);
                Task task = ServeConnectionAsync(connection, cancellationToken);
                lock (_connectionGate)
                {
                    _connections.Add(task);
                }

                _ = task.ContinueWith(
                    (_, state) => ((GatewayControlPlaneServer)state!).RemoveConnection(task),
                    this,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            MarkListenerUnavailable();
            throw;
        }
    }

    private async Task ServeConnectionAsync(
        IHttpConnection connection,
        CancellationToken cancellationToken)
    {
        await using (connection.ConfigureAwait(false))
        {
            try
            {
                IHttpConnectionContext connectionContext = await connection.OpenAsync(cancellationToken)
                    .ConfigureAwait(false);
                await foreach (IHttpContext context in connectionContext.ReceiveAsync(cancellationToken)
                    .ConfigureAwait(false))
                {
                    try
                    {
                        try
                        {
                            await _router.RouteAsync(context, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch
                        {
                            context.Response.StatusCode = HttpStatusCode.InternalServerError;
                            context.Response.Body = new MemoryStream();
                        }

                        await connectionContext.SendAsync(context, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        await context.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch
            {
                // One connection cannot terminate the listener or withdraw other sessions.
            }
        }
    }

    private void RemoveConnection(Task task)
    {
        lock (_connectionGate)
        {
            _connections.Remove(task);
        }
    }

    private void MarkListenerUnavailable()
    {
        _address = null;
        lock (_documentGate)
        {
            _document = null;
        }

        try
        {
            DeleteMetadata();
        }
        catch
        {
            // The accept-loop failure remains the primary listener fault.
        }
    }

    private async Task HandleApplicationAsync(
        IHttpContext context,
        IReadOnlyDictionary<string, string> routeValues,
        CancellationToken cancellationToken)
    {
        if (await AuthorizeAsync(context, requireCommandAccess: false, cancellationToken).ConfigureAwait(false) is null)
        {
            return;
        }

        ApplicationExportDocument? document;
        lock (_documentGate)
        {
            document = _document;
        }

        if (document is null)
        {
            await WriteErrorAsync(
                    context,
                    HttpStatusCode.ServiceUnavailable,
                    "The application export is not published yet.",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        PrepareJsonResponse(context, HttpStatusCode.Ok);
        await document.SaveAsync(context.Response.Body, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleResourceAsync(
        IHttpContext context,
        IReadOnlyDictionary<string, string> routeValues,
        CancellationToken cancellationToken)
    {
        if (await AuthorizeAsync(context, requireCommandAccess: false, cancellationToken).ConfigureAwait(false) is null)
        {
            return;
        }

        if (!TryGetRouteValue(routeValues, "name", out string? name) ||
            !TryFindResource(name!, out IApplicationResourceDescriptor? descriptor, out ResourceManifest? manifest))
        {
            await WriteErrorAsync(
                    context,
                    HttpStatusCode.NotFound,
                    $"Application '{_application}' has no resource named '{name}'.",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        IApplicationResourceStateManager state = _state!;
        ResourceLifecycle lifecycle = state.GetState(descriptor!.Resource.Id);
        IReadOnlyList<ResourceEndpoint> observed = state.GetObservedEndpoints(descriptor.Resource.Id);
        var endpoints = new List<ControlPlaneEndpointDocument>(observed.Count);
        for (int index = 0; index < observed.Count; index++)
        {
            ResourceEndpoint endpoint = observed[index];
            if (Uri.TryCreateEndpoint(
                    endpoint.Scheme,
                    endpoint.Host,
                    endpoint.Port,
                    path: null,
                    out Uri? address))
            {
                endpoints.Add(new ControlPlaneEndpointDocument
                {
                    Name = endpoint.Name,
                    Address = address.ToEndpointString(),
                    IsPublic = endpoint.IsPublic,
                });
            }
        }

        var response = new ControlPlaneResourceDocument
        {
            Name = descriptor.Resource.Name.ToString(),
            Kind = manifest!.Kind,
            State = lifecycle.ToString(),
            Endpoints = endpoints,
        };
        PrepareJsonResponse(context, HttpStatusCode.Ok);
        await JsonSerializer.SerializeAsync(
                context.Response.Body,
                response,
                ControlPlaneJsonContext.Default.ControlPlaneResourceDocument,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HandleGetCommandsAsync(
        IHttpContext context,
        IReadOnlyDictionary<string, string> routeValues,
        CancellationToken cancellationToken)
    {
        ApplicationCaller? caller = await AuthorizeAsync(context, requireCommandAccess: true, cancellationToken)
            .ConfigureAwait(false);
        if (caller is null)
        {
            return;
        }

        if (!TryGetResourceRoute(routeValues, out IApplicationResourceDescriptor? descriptor, out _))
        {
            await WriteErrorAsync(
                    context,
                    HttpStatusCode.NotFound,
                    "The requested resource does not exist.",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        ControlPlaneCommandObservation[] commands = SnapshotCommands(
            descriptor!.Resource.Name,
            GetCommandOwner(caller));
        PrepareJsonResponse(context, HttpStatusCode.Ok);
        await JsonSerializer.SerializeAsync(
                context.Response.Body,
                commands,
                ControlPlaneJsonContext.Default.ControlPlaneCommandObservationArray,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HandlePutCommandAsync(
        IHttpContext context,
        IReadOnlyDictionary<string, string> routeValues,
        CancellationToken cancellationToken)
    {
        ApplicationCaller? caller = await AuthorizeAsync(context, requireCommandAccess: true, cancellationToken)
            .ConfigureAwait(false);
        if (caller is null)
        {
            return;
        }

        string owner = GetCommandOwner(caller);

        if (!TryGetResourceRoute(
                routeValues,
                out IApplicationResourceDescriptor? descriptor,
                out ResourceManifest? manifest) ||
            !TryGetRouteValue(routeValues, "id", out string? id))
        {
            await WriteErrorAsync(
                    context,
                    HttpStatusCode.NotFound,
                    "The requested resource or command does not exist.",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        ControlPlaneCommandRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(
                    context.Request.Body,
                    ControlPlaneJsonContext.Default.ControlPlaneCommandRequest,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            await WriteErrorAsync(
                    context,
                    HttpStatusCode.BadRequest,
                    exception.Message,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (request is null ||
            (request.Id is not null && !string.Equals(request.Id, id, StringComparison.Ordinal)) ||
            string.IsNullOrWhiteSpace(request.Kind) ||
            string.IsNullOrWhiteSpace(request.Owner) ||
            string.IsNullOrWhiteSpace(request.Key))
        {
            await WriteErrorAsync(
                    context,
                    HttpStatusCode.BadRequest,
                    "A command requires matching id, non-empty kind, owner, and key values.",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!string.Equals(request.Owner, owner, StringComparison.Ordinal))
        {
            await WriteErrorAsync(
                    context,
                    HttpStatusCode.Forbidden,
                    $"Command owner '{request.Owner}' does not match authenticated issuer '{owner}'.",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var command = new ResourceCommand(
            id!,
            request.Kind,
            request.Owner,
            request.Key,
            request.Payload ?? Array.Empty<byte>());
        if (!IsCommandKindAllowed(caller, command.Kind))
        {
            ControlPlaneCommandObservation rejected = CreateObservation(command, "Rejected",
                $"Trusted issuer '{owner}' may not send command kind '{command.Kind}'.", result: null);
            PrepareJsonResponse(context, HttpStatusCode.Conflict);
            await JsonSerializer.SerializeAsync(context.Response.Body, rejected,
                ControlPlaneJsonContext.Default.ControlPlaneCommandObservation, cancellationToken).ConfigureAwait(false);
            return;
        }
        ControlPlaneCommandObservation observation = await ApplyCommandIdempotentlyAsync(
                descriptor!,
                manifest!,
                command,
                cancellationToken)
            .ConfigureAwait(false);
        PrepareJsonResponse(
            context,
            string.Equals(observation.Status, "Applied", StringComparison.Ordinal)
                ? HttpStatusCode.Ok
                : HttpStatusCode.Conflict);
        await JsonSerializer.SerializeAsync(
                context.Response.Body,
                observation,
                ControlPlaneJsonContext.Default.ControlPlaneCommandObservation,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HandleDeleteCommandAsync(
        IHttpContext context,
        IReadOnlyDictionary<string, string> routeValues,
        CancellationToken cancellationToken)
    {
        ApplicationCaller? caller = await AuthorizeAsync(context, requireCommandAccess: true, cancellationToken)
            .ConfigureAwait(false);
        if (caller is null)
        {
            return;
        }

        string owner = GetCommandOwner(caller);

        if (!TryGetResourceRoute(
                routeValues,
                out IApplicationResourceDescriptor? descriptor,
                out ResourceManifest? manifest) ||
            !TryGetRouteValue(routeValues, "id", out string? id))
        {
            await WriteErrorAsync(
                    context,
                    HttpStatusCode.NotFound,
                    "The requested resource or command does not exist.",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        ControlPlaneCommandObservation? observed = null;
        await _commandDispatch.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!TryGetCommand(
                    descriptor!.Resource.Name,
                    id!,
                    out observed))
            {
                await WriteErrorAsync(
                        context,
                        HttpStatusCode.NotFound,
                        "The requested command does not exist.",
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (!string.Equals(observed!.Owner, owner, StringComparison.Ordinal))
            {
                await WriteErrorAsync(
                        context,
                        HttpStatusCode.Forbidden,
                        $"Command owner '{observed.Owner}' does not match authenticated issuer '{owner}'.",
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (!IsCommandKindAllowed(caller, observed.Kind))
            {
                ControlPlaneCommandObservation rejected = Reject(observed,
                    $"Trusted issuer '{owner}' may not send command kind '{observed.Kind}'.");
                PrepareJsonResponse(context, HttpStatusCode.Conflict);
                await JsonSerializer.SerializeAsync(context.Response.Body, rejected,
                    ControlPlaneJsonContext.Default.ControlPlaneCommandObservation, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!HasCommandOwnership(descriptor.Resource.Name, id!))
            {
                RemoveCommand(descriptor.Resource.Name, id!);
                context.Response.StatusCode = HttpStatusCode.NoContent;
                return;
            }

            IResourceCommandDispatcher? dispatcher = FindDispatcher(manifest!.Kind);
            Uri? address = null;
            string? reason = null;
            if (dispatcher is null ||
                !TryResolveResourceControlPlane(descriptor, manifest, out address, out reason))
            {
                ControlPlaneCommandObservation rejected = Reject(
                    observed,
                    dispatcher is null
                        ? $"No command dispatcher is registered for resource kind '{manifest.Kind}'."
                        : reason!);
                StoreCommand(descriptor.Resource.Name, rejected);
                PrepareJsonResponse(context, HttpStatusCode.Conflict);
                await JsonSerializer.SerializeAsync(
                        context.Response.Body,
                        rejected,
                        ControlPlaneJsonContext.Default.ControlPlaneCommandObservation,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var command = new ResourceCommand(
                observed.Id,
                observed.Kind,
                observed.Owner,
                observed.Key,
                observed.Payload);
            if (_commandCredentials is null)
            {
                throw new InvalidOperationException(
                    "The serving gateway does not provide resource bootstrap credentials.");
            }

            string bearerToken = await _commandCredentials
                .GetResourceCommandCredentialAsync(_application, descriptor.Resource.Name, cancellationToken)
                .ConfigureAwait(false);
            RemoteCertificateValidationCallback? validator = _transportTrust is not null &&
                string.Equals(address!.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? _transportTrust.CreateOutboundTrustValidator(_application) : null;
            await dispatcher.DeleteAsync(
                    address!,
                    bearerToken,
                    command,
                    validator,
                    cancellationToken)
                .ConfigureAwait(false);
            RemoveCommand(descriptor!.Resource.Name, id!);
            context.Response.StatusCode = HttpStatusCode.NoContent;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (observed is not null)
        {
            ControlPlaneCommandObservation rejected = Reject(
                observed,
                $"Dispatcher for resource kind '{manifest!.Kind}' rejected deletion: {exception.Message}");
            StoreCommand(descriptor!.Resource.Name, rejected);
            PrepareJsonResponse(context, HttpStatusCode.Conflict);
            await JsonSerializer.SerializeAsync(
                    context.Response.Body,
                    rejected,
                    ControlPlaneJsonContext.Default.ControlPlaneCommandObservation,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _commandDispatch.Release();
        }
    }

    private async Task<ControlPlaneCommandObservation> ApplyCommandIdempotentlyAsync(
        IApplicationResourceDescriptor descriptor,
        ResourceManifest manifest,
        ResourceCommand command,
        CancellationToken cancellationToken)
    {
        await _commandDispatch.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCommand(descriptor.Resource.Name, command.Id, out ControlPlaneCommandObservation? existing))
            {
                return CommandMatches(existing!, command)
                    ? existing!
                    : CreateObservation(
                        command,
                        "Rejected",
                        $"Command id '{command.Id}' is already assigned to a different command.",
                        result: null);
            }

            if (TryFindOwnerConflict(descriptor.Resource.Name, command, out string? conflictingOwner))
            {
                ControlPlaneCommandObservation rejected = CreateObservation(
                    command,
                    "Rejected",
                    $"Command key '{command.Key}' for kind '{command.Kind}' is owned by " +
                    $"application '{conflictingOwner}'.",
                    result: null);
                StoreCommand(descriptor.Resource.Name, rejected);
                return rejected;
            }

            ControlPlaneCommandObservation observation = await ApplyCommandAsync(
                    descriptor,
                    manifest,
                    command,
                    cancellationToken)
                .ConfigureAwait(false);
            StoreCommand(descriptor.Resource.Name, observation);
            return observation;
        }
        finally
        {
            _commandDispatch.Release();
        }
    }

    private async Task<ControlPlaneCommandObservation> ApplyCommandAsync(
        IApplicationResourceDescriptor descriptor,
        ResourceManifest manifest,
        ResourceCommand command,
        CancellationToken cancellationToken)
    {
        if (!AcceptsCommand(manifest, command.Kind))
        {
            return CreateObservation(
                command,
                "Rejected",
                $"Resource '{manifest.Name}' does not declare command kind '{command.Kind}'.",
                result: null);
        }

        IResourceCommandDispatcher? dispatcher = FindDispatcher(manifest.Kind);
        if (dispatcher is null)
        {
            return CreateObservation(
                command,
                "Rejected",
                $"No command dispatcher is registered for resource kind '{manifest.Kind}'.",
                result: null);
        }

        if (!TryResolveResourceControlPlane(descriptor, manifest, out Uri? address, out string? reason))
        {
            return CreateObservation(command, "Rejected", reason!, result: null);
        }

        try
        {
            if (_commandCredentials is null)
            {
                return CreateObservation(
                    command,
                    "Rejected",
                    "The serving gateway does not provide resource bootstrap credentials.",
                    result: null);
            }

            string bearerToken = await _commandCredentials
                .GetResourceCommandCredentialAsync(_application, descriptor.Resource.Name, cancellationToken)
                .ConfigureAwait(false);
            RemoteCertificateValidationCallback? validator = _transportTrust is not null &&
                string.Equals(address!.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? _transportTrust.CreateOutboundTrustValidator(_application) : null;
            ReadOnlyMemory<byte> result = await dispatcher.ApplyAsync(
                    address!,
                    bearerToken,
                    command,
                    validator,
                    cancellationToken)
                .ConfigureAwait(false);
            return CreateObservation(
                command,
                "Applied",
                $"Applied by the dispatcher for resource kind '{manifest.Kind}'.",
                result.ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return CreateObservation(
                command,
                "Rejected",
                $"Dispatcher for resource kind '{manifest.Kind}' rejected the command: {exception.Message}",
                result: null);
        }
    }

    // Authenticates the caller through the built-in ES256 trusted-issuer authenticator and then the
    // application's registered caller authenticators (ControlPlaneCallerAuthentication), and applies
    // the route's access rule to the mapped caller. Returns null after writing the refusal. With no
    // registered authenticator every response is the one this server gave before they existed:
    // 401 without a credential, 403 invalid_token for one it cannot verify, 403 insufficient_scope
    // for a verified developer on a command route.
    private async ValueTask<ApplicationCaller?> AuthorizeAsync(
        IHttpContext context,
        bool requireCommandAccess,
        CancellationToken cancellationToken)
    {
        string? authorization = context.Request.Headers.GetValue(HttpHeaderKey.Authorization);
        if (string.IsNullOrWhiteSpace(authorization))
        {
            context.Response.StatusCode = HttpStatusCode.Unauthorized;
            context.Response.Headers[HttpHeaderKey.WWWAuthenticate] = "Bearer";
            return null;
        }

        ApplicationCallerResult result = TryParseAuthorization(authorization, out string? scheme, out string? credential)
            ? await ControlPlaneCallerAuthentication
                .AuthenticateAsync(
                    _application,
                    scheme!,
                    credential!,
                    _trustedIssuers!.GetTrustedIssuers(_application),
                    _callers,
                    _timeProvider.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false)
            : default;

        switch (result.Status)
        {
            case ApplicationCallerStatus.Authenticated:
                break;
            case ApplicationCallerStatus.Unauthorized:
                await WriteCallerRefusalAsync(
                        context, HttpStatusCode.Unauthorized, $"{scheme} error=\"invalid_token\"", result.Failure, cancellationToken)
                    .ConfigureAwait(false);
                return null;
            case ApplicationCallerStatus.Forbidden:
                await WriteCallerRefusalAsync(
                        context, HttpStatusCode.Forbidden, $"{scheme} error=\"insufficient_scope\"", result.Failure, cancellationToken)
                    .ConfigureAwait(false);
                return null;
            default:
                context.Response.StatusCode = HttpStatusCode.Forbidden;
                context.Response.Headers[HttpHeaderKey.WWWAuthenticate] = "Bearer error=\"invalid_token\"";
                return null;
        }

        ApplicationCaller caller = result.Caller!;
        if (requireCommandAccess && !ControlPlaneCallerAuthentication.CanDispatchCommands(caller))
        {
            context.Response.StatusCode = HttpStatusCode.Forbidden;
            context.Response.Headers[HttpHeaderKey.WWWAuthenticate] =
                "Bearer error=\"insufficient_scope\"";
            return null;
        }

        return caller;
    }

    // '<scheme> <credential>': the scheme is the first token and the credential the trimmed rest.
    private static bool TryParseAuthorization(string authorization, out string? scheme, out string? credential)
    {
        int separator = authorization.IndexOf(' ');
        if (separator > 0)
        {
            string presented = authorization[(separator + 1)..].Trim();
            if (presented.Length > 0)
            {
                scheme = authorization[..separator];
                credential = presented;
                return true;
            }
        }

        scheme = null;
        credential = null;
        return false;
    }

    private static async Task WriteCallerRefusalAsync(
        IHttpContext context,
        HttpStatusCode statusCode,
        string challenge,
        string? failure,
        CancellationToken cancellationToken)
    {
        // The challenge is set before any body is written, so it never depends on the response
        // being buffered until the handler returns.
        context.Response.Headers[HttpHeaderKey.WWWAuthenticate] = challenge;
        if (failure is null)
        {
            context.Response.StatusCode = statusCode;
        }
        else
        {
            await WriteErrorAsync(context, statusCode, failure, cancellationToken).ConfigureAwait(false);
        }
    }

    // Commands are owned by the caller's application; CanDispatchCommands guarantees one.
    private static string GetCommandOwner(ApplicationCaller caller) => caller.Application!.Value.ToString();

    // An empty list permits every kind, the TrustedIssuer.AllowedCommandKinds rule.
    private static bool IsCommandKindAllowed(ApplicationCaller caller, string kind) =>
        caller.AllowedCommandKinds.Count == 0 ||
        caller.AllowedCommandKinds.Contains(kind, StringComparer.Ordinal);

    private static IReadOnlyList<IApplicationCallerAuthenticator> SnapshotCallers(IApplicationModel model)
    {
        IList<IApplicationCallerAuthenticator> registered = (model.Providers ?? ApplicationProviders.Empty).Callers;
        if (registered.Count == 0)
        {
            return Array.Empty<IApplicationCallerAuthenticator>();
        }

        var callers = new IApplicationCallerAuthenticator[registered.Count];
        registered.CopyTo(callers, 0);
        return callers;
    }

    private bool TryGetResourceRoute(
        IReadOnlyDictionary<string, string> routeValues,
        out IApplicationResourceDescriptor? descriptor,
        out ResourceManifest? manifest)
    {
        if (!TryGetRouteValue(routeValues, "name", out string? name))
        {
            descriptor = null;
            manifest = null;
            return false;
        }

        return TryFindResource(name!, out descriptor, out manifest);
    }

    private bool TryFindResource(
        string name,
        out IApplicationResourceDescriptor? descriptor,
        out ResourceManifest? manifest)
    {
        if (!IsPublishedResource(name))
        {
            descriptor = null;
            manifest = null;
            return false;
        }

        IApplicationModel model = _model!;
        for (int index = 0; index < model.Descriptors.Count; index++)
        {
            IApplicationResourceDescriptor candidate = model.Descriptors[index];
            if (string.Equals(candidate.Resource.Name.ToString(), name, StringComparison.Ordinal))
            {
                descriptor = candidate;
                manifest = model.Manifests[index];
                return true;
            }
        }

        descriptor = null;
        manifest = null;
        return false;
    }

    private bool IsPublishedResource(string name)
    {
        lock (_documentGate)
        {
            if (_document is null)
            {
                return false;
            }

            for (int index = 0; index < _document.Resources.Count; index++)
            {
                if (string.Equals(_document.Resources[index].Name, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryGetRouteValue(
        IReadOnlyDictionary<string, string> routeValues,
        string key,
        out string? value)
    {
        if (routeValues.TryGetValue(key, out string? text) &&
            !string.IsNullOrWhiteSpace(text))
        {
            value = text;
            return true;
        }

        value = null;
        return false;
    }

    private bool TryResolveResourceControlPlane(
        IApplicationResourceDescriptor descriptor,
        ResourceManifest manifest,
        out Uri? address,
        out string? reason)
    {
        IApplicationResourceStateManager state = _state!;
        ResourceLifecycle lifecycle = state.GetState(descriptor.Resource.Id);
        if (lifecycle != ResourceLifecycle.Running)
        {
            address = null;
            reason = $"Resource '{manifest.Name}' is '{lifecycle}', not Running.";
            return false;
        }

        IReadOnlyList<ResourceEndpoint> endpoints = state.GetObservedEndpoints(descriptor.Resource.Id);
        for (int index = 0; index < endpoints.Count; index++)
        {
            ResourceEndpoint endpoint = endpoints[index];
            if (string.Equals(endpoint.Name, manifest.ControlPlane.Endpoint, StringComparison.Ordinal) &&
                Uri.TryCreateEndpoint(
                    endpoint.Scheme,
                    endpoint.Host,
                    endpoint.Port,
                    manifest.ControlPlane.Path,
                    out address))
            {
                // The served path mints the same ResourceAccess credential the gateway's own delivery
                // does, so it applies the same transport rule before that credential is issued:
                // HTTPS, or loopback HTTP in Local.
                IApplicationModel model = _model!;
                if (string.Equals(address.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                    (model.Environment.IsLocal &&
                     string.Equals(address.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                     address.IsLoopback))
                {
                    reason = null;
                    return true;
                }

                reason = $"Application '{model.Name}' refuses to send a bearer credential to " +
                    $"non-TLS endpoint '{address}'. Use HTTPS, or loopback HTTP in Local.";
                address = null;
                return false;
            }
        }

        address = null;
        reason = $"Resource '{manifest.Name}' has no observed default control-plane endpoint " +
            $"'{manifest.ControlPlane.Endpoint}'.";
        return false;
    }

    // Exact manifest kind first, then the first catch-all dispatcher, mirroring the gateway's own
    // command-client selection (ApplicationGateway.Commands.cs) so an area-specific registration
    // always overrides the default generic control-plane client.
    private IResourceCommandDispatcher? FindDispatcher(string resourceKind)
    {
        IResourceCommandDispatcher? fallback = null;
        for (int index = 0; index < _dispatchers.Count; index++)
        {
            IResourceCommandDispatcher candidate = _dispatchers[index];
            if (string.Equals(candidate.ResourceKind, resourceKind, StringComparison.Ordinal))
            {
                return candidate;
            }

            if (fallback is null &&
                string.Equals(candidate.ResourceKind, IGatewayResourceCommandClient.AnyKind, StringComparison.Ordinal))
            {
                fallback = candidate;
            }
        }

        return fallback;
    }

    private static bool AcceptsCommand(ResourceManifest manifest, string kind)
    {
        for (int index = 0; index < manifest.Commands.Count; index++)
        {
            if (string.Equals(manifest.Commands[index].Kind, kind, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CommandMatches(
        ControlPlaneCommandObservation observed,
        ResourceCommand command) =>
        string.Equals(observed.Kind, command.Kind, StringComparison.Ordinal) &&
        string.Equals(observed.Owner, command.Owner, StringComparison.Ordinal) &&
        string.Equals(observed.Key, command.Key, StringComparison.Ordinal) &&
        observed.Payload.AsSpan().SequenceEqual(command.Payload.Span);

    private bool TryFindOwnerConflict(
        ResourceName resource,
        ResourceCommand command,
        out string? conflictingOwner)
    {
        lock (_commandGate)
        {
            foreach (var entry in _commandOwnership)
            {
                if (entry.Key.Resource == resource &&
                    string.Equals(entry.Value.Kind, command.Kind, StringComparison.Ordinal) &&
                    string.Equals(entry.Value.Key, command.Key, StringComparison.Ordinal) &&
                    !string.Equals(entry.Value.Owner, command.Owner, StringComparison.Ordinal))
                {
                    conflictingOwner = entry.Value.Owner;
                    return true;
                }
            }
        }

        conflictingOwner = null;
        return false;
    }

    private void StoreCommand(ResourceName resource, ControlPlaneCommandObservation command)
    {
        lock (_commandGate)
        {
            if (!_commands.TryGetValue(resource, out Dictionary<string, ControlPlaneCommandObservation>? commands))
            {
                commands = new Dictionary<string, ControlPlaneCommandObservation>(StringComparer.Ordinal);
                _commands.Add(resource, commands);
            }

            commands[command.Id] = command;
            if (string.Equals(command.Status, "Applied", StringComparison.Ordinal))
            {
                _commandOwnership[(resource, command.Id)] = (command.Kind, command.Key, command.Owner);
            }
        }
    }

    private bool TryGetCommand(
        ResourceName resource,
        string id,
        out ControlPlaneCommandObservation? command)
    {
        lock (_commandGate)
        {
            if (_commands.TryGetValue(resource, out Dictionary<string, ControlPlaneCommandObservation>? commands) &&
                commands.TryGetValue(id, out ControlPlaneCommandObservation? existing))
            {
                command = existing;
                return true;
            }
        }

        command = null;
        return false;
    }

    private bool HasCommandOwnership(ResourceName resource, string id)
    {
        lock (_commandGate)
        {
            return _commandOwnership.ContainsKey((resource, id));
        }
    }

    private ControlPlaneCommandObservation[] SnapshotCommands(
        ResourceName resource,
        string owner)
    {
        lock (_commandGate)
        {
            if (!_commands.TryGetValue(resource, out Dictionary<string, ControlPlaneCommandObservation>? commands))
            {
                return Array.Empty<ControlPlaneCommandObservation>();
            }

            var result = new List<ControlPlaneCommandObservation>();
            foreach (ControlPlaneCommandObservation command in commands.Values)
            {
                if (string.Equals(command.Owner, owner, StringComparison.Ordinal))
                {
                    result.Add(command);
                }
            }

            result.Sort(static (left, right) => string.CompareOrdinal(left.Id, right.Id));
            return result.ToArray();
        }
    }

    private void RemoveCommand(ResourceName resource, string id)
    {
        lock (_commandGate)
        {
            if (!_commands.TryGetValue(resource, out Dictionary<string, ControlPlaneCommandObservation>? commands))
            {
                return;
            }

            commands.Remove(id);
            _commandOwnership.Remove((resource, id));
            if (commands.Count == 0)
            {
                _commands.Remove(resource);
            }
        }
    }

    private static ControlPlaneCommandObservation CreateObservation(
        ResourceCommand command,
        string status,
        string detail,
        byte[]? result) =>
        new()
        {
            Id = command.Id,
            Kind = command.Kind,
            Owner = command.Owner,
            Key = command.Key,
            Payload = command.Payload.ToArray(),
            Status = status,
            Detail = detail,
            Result = result,
        };

    private static ControlPlaneCommandObservation Reject(
        ControlPlaneCommandObservation command,
        string detail) =>
        new()
        {
            Id = command.Id,
            Kind = command.Kind,
            Owner = command.Owner,
            Key = command.Key,
            Payload = command.Payload,
            Status = "Rejected",
            Detail = detail,
            Result = command.Result,
        };

    private static void PrepareJsonResponse(IHttpContext context, HttpStatusCode statusCode)
    {
        context.Response.StatusCode = statusCode;
        context.Response.Headers[HttpHeaderKey.ContentType] = "application/json; charset=utf-8";
        context.Response.Headers[HttpHeaderKey.CacheControl] = "no-store";
    }

    private static async Task WriteErrorAsync(
        IHttpContext context,
        HttpStatusCode statusCode,
        string error,
        CancellationToken cancellationToken)
    {
        PrepareJsonResponse(context, statusCode);
        await JsonSerializer.SerializeAsync(
                context.Response.Body,
                new ControlPlaneErrorDocument { Error = error },
                ControlPlaneJsonContext.Default.ControlPlaneErrorDocument,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteMetadataAsync(
        ApplicationExportDocument document,
        CancellationToken cancellationToken)
    {
        if (_metadataPath is null)
        {
            return;
        }

        JsonElement trustKey = document.TrustKey ?? throw new InvalidDataException(
            $"Application export '{document.Application}' has no trustKey.");
        string directory = Path.GetDirectoryName(_metadataPath)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = _metadataPath + "." +
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        new ControlPlaneMetadataDocument
                        {
                            Url = Address.ToEndpointString(),
                            TrustKey = trustKey.Clone(),
                        },
                        ControlPlaneJsonContext.Default.ControlPlaneMetadataDocument,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _metadataPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void DeleteMetadata()
    {
        if (_metadataPath is not null && File.Exists(_metadataPath))
        {
            File.Delete(_metadataPath);
        }
    }

    private static string GetMetadataPath(string metadataDirectory, ApplicationName application)
    {
        string root = Path.GetFullPath(metadataDirectory);
        string applicationDirectory = Path.GetFullPath(Path.Combine(root, application.ToString()));
        string prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!applicationDirectory.StartsWith(prefix, comparison))
        {
            throw new InvalidDataException(
                $"Application name '{application}' cannot be used as a metadata directory.");
        }

        return Path.Combine(applicationDirectory, "control-plane.json");
    }

    private static IPEndPoint GetListenEndpoint(Uri address)
    {
        if (!address.IsAbsoluteUri ||
            !string.Equals(address.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The hosting-free control plane currently supports absolute HTTP listen addresses only.",
                nameof(address));
        }

        IPAddress host;
        if (string.Equals(address.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            host = IPAddress.Loopback;
        }
        else if (!IPAddress.TryParse(address.Host, out host!))
        {
            throw new ArgumentException(
                $"Control-plane listen host '{address.Host}' must be an IP address or localhost.",
                nameof(address));
        }

        return new IPEndPoint(host, address.Port);
    }
}
