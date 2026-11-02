using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.LogSpace.Hosting.ApplicationModel.Tests;

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

    public ValueTask<ApplicationInstancePublication> PublishAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
    {
        _manifests.Enqueue(manifest);
        return ValueTask.FromResult(new ApplicationInstancePublication([], null));
    }

    public ValueTask<bool> RenewAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(true);
    }

    public ValueTask StartAsync(IApplicationInstanceControlPlane controlPlane, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controlPlane);

        _serving = controlPlane;
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        _serving = null;
        return ValueTask.CompletedTask;
    }
}
