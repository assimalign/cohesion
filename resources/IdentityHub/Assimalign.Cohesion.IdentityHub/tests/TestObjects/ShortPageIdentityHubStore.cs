using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.IdentityHub.Tests;

/// <summary>
/// A store that pages as stingily as the contract allows: the first page of every query is empty
/// and still carries a continuation, and every later page holds one record at most. Everything
/// else is the inner store's.
/// </summary>
internal sealed class ShortPageIdentityHubStore : IdentityHubStore
{
    private const string First = "first";
    private const string Prefix = "next:";

    private readonly IIdentityHubStore _inner;

    public ShortPageIdentityHubStore(IIdentityHubStore inner)
    {
        _inner = inner;
    }

    public int Queries { get; private set; }

    protected override ValueTask<IdentityHubStoredRecord?> FindCoreAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecordKey key,
        CancellationToken cancellationToken)
    {
        return _inner.FindAsync(set, key, cancellationToken);
    }

    protected override async ValueTask<IdentityHubRecordPage> QueryCoreAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecordQuery query,
        CancellationToken cancellationToken)
    {
        Queries++;
        if (query.Continuation is null)
        {
            return new IdentityHubRecordPage(Array.Empty<IdentityHubStoredRecord>(), First);
        }

        IdentityHubRecordQuery one = query.Index is { } index && query.Value is { } value
            ? IdentityHubRecordQuery.ByIndex(index, value, limit: 1)
            : IdentityHubRecordQuery.All(limit: 1);
        if (!string.Equals(query.Continuation, First, StringComparison.Ordinal))
        {
            one = one.After(query.Continuation[Prefix.Length..]);
        }

        IdentityHubRecordPage page = await _inner.QueryAsync(set, one, cancellationToken);
        return new IdentityHubRecordPage(page.Records, page.Continuation is null ? null : Prefix + page.Continuation);
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
