using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.ApplicationModel.Tests;

/// <summary>
/// A throwaway platform for one resource instance: its client records the manifests the instance
/// publishes and holds the control plane the instance opens to it.
/// </summary>
internal sealed class TestGatewayClient : IApplicationInstanceGatewayClient
{
    private readonly ConcurrentQueue<ApplicationInstanceManifest> _manifests = new();
    private volatile IApplicationInstanceControlPlane? _serving;

    public PlatformName Platform => "test";

    /// <summary>
    /// Gets the manifests received so far, in arrival order.
    /// </summary>
    public IReadOnlyCollection<ApplicationInstanceManifest> Manifests => _manifests;

    /// <summary>
    /// Gets the control plane the gateway's calls reach, or <see langword="null"/> while the instance
    /// has not opened it or has closed it.
    /// </summary>
    public IApplicationInstanceControlPlane? Serving => _serving;

    public Func<CancellationToken, Task>? OnPublish { get; set; }
    public Func<CancellationToken, Task>? OnStart { get; set; }
    public Func<CancellationToken, Task>? OnStop { get; set; }
    public IReadOnlyList<ApplicationActionSnapshot> Snapshots { get; set; } = [];
    public bool Started { get; private set; }
    public bool Stopped { get; private set; }

    public async ValueTask<ApplicationInstancePublication> PublishAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
    {
        _manifests.Enqueue(manifest);
        if (OnPublish is { } publish)
        {
            await publish(cancellationToken);
        }
        return new ApplicationInstancePublication(Snapshots, null);
    }

    public ValueTask<bool> RenewAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(true);
    }

    public async ValueTask StartAsync(IApplicationInstanceControlPlane controlPlane, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controlPlane);

        if (OnStart is { } start)
        {
            await start(cancellationToken);
        }
        _serving = controlPlane;
        Started = true;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (OnStop is { } stop)
        {
            await stop(cancellationToken);
        }
        _serving = null;
        Stopped = true;
    }
}
