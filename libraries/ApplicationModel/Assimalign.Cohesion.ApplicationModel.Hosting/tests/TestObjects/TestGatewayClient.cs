using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Tests;

/// <summary>
/// A platform's gateway client that records each publication and renewal and answers with the
/// snapshots and lease it was given, as a gateway answers with the state written since the last
/// deployment. It records the control plane it carries the gateway's calls to while it does.
/// </summary>
internal sealed class TestGatewayClient : IApplicationResourceInstanceGatewayClient
{
    private readonly IReadOnlyList<ApplicationActionSnapshot> _snapshots;
    private int _renewals;
    private volatile bool _known = true;
    private volatile IApplicationInstanceControlPlane? _serving;

    public TestGatewayClient(params ApplicationActionSnapshot[] snapshots)
    {
        _snapshots = snapshots;
    }

    public PlatformName Platform { get; init; } = "test";

    public ConcurrentQueue<ApplicationInstanceManifest> Published { get; } = new();

    /// <summary>The lease publications answer with; none unless a test sets one.</summary>
    public TimeSpan? Lease { get; init; }

    /// <summary>Whether a renewal finds the instance registered.</summary>
    public bool Known
    {
        get => _known;
        set => _known = value;
    }

    /// <summary>An exception every renewal throws, as an unreachable gateway would.</summary>
    public Exception? RenewalFailure { get; init; }

    public int Renewals => Volatile.Read(ref _renewals);

    /// <summary>A task each publication waits for before it answers, as a slow gateway would.</summary>
    public Task PublishGate { get; init; } = Task.CompletedTask;

    /// <summary>The control plane the gateway's calls reach, or <see langword="null"/> when they reach none.</summary>
    public IApplicationInstanceControlPlane? Serving => _serving;

    /// <summary>What the control plane's actions held when the client started carrying calls to it.</summary>
    public Func<IApplicationInstanceControlPlane, object?>? OnStart { get; init; }

    /// <summary>The value <see cref="OnStart"/> read.</summary>
    public object? ReadAtStart { get; private set; }

    public async ValueTask<ApplicationInstancePublication> PublishAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
    {
        Published.Enqueue(manifest);
        await PublishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new ApplicationInstancePublication(_snapshots, Lease);
    }

    public ValueTask<bool> RenewAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _renewals);
        if (RenewalFailure is { } failure)
        {
            throw failure;
        }

        return ValueTask.FromResult(Known);
    }

    public ValueTask StartAsync(IApplicationInstanceControlPlane controlPlane, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controlPlane);

        ReadAtStart = OnStart?.Invoke(controlPlane);
        _serving = controlPlane;
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        _serving = null;
        return ValueTask.CompletedTask;
    }
}
