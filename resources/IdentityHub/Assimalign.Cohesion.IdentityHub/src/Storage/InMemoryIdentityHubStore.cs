using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.IdentityHub.Internal;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The process-local <see cref="IIdentityHubStore"/>: every record lives in this object and is
/// gone when the process ends.
/// </summary>
/// <remarks>
/// <para>
/// It is the reference implementation of the store contract, and the store for tests and for a
/// hub that runs on one node and may forget everything. A hub that keeps signing keys, clients,
/// or grants across a restart, or that runs on more than one node, uses a durable store.
/// </para>
/// <para>
/// Each record set has one lock, so a conditional write and the checks that refuse it are a
/// single step. There is no background timer: an expired record is dropped the next time an
/// operation meets it, and every write to a record set first drops all of that set's expired
/// records, so records that expire and are never read again do not accumulate. Records are
/// listed in the ordinal order of their keys.
/// </para>
/// <para>
/// The clock is a <see cref="TimeProvider"/> so that tests drive expiry; the parameterless
/// constructor uses <see cref="TimeProvider.System"/>.
/// </para>
/// </remarks>
[DebuggerDisplay("InMemoryIdentityHubStore")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class InMemoryIdentityHubStore : IdentityHubStore
{
    private readonly ConcurrentDictionary<string, InMemoryRecordSet> _sets = new(StringComparer.Ordinal);
    private readonly Func<IdentityHubRecordVersion> _nextVersion;
    private readonly TimeProvider _timeProvider;
    private long _version;

    /// <summary>
    /// Initializes an empty store that reads the system clock.
    /// </summary>
    public InMemoryIdentityHubStore()
        : this(TimeProvider.System)
    {
    }

    private InMemoryIdentityHubStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _nextVersion = NextVersion;
    }

    /// <summary>
    /// Creates an empty store that reads the supplied clock.
    /// </summary>
    /// <param name="timeProvider">The clock that decides when a record has expired.</param>
    /// <returns>The store.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="timeProvider"/> is <see langword="null"/>.</exception>
    public static InMemoryIdentityHubStore Create(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return new InMemoryIdentityHubStore(timeProvider);
    }

    /// <inheritdoc />
    protected override ValueTask<IdentityHubStoredRecord?> FindCoreAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecordKey key,
        CancellationToken cancellationToken)
    {
        IdentityHubStoredRecord? stored = _sets.TryGetValue(set.Value, out InMemoryRecordSet? records)
            ? records.Find(key, _timeProvider.GetUtcNow())
            : null;
        return ValueTask.FromResult(stored);
    }

    /// <inheritdoc />
    protected override ValueTask<IdentityHubRecordPage> QueryCoreAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecordQuery query,
        CancellationToken cancellationToken)
    {
        // A set that was never written to still reads its continuation, so that a token this
        // store did not issue is refused whether or not the set holds records.
        InMemoryRecordSet records = _sets.TryGetValue(set.Value, out InMemoryRecordSet? existing)
            ? existing
            : new InMemoryRecordSet(set);
        return ValueTask.FromResult(records.Query(query, _timeProvider.GetUtcNow()));
    }

    /// <inheritdoc />
    protected override ValueTask<IdentityHubWriteResult> TryAddCoreAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecord record,
        CancellationToken cancellationToken)
    {
        InMemoryRecordSet records = _sets.GetOrAdd(set.Value, static (_, name) => new InMemoryRecordSet(name), set);
        return ValueTask.FromResult(records.TryAdd(record, _timeProvider.GetUtcNow(), _nextVersion));
    }

    /// <inheritdoc />
    protected override ValueTask<IdentityHubWriteResult> TryReplaceCoreAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecord record,
        IdentityHubRecordVersion expectedVersion,
        CancellationToken cancellationToken)
    {
        IdentityHubWriteResult result = _sets.TryGetValue(set.Value, out InMemoryRecordSet? records)
            ? records.TryReplace(record, expectedVersion, _timeProvider.GetUtcNow(), _nextVersion)
            : IdentityHubWriteResult.VersionConflict;
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    protected override ValueTask<bool> TryRemoveCoreAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecordKey key,
        IdentityHubRecordVersion expectedVersion,
        CancellationToken cancellationToken)
    {
        bool removed = _sets.TryGetValue(set.Value, out InMemoryRecordSet? records) &&
            records.TryRemove(key, expectedVersion, _timeProvider.GetUtcNow());
        return ValueTask.FromResult(removed);
    }

    /// <summary>
    /// Counts the records a set holds in memory, expired ones not yet reclaimed included.
    /// </summary>
    /// <param name="set">The record set.</param>
    /// <returns>The number of records held.</returns>
    internal int CountHeld(IdentityHubRecordSetName set)
    {
        return _sets.TryGetValue(set.Value, out InMemoryRecordSet? records) ? records.HeldCount : 0;
    }

    // One counter for the whole store: a record that is removed and added again never gets a
    // version it held before, so a stale conditional write cannot match the new record.
    private IdentityHubRecordVersion NextVersion()
    {
        return new IdentityHubRecordVersion(Interlocked.Increment(ref _version).ToString(CultureInfo.InvariantCulture));
    }

    private sealed class DebugView
    {
        public DebugView(InMemoryIdentityHubStore store)
        {
        }
    }
}
