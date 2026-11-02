using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.InProcess.Tests;

/// <summary>
/// An in-process gateway that records what instances send it, answers every publication with no
/// state and no lease, and keeps the control planes attached to it.
/// </summary>
internal sealed class TestGateway : IInProcessGateway
{
    private int _renewals;

    public ConcurrentQueue<ApplicationInstanceManifest> Published { get; } = new();

    public ConcurrentQueue<ApplicationInstanceManifest> Renewed { get; } = new();

    public ConcurrentDictionary<InstanceKey, IApplicationInstanceControlPlane> Attached { get; } = new();

    public int Renewals => Volatile.Read(ref _renewals);

    public ValueTask<ApplicationInstancePublication> PublishAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
    {
        Published.Enqueue(manifest);
        return ValueTask.FromResult(new ApplicationInstancePublication([], Lease: null));
    }

    public ValueTask<bool> RenewAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _renewals);
        Renewed.Enqueue(manifest);
        return ValueTask.FromResult(true);
    }

    public IDisposable Attach(IApplicationInstanceControlPlane controlPlane)
    {
        ArgumentNullException.ThrowIfNull(controlPlane);

        Attached[controlPlane.Manifest.Key] = controlPlane;
        return new Detach(this, controlPlane.Manifest.Key);
    }

    /// <summary>Creates a unique endpoint inside this process.</summary>
    public static Uri CreateEndpoint()
    {
        return new Uri($"inprocess://{Guid.NewGuid():N}/");
    }

    private sealed class Detach : IDisposable
    {
        private readonly TestGateway _gateway;
        private readonly InstanceKey _key;

        public Detach(TestGateway gateway, InstanceKey key)
        {
            _gateway = gateway;
            _key = key;
        }

        public void Dispose()
        {
            _gateway.Attached.TryRemove(_key, out _);
        }
    }
}
