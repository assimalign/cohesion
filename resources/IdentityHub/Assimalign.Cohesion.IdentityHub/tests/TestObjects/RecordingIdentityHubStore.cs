using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.IdentityHub.Tests;

/// <summary>
/// A store that keeps nothing: it records which <c>Core</c> members the base class reached and
/// with what, and answers each with a value a test sets.
/// </summary>
internal sealed class RecordingIdentityHubStore : IdentityHubStore
{
    public List<string> Calls { get; } = new();

    public IdentityHubRecordSetName Set { get; private set; }

    public IdentityHubRecordKey Key { get; private set; }

    public IdentityHubRecordVersion ExpectedVersion { get; private set; }

    public IdentityHubRecord? Record { get; private set; }

    public IdentityHubRecordQuery? Query { get; private set; }

    public CancellationToken CancellationToken { get; private set; }

    public IdentityHubStoredRecord? Found { get; set; }

    public IdentityHubRecordPage? Page { get; set; } = IdentityHubRecordPage.Empty;

    public IdentityHubWriteResult WriteResult { get; set; } = IdentityHubWriteResult.Written("1");

    public bool Removed { get; set; } = true;

    protected override ValueTask<IdentityHubStoredRecord?> FindCoreAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecordKey key,
        CancellationToken cancellationToken)
    {
        Calls.Add(nameof(FindCoreAsync));
        Set = set;
        Key = key;
        CancellationToken = cancellationToken;
        return ValueTask.FromResult(Found);
    }

    protected override ValueTask<IdentityHubRecordPage> QueryCoreAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecordQuery query,
        CancellationToken cancellationToken)
    {
        Calls.Add(nameof(QueryCoreAsync));
        Set = set;
        Query = query;
        CancellationToken = cancellationToken;
        return ValueTask.FromResult(Page!);
    }

    protected override ValueTask<IdentityHubWriteResult> TryAddCoreAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecord record,
        CancellationToken cancellationToken)
    {
        Calls.Add(nameof(TryAddCoreAsync));
        Set = set;
        Record = record;
        CancellationToken = cancellationToken;
        return ValueTask.FromResult(WriteResult);
    }

    protected override ValueTask<IdentityHubWriteResult> TryReplaceCoreAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecord record,
        IdentityHubRecordVersion expectedVersion,
        CancellationToken cancellationToken)
    {
        Calls.Add(nameof(TryReplaceCoreAsync));
        Set = set;
        Record = record;
        ExpectedVersion = expectedVersion;
        CancellationToken = cancellationToken;
        return ValueTask.FromResult(WriteResult);
    }

    protected override ValueTask<bool> TryRemoveCoreAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecordKey key,
        IdentityHubRecordVersion expectedVersion,
        CancellationToken cancellationToken)
    {
        Calls.Add(nameof(TryRemoveCoreAsync));
        Set = set;
        Key = key;
        ExpectedVersion = expectedVersion;
        CancellationToken = cancellationToken;
        return ValueTask.FromResult(Removed);
    }
}
