using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Tests;

/// <summary>
/// Records each publication and renewal, and answers with the snapshots and lease it was given, as a
/// gateway answers with the state written since the last deployment.
/// </summary>
internal sealed class TestPublisher : IApplicationInstancePublisher
{
    private readonly IReadOnlyList<ApplicationActionSnapshot> _snapshots;
    private int _renewals;
    private volatile bool _known = true;

    public TestPublisher(params ApplicationActionSnapshot[] snapshots)
    {
        _snapshots = snapshots;
    }

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
}
