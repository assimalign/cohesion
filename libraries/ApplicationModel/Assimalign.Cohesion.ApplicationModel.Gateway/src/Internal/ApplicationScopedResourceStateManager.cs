using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Presents application-local resource identifiers over a shared gateway state manager.
/// </summary>
internal sealed class ApplicationScopedResourceStateManager :
    IApplicationResourceStateManager,
    IDisposable
{
    private readonly object _gate = new();
    private readonly IApplicationResourceStateManager _inner;
    private readonly Guid _applicationNamespace;
    private readonly Dictionary<ResourceId, ResourceId> _scopedByResource = new();
    private readonly Dictionary<ResourceId, ResourceId> _resourceByScoped = new();
    private bool _disposed;

    public ApplicationScopedResourceStateManager(
        IApplicationResourceStateManager inner,
        ApplicationName application,
        IReadOnlyList<IApplicationResource> resources)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentNullException.ThrowIfNull(resources);
        _applicationNamespace = Guid.AsDeterministicGuid(application.ToString());

        foreach (IApplicationResource resource in resources)
        {
            GetScopedId(resource.Id);
        }

        _inner.StateChanged += OnStateChanged;
    }

    public event EventHandler<ResourceStateChangedEventArgs>? StateChanged;

    public ResourceLifecycle GetState(ResourceId id) => _inner.GetState(GetScopedId(id));

    public void SetState(
        ResourceId id,
        ResourceLifecycle state,
        string? detail = null,
        IReadOnlyList<ResourceEndpoint>? observedEndpoints = null) =>
        _inner.SetState(GetScopedId(id), state, detail, observedEndpoints);

    public IReadOnlyList<ResourceEndpoint> GetObservedEndpoints(ResourceId id) =>
        _inner.GetObservedEndpoints(GetScopedId(id));

    public Task<ResourceLifecycle> WaitForStateAsync(
        ResourceId id,
        IReadOnlySet<ResourceLifecycle> terminals,
        TimeSpan budget,
        CancellationToken cancellationToken = default) =>
        _inner.WaitForStateAsync(GetScopedId(id), terminals, budget, cancellationToken);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _inner.StateChanged -= OnStateChanged;
    }

    private ResourceId GetScopedId(ResourceId resource)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_scopedByResource.TryGetValue(resource, out ResourceId scoped))
            {
                return scoped;
            }

            scoped = Guid.AsDeterministicGuid(resource.ToString(), _applicationNamespace);
            _scopedByResource.Add(resource, scoped);
            _resourceByScoped.Add(scoped, resource);
            return scoped;
        }
    }

    private void OnStateChanged(object? sender, ResourceStateChangedEventArgs args)
    {
        ResourceId resource;
        EventHandler<ResourceStateChangedEventArgs>? handlers;
        lock (_gate)
        {
            if (_disposed || !_resourceByScoped.TryGetValue(args.Resource, out resource))
            {
                return;
            }

            handlers = StateChanged;
        }

        handlers?.Invoke(
            this,
            new ResourceStateChangedEventArgs(
                resource,
                args.Previous,
                args.Current,
                args.Detail));
    }
}
