using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

/// <summary>
/// Records each publication and renewal, and answers with the snapshots and lease it was given, as a
/// gateway answers with the state written since the last deployment.
/// </summary>
internal sealed class TestPublisher : IResourceManifestPublisher
{
    private readonly IReadOnlyList<ResourceStateSnapshot> _snapshots;
    private int _renewals;
    private volatile bool _known = true;

    public TestPublisher(params ResourceStateSnapshot[] snapshots)
    {
        _snapshots = snapshots;
    }

    public ConcurrentQueue<ResourceManifest> Published { get; } = new();

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

    public async ValueTask<ResourcePublication> PublishAsync(ResourceManifest manifest, CancellationToken cancellationToken = default)
    {
        Published.Enqueue(manifest);
        await PublishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new ResourcePublication(_snapshots, Lease);
    }

    public ValueTask<bool> RenewAsync(ResourceManifest manifest, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _renewals);
        if (RenewalFailure is { } failure)
        {
            throw failure;
        }

        return ValueTask.FromResult(Known);
    }
}
