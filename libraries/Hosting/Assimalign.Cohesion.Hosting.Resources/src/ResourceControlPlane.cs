using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// The control plane of one resource instance.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StartAsync"/> publishes the instance's <see cref="Manifest"/> and restores every
/// snapshot the publisher returns before it completes. Register the control plane ahead of the
/// services that serve traffic: a host starts its services in registration order, so the instance
/// serves only once the state written since the resource's last deployment is in effect. A failed
/// publication fails the host's start; the gateway tears down an instance whose manifest never
/// arrives, and the platform's restart policy decides what runs next.
/// </para>
/// <para>
/// Reads run concurrently. Writes and restores run one at a time for the whole resource, so the
/// snapshot a write returns is the state that write left. The control plane reads the resource's
/// actions once, when it is created.
/// </para>
/// <para>
/// Content refused with <see cref="ResourceContentException"/>, by the content serializer or by an
/// action, is reported as <see cref="ResourceActionStatus.Rejected"/> and changes nothing.
/// </para>
/// </remarks>
[DebuggerDisplay("{Resource.Provider}/{Resource.Name} {Resource.Key}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class ResourceControlPlane : IResourceControlPlane
{
    private const int created = 0;
    private const int starting = 1;
    private const int started = 2;
    private const int stopped = 3;

    private readonly IResourceManifestPublisher _publisher;
    private readonly FrozenDictionary<ResourceActionName, IResourceAction> _actions;
    private readonly SemaphoreSlim _mutations = new(1, 1);
    private int _lifecycle;

    private ResourceControlPlane(
        IResource resource,
        IResourceManifestPublisher publisher,
        IResourceContentSerializer serializer,
        FrozenDictionary<ResourceActionName, IResourceAction> actions,
        ResourceManifest manifest)
    {
        Resource = resource;
        Manifest = manifest;
        Serializer = serializer;
        _publisher = publisher;
        _actions = actions;
    }

    /// <inheritdoc/>
    public ServiceId Id { get; } = ServiceId.New();

    /// <inheritdoc/>
    public IResource Resource { get; }

    /// <inheritdoc/>
    public ResourceManifest Manifest { get; }

    /// <inheritdoc/>
    public IResourceContentSerializer Serializer { get; }

    /// <summary>
    /// Creates the control plane of a resource instance.
    /// </summary>
    /// <param name="resource">The resource instance.</param>
    /// <param name="publisher">Publishes the instance's manifest when its host starts.</param>
    /// <param name="serializer">
    /// Writes and reads the instance's content. It must know the type of every action's state, which
    /// creation checks, and every action's input and output type.
    /// </param>
    /// <returns>The control plane.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The resource's provider, name, or key is <see langword="default"/>; it returns a
    /// <see langword="null"/> collection; one of its actions is <see langword="null"/>, has no
    /// name, shares a name with another, declares an operation other than Read or Write, or has
    /// state without accepting writes; or <paramref name="serializer"/> cannot write an action's state.
    /// </exception>
    public static ResourceControlPlane Create(IResource resource, IResourceManifestPublisher publisher, IResourceContentSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(serializer);

        if (resource.Provider.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ProviderNameMissing, nameof(resource));
        }

        if (resource.Name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ResourceNameMissing, nameof(resource));
        }

        if (resource.Key == default)
        {
            throw new ArgumentException(ErrorMessages.ResourceInstanceKeyMissing, nameof(resource));
        }

        if (resource.HealthContributors is null)
        {
            throw new ArgumentException(ErrorMessages.ResourceHealthContributorsMissing, nameof(resource));
        }

        IReadOnlyCollection<IResourceAction> declared = resource.Actions
            ?? throw new ArgumentException(ErrorMessages.ResourceActionsMissing, nameof(resource));

        var actions = new Dictionary<ResourceActionName, IResourceAction>(declared.Count);
        var descriptors = new List<ResourceActionDescriptor>(declared.Count);
        foreach (IResourceAction action in declared)
        {
            if (action is null)
            {
                throw new ArgumentException(ErrorMessages.ResourceActionNull, nameof(resource));
            }

            ResourceActionName name = action.Name;
            if (name.Value is null)
            {
                throw new ArgumentException(ErrorMessages.ResourceActionNameMissing, nameof(resource));
            }

            if (action.Kind is not (ResourceActionKind.Read or ResourceActionKind.Write or ResourceActionKind.ReadWrite))
            {
                throw new ArgumentException(Format(ErrorMessages.ResourceActionKindInvalid, name), nameof(resource));
            }

            IResourceActionState? state = action.State;
            if (state is not null && !action.Kind.HasFlag(ResourceActionKind.Write))
            {
                throw new ArgumentException(Format(ErrorMessages.ResourceActionStateReadOnly, name), nameof(resource));
            }

            if (!actions.TryAdd(name, action))
            {
                throw new ArgumentException(Format(ErrorMessages.ResourceActionNameDuplicate, name), nameof(resource));
            }

            // A serializer that cannot write a state would fail the first write after it changed the
            // instance, leaving state the gateway never sees: refuse it before anything runs.
            if (state is not null)
            {
                try
                {
                    _ = state.GetSnapshot(serializer);
                }
                catch (NotSupportedException exception)
                {
                    throw new ArgumentException(Format(ErrorMessages.ResourceActionStateUnsupported, name), nameof(serializer), exception);
                }
            }

            descriptors.Add(new ResourceActionDescriptor(name, action.Kind, state is not null));
        }

        descriptors.Sort(static (left, right) => left.Name.CompareTo(right.Name));

        var manifest = new ResourceManifest(resource.Provider, resource.Name, resource.Key, descriptors.AsReadOnly());
        return new ResourceControlPlane(resource, publisher, serializer, actions.ToFrozenDictionary(), manifest);
    }

    /// <summary>
    /// Publishes the instance's manifest, then restores every snapshot the publisher returns.
    /// </summary>
    /// <param name="cancellationToken">Cancels publication and restores.</param>
    /// <returns>A task that completes when the returned state is in effect.</returns>
    /// <exception cref="InvalidOperationException">
    /// The control plane was already started; the publisher returned <see langword="null"/>, a
    /// <see langword="null"/> snapshot, or two snapshots for one action; or a snapshot was not
    /// restored. A failed start leaves the control plane unstarted.
    /// </exception>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _lifecycle, starting, created) != created)
        {
            throw new InvalidOperationException(ErrorMessages.ResourceControlPlaneAlreadyStarted);
        }

        bool published = false;
        try
        {
            IReadOnlyList<ResourceStateSnapshot> snapshots = await _publisher.PublishAsync(Manifest, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(ErrorMessages.ResourcePublicationNull);

            var restored = new HashSet<ResourceActionName>();
            foreach (ResourceStateSnapshot snapshot in snapshots)
            {
                if (snapshot is null)
                {
                    throw new InvalidOperationException(ErrorMessages.ResourcePublicationSnapshotNull);
                }

                if (!restored.Add(snapshot.Action))
                {
                    throw new InvalidOperationException(Format(ErrorMessages.ResourcePublicationSnapshotDuplicate, snapshot.Action));
                }

                ResourceActionResult result = await RestoreCoreAsync(snapshot, cancellationToken).ConfigureAwait(false);
                if (result.Status != ResourceActionStatus.Succeeded)
                {
                    throw new InvalidOperationException(Format(ErrorMessages.ResourceStateRestoreFailed, snapshot.Action, result.Status, result.Reason));
                }
            }

            published = true;
        }
        finally
        {
            // A failed start returns to created so the host may start the control plane again; a
            // stop that raced the start keeps its stopped state.
            Interlocked.CompareExchange(ref _lifecycle, published ? started : created, starting);
        }
    }

    /// <summary>
    /// Stops accepting action invocations and restores.
    /// </summary>
    /// <param name="cancellationToken">Unused; stopping completes synchronously.</param>
    /// <returns>A completed task.</returns>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _lifecycle, stopped);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceActionResult> ExecuteAsync(ResourceActionName action, ResourceActionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ThrowIfNotStarted();

        if (!_actions.TryGetValue(action, out IResourceAction? target))
        {
            return ResourceActionResult.NotFound();
        }

        if ((target.Kind & context.Operation) == ResourceActionKind.None)
        {
            return ResourceActionResult.NotSupported();
        }

        if (context.Operation == ResourceActionKind.Read)
        {
            return await InvokeAsync(target, context, cancellationToken).ConfigureAwait(false);
        }

        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ResourceActionResult result = await InvokeAsync(target, context, cancellationToken).ConfigureAwait(false);
            if (result.Status == ResourceActionStatus.Succeeded && target.State is { } state)
            {
                return result.WithState(ResourceStateSnapshot.Create(target.Name, state.GetSnapshot(Serializer)));
            }

            return result;
        }
        finally
        {
            _mutations.Release();
        }
    }

    /// <inheritdoc/>
    public ValueTask<ResourceActionResult> RestoreAsync(ResourceStateSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ThrowIfNotStarted();

        return RestoreCoreAsync(snapshot, cancellationToken);
    }

    private async ValueTask<ResourceActionResult> RestoreCoreAsync(ResourceStateSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!_actions.TryGetValue(snapshot.Action, out IResourceAction? target))
        {
            return ResourceActionResult.NotFound();
        }

        if (target.State is not { } state)
        {
            return ResourceActionResult.NotSupported();
        }

        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await state.RestoreAsync(snapshot.Value, Serializer, cancellationToken).ConfigureAwait(false);
            return ResourceActionResult.Success();
        }
        catch (ResourceContentException exception)
        {
            // The snapshot does not describe a value the state accepts: the sender's error.
            return ResourceActionResult.Rejected(exception.Message);
        }
        finally
        {
            _mutations.Release();
        }
    }

    private static async ValueTask<ResourceActionResult> InvokeAsync(IResourceAction action, ResourceActionContext context, CancellationToken cancellationToken)
    {
        ResourceActionResult? result;
        try
        {
            result = await action.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (ResourceContentException exception)
        {
            // The input did not bind to the action's input type, or the action refused its value:
            // the caller's error, not the action's.
            return ResourceActionResult.Rejected(exception.Message);
        }

        return result ?? throw new InvalidOperationException(Format(ErrorMessages.ResourceActionResultNull, action.Name));
    }

    private void ThrowIfNotStarted()
    {
        if (Volatile.Read(ref _lifecycle) != started)
        {
            throw new InvalidOperationException(ErrorMessages.ResourceControlPlaneNotStarted);
        }
    }

    private static string Format(string format, params object?[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }

    private sealed class DebugView
    {
        private readonly ResourceControlPlane _controlPlane;

        public DebugView(ResourceControlPlane controlPlane)
        {
            _controlPlane = controlPlane;
        }

        public ServiceId Id => _controlPlane.Id;

        public IResource Resource => _controlPlane.Resource;

        public ResourceManifest Manifest => _controlPlane.Manifest;

        public IResourceContentSerializer Serializer => _controlPlane.Serializer;
    }
}
