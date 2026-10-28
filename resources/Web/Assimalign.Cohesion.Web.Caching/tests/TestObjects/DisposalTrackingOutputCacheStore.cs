using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Caching.Tests.TestObjects;

/// <summary>
/// A disposable <see cref="IOutputCacheStore"/> that stores nothing and records whether it was disposed,
/// so a test can assert that the middleware leaves a store the application supplied to its owner.
/// </summary>
internal sealed class DisposalTrackingOutputCacheStore : IOutputCacheStore, IDisposable
{
    public bool IsDisposed { get; private set; }

    public ValueTask<OutputCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<OutputCacheEntry?>(null);

    public ValueTask SetAsync(string key, OutputCacheEntry entry, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public void Dispose() => IsDisposed = true;
}
