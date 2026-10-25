using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;
using Assimalign.Cohesion.Hosting.Resources.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

public abstract partial class ApplicationGateway
{
    private readonly object _managementGate = new();
    private ResourceManagementCoordinator? _managementCoordinator;
    private CancellationTokenSource? _managementStop;
    private Task? _managementLoop;

    private ResourceManagementCoordinator Management
    {
        get
        {
            lock (_managementGate)
            {
                return _managementCoordinator ??= new ResourceManagementCoordinator(
                    _options.ManagementStore ?? FileResourceManagementStore.Create(Path.Combine(_options.ExportDirectory ?? Path.Combine(Environment.CurrentDirectory, ".cohesion"), "management")),
                    _options.StateProtocolClient ?? new ResourceStateProtocolClient(CreateManagementClientAsync),
                    _options.TimeProvider, GetDeclaredRuntimeBinding, GetInstancesAsync);
            }
        }
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The resource has no supported runtime manifest.</exception>
    public JsonElement GetRuntimeManifest(ApplicationName application, ResourceName resource)
        => Management.GetRuntimeManifest(application, resource);

    private JsonElement GetDeclaredRuntimeBinding(ApplicationName application, ResourceName resource)
    {
        ModelResource item = FindManagementResource(application, resource);
        return item.Model.Manifests[IndexOfDescriptor(item.Model.Descriptors, item.Descriptor)].Runtime?.Clone()
            ?? throw new NotSupportedException(ErrorMessages.ManagementStateUnsupported);
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The resource is not locally realized by this gateway.</exception>
    public ValueTask<ResourceInstanceInventory> GetInstancesAsync(ApplicationName application, ResourceName resource, CancellationToken cancellationToken = default)
    {
        ModelResource item = FindManagementResource(application, resource);
        return ObserveResourceInstancesAsync(item.Model, item.Descriptor, cancellationToken);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">The request or desired value is invalid.</exception>
    /// <exception cref="InvalidOperationException">The condition conflicts or trusted inventory is unavailable.</exception>
    /// <exception cref="NotSupportedException">The runtime contract does not permit management.</exception>
    public ValueTask<ResourceStateOperation> SetStateAsync(ResourceStateChange change, string verifiedSubject, CancellationToken cancellationToken = default) =>
        Management.CommitAsync(change, verifiedSubject, false, cancellationToken);

    /// <inheritdoc />
    public ValueTask<ResourceStateOperation?> GetStateAsync(ResourceStateAddress address, CancellationToken cancellationToken = default) =>
        Management.GetAsync(address, cancellationToken);

    /// <inheritdoc />
    public ValueTask<ResourceStateOperation?> GetStateOperationAsync(ApplicationName application, ResourceName resource, Guid operationId, CancellationToken cancellationToken = default) =>
        Management.GetOperationAsync(application, resource, operationId, cancellationToken);

    /// <summary>Observes individually addressable management processes. Multi-instance platforms must override this hook.</summary>
    /// <param name="model">The resource's application.</param>
    /// <param name="descriptor">The logical resource descriptor.</param>
    /// <param name="cancellationToken">Signals cancellation.</param>
    /// <returns>The current platform inventory, including starting processes.</returns>
    /// <exception cref="NotSupportedException">The platform has multiple replicas without an instance inventory.</exception>
    protected virtual ValueTask<ResourceInstanceInventory> ObserveResourceInstancesAsync(IApplicationModel model,
        IApplicationResourceDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int desired = descriptor.Plan!.Workload.Replicas;
        if (desired > 1) { throw new NotSupportedException(ErrorMessages.ManagementResourceUnavailable); }
        var targets = new List<ResourceManagementTarget>();
        if (TryGetResourceManagementBinding(model, descriptor, out PortBinding? binding, out string? path))
        {
            foreach (ResourceEndpoint endpoint in GetApplicationState(model).GetObservedEndpoints(descriptor.Resource.Id))
            {
                if (endpoint.Name == binding!.Endpoint && Uri.TryCreateEndpoint(endpoint.Scheme, endpoint.Host, endpoint.Port, path, out Uri? address))
                {
                    targets.Add(new ResourceManagementTarget(descriptor.Resource.Name.Value, address.AbsoluteUri, address));
                }
            }
        }
        byte[] identity = SHA256.HashData(Encoding.UTF8.GetBytes(model.Name.Value + "\0" + descriptor.Resource.Name.Value));
        return ValueTask.FromResult(new ResourceInstanceInventory(new Guid(identity.AsSpan(0, 16)),
            string.Join("|", targets.Select(static target => target.Generation)), desired, targets.Count == desired, targets));
    }

    /// <summary>Resolves generic runtime metadata to a declared plan port without resource-area knowledge.</summary>
    /// <param name="model">The owning application.</param>
    /// <param name="descriptor">The resource descriptor.</param>
    /// <param name="binding">The declared management port when present.</param>
    /// <param name="path">The versioned management path when present.</param>
    /// <returns>Whether this resource exposes runtime management.</returns>
    protected static bool TryGetResourceManagementBinding(IApplicationModel model, IApplicationResourceDescriptor descriptor,
        out PortBinding? binding, out string? path)
    {
        ResourceManifest manifest = model.Manifests[IndexOfDescriptor(model.Descriptors, descriptor)];
        binding = null;
        path = null;
        if (manifest.Runtime is not JsonElement runtime) { return false; }
        JsonElement management = runtime.GetProperty("management");
        string? endpoint = management.GetProperty("endpoint").GetString();
        path = management.GetProperty("path").GetString();
        binding = descriptor.Plan!.Container.Ports.FirstOrDefault(port => port.Endpoint == endpoint);
        return binding is not null && path is not null;
    }

    private ModelResource FindManagementResource(ApplicationName application, ResourceName resource)
    {
        foreach (ModelResource item in _order)
        {
            if (item.Model.Name == application && item.Descriptor.Resource.Name == resource && !IsExternalPlan(item.Descriptor.Plan!)) { return item; }
        }
        throw new NotSupportedException(ErrorMessages.ManagementStateUnsupported);
    }

    private static void ValidateManagementManifest(ResourceManifest manifest)
    {
        if (manifest.Runtime is not JsonElement runtime) { return; }
        if (!runtime.TryGetProperty("schema", out JsonElement schema) || schema.GetString() != "cohesion/resource-runtime/v1" ||
            !runtime.TryGetProperty("management", out JsonElement management) ||
            !management.TryGetProperty("protocol", out JsonElement protocol) || protocol.GetString() != "cohesion/resource-state/v1" ||
            !management.TryGetProperty("endpoint", out JsonElement endpoint) || endpoint.ValueKind != JsonValueKind.String ||
            !manifest.Endpoints.Any(candidate => candidate.Name == endpoint.GetString()) ||
            !management.TryGetProperty("path", out JsonElement path) || path.ValueKind != JsonValueKind.String ||
            !ResourceStateWire.IsValidManagementPath(path.GetString()))
        {
            throw new NotSupportedException(ErrorMessages.ManagementStateUnsupported);
        }
        if (runtime.TryGetProperty("states", out _) || runtime.TryGetProperty("initialState", out _))
        {
            throw new NotSupportedException(ErrorMessages.ManagementStateUnsupported);
        }
    }

    private async ValueTask<HttpClient> CreateManagementClientAsync(ResourceStateAddress address, ResourceManagementTarget target, CancellationToken cancellationToken)
    {
        ModelResource item = FindManagementResource(address.Application, address.Resource);
        if (!CanSendCredential(item.Model, target.Address, out _)) { throw new InvalidOperationException(ErrorMessages.ManagementResourceUnavailable); }
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false };
        if (target.Address.Scheme == Uri.UriSchemeHttps)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = CreateOutboundTrustValidator(address.Application);
        }
        if (target.ServerName is not null)
        {
            if (!IPAddress.TryParse(target.Address.DnsSafeHost, out IPAddress? ip)) { handler.Dispose(); throw new InvalidOperationException(ErrorMessages.ManagementResourceUnavailable); }
            handler.ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(ip, target.Address.Port), token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            };
        }
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            string token = await ((IResourceCommandCredentialProvider)this).GetResourceCommandCredentialAsync(address.Application, address.Resource, cancellationToken).ConfigureAwait(false);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private void StartResourceManagement()
    {
        if (_managementLoop is not null || !_order.Any(item => item.Model.Manifests[IndexOfDescriptor(item.Model.Descriptors, item.Descriptor)].Runtime.HasValue)) { return; }
        _managementStop = new CancellationTokenSource();
        CancellationToken token = _managementStop.Token;
        _managementLoop = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                foreach (ModelResource item in _order)
                {
                    if (item.Model.Manifests[IndexOfDescriptor(item.Model.Descriptors, item.Descriptor)].Runtime is null || IsExternalPlan(item.Descriptor.Plan!)) { continue; }
                    try { await Management.ReconcileAsync(item.Model.Name, item.Descriptor.Resource.Name, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                    catch (Exception)
                    {
                        // A resource/storage outage must not stop reconciliation of independent resources.
                        // Durable operation observations retain their timestamp; a later pass retries intent.
                    }
                }
                try { await Task.Delay(_options.ManagementReconcileInterval, _options.TimeProvider, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            }
        }, CancellationToken.None);
    }

    private async Task StopResourceManagementAsync()
    {
        _managementStop?.Cancel();
        if (_managementLoop is not null) { await _managementLoop.ConfigureAwait(false); }
        _managementLoop = null;
        _managementStop?.Dispose();
        _managementStop = null;
        lock (_managementGate) { _managementCoordinator = null; }
    }
}
