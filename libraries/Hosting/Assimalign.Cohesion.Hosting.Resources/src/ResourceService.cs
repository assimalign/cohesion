using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Joins a resource instance to its host and its gateway.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StartingAsync"/> publishes the control plane's manifest, restores every snapshot the
/// publisher answers with, and, when the answer carries a lease, starts renewing it. A host completes
/// <see cref="StartingAsync"/> for every lifecycle service before it starts any service or server, so
/// the instance serves only once the state written since the resource's last deployment is in
/// effect. A failed publication fails the host's start; the gateway tears down an instance whose
/// manifest never arrives, and the platform's restart policy decides what runs next.
/// </para>
/// <para>
/// Renewal runs three times per lease until <see cref="StopAsync"/>, so one lost renewal does not
/// lapse the registration. A gateway that no longer knows the instance, such as one restarted
/// without its record, receives the manifest again, and the service restores what it answers with.
/// A failed renewal is retried on the next one: the instance keeps serving while its gateway is
/// unreachable.
/// </para>
/// <para>
/// A host may start the service again once it has stopped; each start publishes again.
/// </para>
/// </remarks>
[DebuggerDisplay("{ControlPlane.Resource.Provider}/{ControlPlane.Resource.Name} {ControlPlane.Resource.Key}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class ResourceService : IResourceService
{
    private const int stopped = 0;
    private const int starting = 1;
    private const int started = 2;

    // A stop that arrived while a start was publishing: the start ends stopped, without renewing.
    private const int stopRequested = 3;

    private static readonly TimeSpan _minimumRenewalInterval = TimeSpan.FromMilliseconds(10);

    private readonly IResourceManifestPublisher _publisher;
    private readonly Lock _lifecycleLock = new();
    private CancellationTokenSource? _renewal;
    private Task _renewing = Task.CompletedTask;
    private int _lifecycle;

    private ResourceService(IResourceControlPlane controlPlane, IResourceManifestPublisher publisher)
    {
        ControlPlane = controlPlane;
        _publisher = publisher;
    }

    /// <inheritdoc/>
    public ServiceId Id { get; } = ServiceId.New();

    /// <inheritdoc/>
    public IResourceControlPlane ControlPlane { get; }

    /// <summary>
    /// Creates the service that joins a resource instance to its host and its gateway.
    /// </summary>
    /// <param name="controlPlane">The instance's control plane.</param>
    /// <param name="publisher">Publishes the control plane's manifest to the instance's gateway.</param>
    /// <returns>The service.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static ResourceService Create(IResourceControlPlane controlPlane, IResourceManifestPublisher publisher)
    {
        ArgumentNullException.ThrowIfNull(controlPlane);
        ArgumentNullException.ThrowIfNull(publisher);

        return new ResourceService(controlPlane, publisher);
    }

    /// <summary>
    /// Publishes the instance's manifest, restores every snapshot the publisher returns, and, when
    /// the publication carries a lease, starts renewing it.
    /// </summary>
    /// <param name="cancellationToken">Cancels publication and restores.</param>
    /// <returns>A task that completes when the returned state is in effect.</returns>
    /// <exception cref="InvalidOperationException">
    /// The service is started or starting; the publisher returned <see langword="null"/>, a
    /// publication without a snapshot list, a lease that is not positive, a <see langword="null"/>
    /// snapshot, or two snapshots for one action; or a snapshot was not restored. A failed start
    /// leaves the service stopped.
    /// </exception>
    public async Task StartingAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleLock)
        {
            if (_lifecycle != stopped)
            {
                throw new InvalidOperationException(ErrorMessages.ResourceServiceAlreadyStarted);
            }

            _lifecycle = starting;
        }

        ResourcePublication publication;
        try
        {
            publication = await PublishAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_lifecycleLock)
            {
                _lifecycle = stopped;
            }

            throw;
        }

        lock (_lifecycleLock)
        {
            if (_lifecycle == stopRequested)
            {
                _lifecycle = stopped;
                return;
            }

            _lifecycle = started;
            if (publication.Lease is { } lease)
            {
                var renewal = new CancellationTokenSource();
                _renewal = renewal;
                _renewing = RenewAsync(lease, renewal.Token);
            }
        }
    }

    /// <summary>
    /// Completes at once: the service publishes and restores in <see cref="StartingAsync"/>.
    /// </summary>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>A completed task.</returns>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Completes at once.
    /// </summary>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>A completed task.</returns>
    public Task StartedAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Completes at once: the registration stays alive while the host drains.
    /// </summary>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>A completed task.</returns>
    public Task StoppingAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops renewing the registration; the gateway drops the instance once its lease lapses.
    /// </summary>
    /// <remarks>
    /// A stop that arrives while a start is publishing ends that start without renewing. A failed
    /// start's rollback calls only this member, so it releases everything the service holds.
    /// </remarks>
    /// <param name="cancellationToken">Unused; stopping waits only for a renewal in flight to end.</param>
    /// <returns>A task that completes when renewal has stopped.</returns>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? renewal;
        Task renewing;
        lock (_lifecycleLock)
        {
            if (_lifecycle == starting)
            {
                _lifecycle = stopRequested;
                return;
            }

            if (_lifecycle == started)
            {
                _lifecycle = stopped;
            }

            renewal = _renewal;
            renewing = _renewing;
            _renewal = null;
        }

        if (renewal is not null)
        {
            await renewal.CancelAsync().ConfigureAwait(false);
            await renewing.ConfigureAwait(false);
            renewal.Dispose();
        }
    }

    /// <summary>
    /// Completes at once.
    /// </summary>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>A completed task.</returns>
    public Task StoppedAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    private async Task<ResourcePublication> PublishAsync(CancellationToken cancellationToken)
    {
        ResourcePublication? publication = await _publisher.PublishAsync(ControlPlane.Manifest, cancellationToken).ConfigureAwait(false);
        if (publication?.Snapshots is null)
        {
            throw new InvalidOperationException(ErrorMessages.ResourcePublicationNull);
        }

        if (publication.Lease is { } lease && lease <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(Format(ErrorMessages.ResourcePublicationLeaseInvalid, lease));
        }

        var restored = new HashSet<ResourceActionName>();
        foreach (ResourceStateSnapshot snapshot in publication.Snapshots)
        {
            if (snapshot is null)
            {
                throw new InvalidOperationException(ErrorMessages.ResourcePublicationSnapshotNull);
            }

            if (!restored.Add(snapshot.Action))
            {
                throw new InvalidOperationException(Format(ErrorMessages.ResourcePublicationSnapshotDuplicate, snapshot.Action));
            }

            ResourceActionResult result = await ControlPlane.RestoreAsync(snapshot, cancellationToken).ConfigureAwait(false);
            if (result.Status != ResourceActionStatus.Succeeded)
            {
                throw new InvalidOperationException(Format(ErrorMessages.ResourceStateRestoreFailed, snapshot.Action, result.Status, result.Reason));
            }
        }

        return publication;
    }

    private async Task RenewAsync(TimeSpan lease, CancellationToken cancellationToken)
    {
        TimeSpan interval = lease / 3;
        if (interval < _minimumRenewalInterval)
        {
            interval = _minimumRenewalInterval;
        }

        ResourceManifest manifest = ControlPlane.Manifest;
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    if (await _publisher.RenewAsync(manifest, cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    // The gateway does not know the instance: publish again and restore what it
                    // answers with. Restores are idempotent, so state already in effect is harmless.
                    ResourcePublication? publication = await _publisher.PublishAsync(manifest, cancellationToken).ConfigureAwait(false);
                    foreach (ResourceStateSnapshot? snapshot in publication?.Snapshots ?? [])
                    {
                        if (snapshot is not null)
                        {
                            await ControlPlane.RestoreAsync(snapshot, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // Deliberately broad: each transport reports an unreachable or refusing gateway
                    // in its own terms. The instance keeps serving and retries on the next tick.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The service stopped.
        }
    }

    private static string Format(string format, params object?[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }

    private sealed class DebugView
    {
        private readonly ResourceService _service;

        public DebugView(ResourceService service)
        {
            _service = service;
        }

        public ServiceId Id => _service.Id;

        public IResourceControlPlane ControlPlane => _service.ControlPlane;
    }
}
