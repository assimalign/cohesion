using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.KeyValuePair.Internal;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.KeyValuePair.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

/// <summary>
/// Executes key-value commands against the entry record space through the
/// primary key index — the model's <b>index-primary</b> composition: the B+Tree
/// is the key structure (key → packed entry location), the record space holds
/// the MVCC-stamped values, and every read seeks rather than scans (the inverse
/// of the SQL engine's scan-primary shape — deliberately, this is the kernel
/// generality exercise).
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads</b> (get/exists/scan) take no locks: the primary index's cursor
/// filters entries through the command's snapshot, and each fetched record's
/// stamps are re-checked against the same snapshot (defense in depth — entries
/// mirror record stamps by the maintenance discipline, so a divergence is a bug
/// this filter contains rather than surfaces).
/// </para>
/// <para>
/// <b>Writes</b> (put/delete) execute in two phases. Phase one — no physical
/// bracket: acquire the key's exclusive lock through the lock manager
/// (<c>LockResource.Entry(keySpace, key.Hash())</c>, the same identity the
/// B+Tree's unique enforcement locks internally, so its in-gate acquisition is a
/// same-owner re-grant), then resolve the key's visible version and re-validate
/// it against its <em>current</em> stamps — the latest-state check under the
/// lock: a version tombstoned by a concurrently committed transaction fails the
/// command with the retryable first-updater-wins conflict. Key-grain locks are
/// the model's <em>only</em> user-visible conflict surface: every mutation is
/// keyed by exactly one key, so the key lock subsumes the SQL engine's separate
/// per-row location locks (a genuine composition difference, recorded in
/// docs/DESIGN.md). Phase two — the coordinator's gated apply bracket: tombstone
/// the old version in place, insert the new one, mirror both in the primary
/// index, and record every effect in the version-store ledger so rollback can
/// undo it logically. Conditional misses (compare-and-swap) are decided under
/// the lock <em>before</em> the apply phase and return first-class not-applied
/// outcomes — no mutation, no exception.
/// </para>
/// </remarks>
internal sealed class KeyValueOperationExecutor
{
    /// <summary>
    /// The object id of the (single, implicit) key space: the owner tag of the
    /// entry record chain, the object the primary index registers under, and the
    /// object component of every key lock.
    /// </summary>
    internal const ulong KeySpaceObjectId = 1;

    /// <summary>
    /// The primary key index's name in the index directory and the catalog's
    /// registrations.
    /// </summary>
    internal const string PrimaryIndexName = "key";

    private static readonly IReadOnlyList<QueryColumn> entryColumns =
    [
        new QueryColumn { Name = "key", Ordinal = 0, Type = DatabaseType.Binary },
        new QueryColumn { Name = "value", Ordinal = 1, Type = DatabaseType.Binary },
        new QueryColumn { Name = "etag", Ordinal = 2, Type = DatabaseType.Int64 },
    ];

    private static readonly IReadOnlyList<QueryColumn> putColumns =
    [
        new QueryColumn { Name = "applied", Ordinal = 0, Type = DatabaseType.Boolean },
        new QueryColumn { Name = "etag", Ordinal = 1, Type = DatabaseType.Int64, IsNullable = true },
    ];

    private static readonly IReadOnlyList<QueryColumn> existsColumns =
    [
        new QueryColumn { Name = "exists", Ordinal = 0, Type = DatabaseType.Boolean },
    ];

    private readonly KeyValueStorage _storage;
    private readonly IIndex _primaryIndex;

    internal KeyValueOperationExecutor(KeyValueStorage storage, IIndex primaryIndex)
    {
        _storage = storage;
        _primaryIndex = primaryIndex;
    }

    /// <summary>
    /// Executes one key-value request under the given command context.
    /// </summary>
    internal async ValueTask<QueryResult> ExecuteAsync(KeyValueRequest request, KeyValueStatementContext context, CancellationToken cancellationToken)
    {
        return request switch
        {
            KeyValueGetRequest get => await ExecuteGetAsync(get, context, cancellationToken).ConfigureAwait(false),
            KeyValueExistsRequest exists => await ExecuteExistsAsync(exists, context, cancellationToken).ConfigureAwait(false),
            KeyValueScanRequest scan => await ExecuteScanAsync(scan, context, cancellationToken).ConfigureAwait(false),
            KeyValuePutRequest put => await ExecutePutAsync(put, context, cancellationToken).ConfigureAwait(false),
            KeyValueDeleteRequest delete => await ExecuteDeleteAsync(delete, context, cancellationToken).ConfigureAwait(false),
            _ => throw new DatabaseException($"The key-value engine has no executor for {request.GetType().Name}."),
        };
    }

    // ── Reads ──────────────────────────────────────────────────────────

    private async ValueTask<QueryResult> ExecuteGetAsync(KeyValueGetRequest request, KeyValueStatementContext context, CancellationToken cancellationToken)
    {
        var rows = new List<object?[]>(1);
        var current = await ResolveCurrentAsync(request.Key, context, cancellationToken).ConfigureAwait(false);

        if (current is not null)
        {
            rows.Add([current.Value.Key, current.Value.Value, (long)current.Value.Writer.Value]);
        }

        return new KeyValueMaterializedResultSet(entryColumns, rows);
    }

    private async ValueTask<QueryResult> ExecuteExistsAsync(KeyValueExistsRequest request, KeyValueStatementContext context, CancellationToken cancellationToken)
    {
        var current = await ResolveCurrentAsync(request.Key, context, cancellationToken).ConfigureAwait(false);

        return new KeyValueMaterializedResultSet(existsColumns, [[current is not null]]);
    }

    private async ValueTask<QueryResult> ExecuteScanAsync(KeyValueScanRequest request, KeyValueStatementContext context, CancellationToken cancellationToken)
    {
        IndexKeyRange range = BuildScanRange(request);
        var rows = new List<object?[]>();
        int limit = request.Limit ?? int.MaxValue;

        if (limit > 0)
        {
            await using var cursor = _primaryIndex.OpenCursor(context.Snapshot, range);

            while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                var version = ReadVisibleVersion(cursor.CurrentEntryReference, context.Snapshot);

                if (version is null)
                {
                    continue;
                }

                rows.Add([version.Value.Key, version.Value.Value, (long)version.Value.Writer.Value]);

                if (rows.Count >= limit)
                {
                    break;
                }
            }
        }

        return new KeyValueMaterializedResultSet(entryColumns, rows);
    }

    // ── Writes ─────────────────────────────────────────────────────────

    private async ValueTask<QueryResult> ExecutePutAsync(KeyValuePutRequest request, KeyValueStatementContext context, CancellationToken cancellationToken)
    {
        var indexKey = new IndexKey(request.Key);

        // Phase one: the key's exclusive lock — the model's single conflict
        // arbiter — acquired before the gate, per the lock-ordering rule.
        await context.Coordinator.LockManager.AcquireAsync(
            context.Transaction.Sequence, LockResource.Entry(KeySpaceObjectId, indexKey.Hash()), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);

        // Resolve the visible version and re-validate it as latest under the
        // lock: a set deleter from another transaction is a concurrently
        // committed change our snapshot cannot see — first-updater-wins.
        var current = await ResolveCurrentAsync(request.Key, context, cancellationToken).ConfigureAwait(false);

        if (current is not null)
        {
            EnsureLatestVersion(current.Value.Location, context.Transaction.Sequence);
        }

        // Conditional decisions happen under the lock, before any mutation: a
        // miss is a first-class not-applied outcome, never an exception.
        if (request.OnlyIfAbsent && current is not null)
        {
            return NotApplied((long)current.Value.Writer.Value);
        }

        if (request.ExpectedETag is { } expected
            && (current is null || expected != (long)current.Value.Writer.Value))
        {
            return NotApplied(current is null ? null : (long)current.Value.Writer.Value);
        }

        try
        {
            await context.Coordinator.ApplyStatementAsync<bool>(context.Transaction, async bracket =>
            {
                if (current is not null)
                {
                    TombstoneVersion(context, bracket, current.Value.Location);
                    await TombstoneIndexEntryAsync(context, indexKey, current.Value.Location, cancellationToken).ConfigureAwait(false);
                }

                byte[] record = KeyValueRecordCodec.Encode(request.Key.Span, request.Value.Span, context.Transaction.Sequence);
                var (pageId, slotIndex) = _storage.InsertEntry(bracket, KeySpaceObjectId, record);
                context.Coordinator.VersionStore.RecordCreated(context.Transaction.Sequence, pageId, slotIndex);

                ulong reference = KeyValueRecordLocation.Pack(pageId, slotIndex);
                await _primaryIndex.InsertAsync(context.Transaction, indexKey, reference, cancellationToken).ConfigureAwait(false);
                context.Coordinator.VersionStore.RecordIndexEntryCreated(context.Transaction.Sequence, _primaryIndex, indexKey, reference);

                return true;
            }, durable: false, cancellationToken).ConfigureAwait(false);
        }
        catch (IndexUniqueViolationException exception)
        {
            // The unique primary index found a live entry our snapshot cannot
            // see: a concurrently committed writer beat this command to the key.
            // The bracket has rolled back; the conflict is retryable.
            throw new TransactionAbortedException(
                $"Write-write conflict on key '{Convert.ToHexString(request.Key.Span)}': the key was concurrently written by a committed transaction (first-updater-wins). Retry the transaction.",
                exception);
        }

        return new KeyValueMaterializedResultSet(putColumns, [[true, (long)context.Transaction.Sequence.Value]], affectedCount: 1);
    }

    private async ValueTask<QueryResult> ExecuteDeleteAsync(KeyValueDeleteRequest request, KeyValueStatementContext context, CancellationToken cancellationToken)
    {
        var indexKey = new IndexKey(request.Key);

        await context.Coordinator.LockManager.AcquireAsync(
            context.Transaction.Sequence, LockResource.Entry(KeySpaceObjectId, indexKey.Hash()), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);

        var current = await ResolveCurrentAsync(request.Key, context, cancellationToken).ConfigureAwait(false);

        if (current is null)
        {
            return new KeyValueQueryResult(QueryResultStatus.Success, affectedCount: 0);
        }

        EnsureLatestVersion(current.Value.Location, context.Transaction.Sequence);

        if (request.ExpectedETag is { } expected && expected != (long)current.Value.Writer.Value)
        {
            // Compare-and-swap miss: a first-class no-delete outcome.
            return new KeyValueQueryResult(QueryResultStatus.Success, affectedCount: 0);
        }

        await context.Coordinator.ApplyStatementAsync<bool>(context.Transaction, async bracket =>
        {
            TombstoneVersion(context, bracket, current.Value.Location);
            await TombstoneIndexEntryAsync(context, indexKey, current.Value.Location, cancellationToken).ConfigureAwait(false);
            return true;
        }, durable: false, cancellationToken).ConfigureAwait(false);

        return new KeyValueQueryResult(QueryResultStatus.Success, affectedCount: 1);
    }

    // ── Version resolution and validation ──────────────────────────────

    /// <summary>
    /// Resolves the key's visible, live version through the command snapshot: an
    /// exact-key index seek, with each candidate's record stamps re-checked
    /// against the same snapshot and its decoded key compared to the requested
    /// key (an exact-range cursor cannot yield another key; the comparison is
    /// defense in depth against a corrupted entry reference).
    /// </summary>
    private async ValueTask<ResolvedVersion?> ResolveCurrentAsync(ReadOnlyMemory<byte> key, KeyValueStatementContext context, CancellationToken cancellationToken)
    {
        var indexKey = new IndexKey(key);
        var range = new IndexKeyRange(indexKey, indexKey, IsStartInclusive: true, IsEndInclusive: true);

        await using var cursor = _primaryIndex.OpenCursor(context.Snapshot, range);

        while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            var version = ReadVisibleVersion(cursor.CurrentEntryReference, context.Snapshot);

            if (version is null || !version.Value.Key.AsSpan().SequenceEqual(key.Span))
            {
                continue;
            }

            return version;
        }

        return null;
    }

    /// <summary>
    /// Reads and decodes the record behind an index entry, returning it only when
    /// its stamps admit it through the snapshot (visible writer, no visible
    /// deleter). A missing or reverted slot reads as absence.
    /// </summary>
    private ResolvedVersion? ReadVisibleVersion(ulong entryReference, TransactionSnapshot snapshot)
    {
        var (pageId, slotIndex) = KeyValueRecordLocation.Unpack(entryReference);

        ReadOnlyMemory<byte> record;
        try
        {
            record = _storage.ReadEntry(pageId, slotIndex);
        }
        catch (StorageException)
        {
            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            // The slot was reverted out of existence by a bracket rollback.
            return null;
        }

        if (!KeyValueRecordCodec.TryDecode(record.Span, out byte[] key, out byte[] value, out var writer, out var deleter))
        {
            return null;
        }

        bool visible = snapshot.IsVisible(writer)
            && (deleter == TransactionSequence.None || !snapshot.IsVisible(deleter));

        return visible ? new ResolvedVersion(key, value, writer, entryReference) : null;
    }

    /// <summary>
    /// The latest-state check under the exclusive key lock: a version tombstoned
    /// by a concurrently committed transaction fails the command with the
    /// retryable write-write conflict — first-updater-wins. (A snapshot
    /// visibility check alone would admit write skew; this is the B+Tree
    /// uniqueness-discipline precedent, the same check the SQL engine runs per
    /// target row.)
    /// </summary>
    /// <exception cref="TransactionAbortedException">The key was modified by a concurrently committed transaction.</exception>
    private void EnsureLatestVersion(ulong entryReference, TransactionSequence self)
    {
        var (pageId, slotIndex) = KeyValueRecordLocation.Unpack(entryReference);

        ReadOnlyMemory<byte> record;
        try
        {
            record = _storage.ReadEntry(pageId, slotIndex);
        }
        catch (StorageException)
        {
            record = ReadOnlyMemory<byte>.Empty;
        }
        catch (ArgumentOutOfRangeException)
        {
            record = ReadOnlyMemory<byte>.Empty;
        }

        if (record.Length < KeyValueRecordCodec.StampHeaderSize)
        {
            throw new TransactionAbortedException(
                "Write-write conflict: the target entry version was reclaimed by a concurrent transaction. Retry the transaction.");
        }

        var (_, deleter) = KeyValueRecordCodec.ReadStamps(record.Span);

        if (deleter != TransactionSequence.None && deleter != self)
        {
            throw new TransactionAbortedException(
                $"Write-write conflict: the entry was modified by concurrently committed transaction {deleter} (first-updater-wins). Retry the transaction.");
        }
    }

    /// <summary>
    /// Stamps the record's deleter with the command's transaction sequence — the
    /// same-length in-place tombstone write — and records it in the version-store
    /// ledger for logical undo and pruning.
    /// </summary>
    private void TombstoneVersion(KeyValueStatementContext context, IStorageTransaction bracket, ulong entryReference)
    {
        var (pageId, slotIndex) = KeyValueRecordLocation.Unpack(entryReference);
        var current = _storage.ReadEntry(pageId, slotIndex);
        byte[] tombstoned = KeyValueRecordCodec.WithDeleter(current.Span, context.Transaction.Sequence);
        _storage.UpdateEntry(bracket, pageId, slotIndex, tombstoned);
        context.Coordinator.VersionStore.RecordTombstoned(context.Transaction.Sequence, pageId, slotIndex);
    }

    /// <summary>
    /// Tombstones the version's primary-index entry with the command's
    /// transaction sequence (mirroring the record's deleter stamp) and records it
    /// in the ledger so a logical rollback restores it.
    /// </summary>
    private async ValueTask TombstoneIndexEntryAsync(KeyValueStatementContext context, IndexKey indexKey, ulong entryReference, CancellationToken cancellationToken)
    {
        await _primaryIndex.DeleteAsync(context.Transaction, indexKey, entryReference, cancellationToken).ConfigureAwait(false);
        context.Coordinator.VersionStore.RecordIndexEntryTombstoned(context.Transaction.Sequence, _primaryIndex, indexKey, entryReference);
    }

    private static KeyValueMaterializedResultSet NotApplied(long? currentETag)
        => new(putColumns, [[false, currentETag]], affectedCount: 0);

    /// <summary>
    /// Builds the index key range for a scan: an explicit [start, end) range, or
    /// a prefix mapped to [prefix, successor(prefix)) via byte-successor
    /// arithmetic (a prefix of all 0xFF bytes has no successor — the range is
    /// unbounded above).
    /// </summary>
    private static IndexKeyRange BuildScanRange(KeyValueScanRequest request)
    {
        if (request.Prefix is { } prefix)
        {
            IndexKey? end = Successor(prefix.Span) is { } successor ? new IndexKey(successor) : null;
            return new IndexKeyRange(new IndexKey(prefix), end, IsStartInclusive: true, IsEndInclusive: false);
        }

        IndexKey? start = request.Start is { } s ? new IndexKey(s) : null;
        IndexKey? endBound = request.End is { } e ? new IndexKey(e) : null;

        return new IndexKeyRange(start, endBound, IsStartInclusive: true, IsEndInclusive: false);
    }

    /// <summary>
    /// Computes the shortest key strictly greater than every key carrying the
    /// prefix: increment the last non-0xFF byte and truncate behind it. Null when
    /// no such key exists (the prefix is all 0xFF bytes).
    /// </summary>
    private static byte[]? Successor(ReadOnlySpan<byte> prefix)
    {
        for (int index = prefix.Length - 1; index >= 0; index--)
        {
            if (prefix[index] != 0xFF)
            {
                byte[] successor = prefix.Slice(0, index + 1).ToArray();
                successor[index]++;
                return successor;
            }
        }

        return null;
    }

    private readonly record struct ResolvedVersion(byte[] Key, byte[] Value, TransactionSequence Writer, ulong Location);
}
