using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.IdentityHub;

namespace Assimalign.Cohesion.IdentityHub.Hosting.Tests;

/// <summary>
/// An in-memory store that counts how often it is disposed, so a test can tell whether the
/// application owned it.
/// </summary>
internal sealed class DisposableIdentityHubStore : IdentityHubStore, IDisposable
{
    private readonly InMemoryIdentityHubStore _inner = new();

    public int DisposeCount { get; private set; }

    public void Dispose()
    {
        DisposeCount++;
    }

    protected override ValueTask<IdentityHubStoredRecord?> FindCoreAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecordKey key,
        CancellationToken cancellationToken)
    {
        return _inner.FindAsync(set, key, cancellationToken);
    }

    protected override ValueTask<IdentityHubRecordPage> QueryCoreAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecordQuery query,
        CancellationToken cancellationToken)
    {
        return _inner.QueryAsync(set, query, cancellationToken);
    }

    protected override ValueTask<IdentityHubWriteResult> TryAddCoreAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecord record,
        CancellationToken cancellationToken)
    {
        return _inner.TryAddAsync(set, record, cancellationToken);
    }

    protected override ValueTask<IdentityHubWriteResult> TryReplaceCoreAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecord record,
        IdentityHubRecordVersion expectedVersion,
        CancellationToken cancellationToken)
    {
        return _inner.TryReplaceAsync(set, record, expectedVersion, cancellationToken);
    }

    protected override ValueTask<bool> TryRemoveCoreAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecordKey key,
        IdentityHubRecordVersion expectedVersion,
        CancellationToken cancellationToken)
    {
        return _inner.TryRemoveAsync(set, key, expectedVersion, cancellationToken);
    }
}
