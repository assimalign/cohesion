using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.KeyValuePair.Internal;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.KeyValuePair.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// The key-value engine's <see cref="IVersionStore"/>, implemented over the entry
/// record space itself (the contract's intended shape: "model storage layers
/// implement this over their page layouts"). Version payloads live in the record
/// space — a put over an existing key tombstones the old version in place and
/// inserts the new one — so the store holds no copies: it is the <b>ledger</b> of
/// each writer's physical effects (created versions, tombstoned versions, and
/// their primary-index mirrors), which is what makes logical undo
/// (<see cref="PurgeWriterAsync"/>) and space reclamation (<see cref="PruneAsync"/>)
/// executable.
/// </summary>
/// <remarks>
/// Adapted from the SQL engine's record-space version store — the mechanics are
/// deliberately identical (same stamp discipline, same ledger duties). That the
/// second model needed a near-verbatim copy is recorded as a kernel gap in the
/// area DESIGN's generality report: the record-space version store is
/// model-agnostic machinery that wants a kernel home.
/// </remarks>
internal sealed class KeyValueVersionStore : IVersionStore
{
    private readonly KeyValueStorage _storage;
    private readonly SemaphoreSlim _applyGate;
    private readonly Dictionary<ulong, List<LedgerEntry>> _ledger = new();
    private readonly List<PrunableVersion> _prunable = new();
    private readonly HashSet<ulong> _pendingAbortedPurges = new();
    private readonly object _sync = new();

    internal KeyValueVersionStore(KeyValueStorage storage, SemaphoreSlim applyGate)
    {
        _storage = storage;
        _applyGate = applyGate;
    }

    /// <summary>
    /// Gets the number of in-flight ledger entries plus retained prunable
    /// versions (test observability: "version-store size").
    /// </summary>
    internal int TrackedVersionCount
    {
        get
        {
            lock (_sync)
            {
                int count = _prunable.Count;

                foreach (var entries in _ledger.Values)
                {
                    count += entries.Count;
                }

                return count;
            }
        }
    }

    /// <summary>
    /// Gets the writers whose abort-time undo did not complete and is retried
    /// by the version-purge worker.
    /// </summary>
    internal IReadOnlyCollection<ulong> PendingAbortedPurges
    {
        get
        {
            lock (_sync)
            {
                return [.. _pendingAbortedPurges];
            }
        }
    }

    /// <summary>
    /// Records that <paramref name="writer"/> created the version at the given
    /// location (a put's new version). Called inside the statement's apply bracket.
    /// </summary>
    internal void RecordCreated(TransactionSequence writer, PageId pageId, int slotIndex)
        => Record(writer, new LedgerEntry(LedgerEntryKind.Created, KeyValueRecordLocation.Pack(pageId, slotIndex)));

    /// <summary>
    /// Records that <paramref name="writer"/> tombstoned the version at the
    /// given location (a delete, or the old version of a put-over-existing).
    /// </summary>
    internal void RecordTombstoned(TransactionSequence writer, PageId pageId, int slotIndex)
        => Record(writer, new LedgerEntry(LedgerEntryKind.Tombstoned, KeyValueRecordLocation.Pack(pageId, slotIndex)));

    /// <summary>
    /// Records that <paramref name="writer"/> inserted a primary-index entry — the
    /// index-side mirror of <see cref="RecordCreated"/>, so a logical rollback
    /// physically erases the aborted writer's entries from the index too.
    /// </summary>
    internal void RecordIndexEntryCreated(TransactionSequence writer, IIndex index, IndexKey key, ulong entryReference)
        => Record(writer, new LedgerEntry(LedgerEntryKind.IndexEntryCreated, entryReference, index, key.Encoded.ToArray()));

    /// <summary>
    /// Records that <paramref name="writer"/> tombstoned a primary-index entry —
    /// the index-side mirror of <see cref="RecordTombstoned"/>, so a logical
    /// rollback restores the entry's deleter stamp.
    /// </summary>
    internal void RecordIndexEntryTombstoned(TransactionSequence writer, IIndex index, IndexKey key, ulong entryReference)
        => Record(writer, new LedgerEntry(LedgerEntryKind.IndexEntryTombstoned, entryReference, index, key.Encoded.ToArray()));

    /// <summary>
    /// Completes a committed writer's ledger: created versions are permanent
    /// (nothing to track), tombstoned versions move to the prunable set — they
    /// are reclaimable once the oldest snapshot bound passes the writer.
    /// </summary>
    internal void OnCommitted(TransactionSequence writer)
    {
        lock (_sync)
        {
            if (!_ledger.Remove(writer.Value, out var entries))
            {
                return;
            }

            foreach (var entry in entries)
            {
                if (entry.Kind == LedgerEntryKind.Tombstoned)
                {
                    _prunable.Add(new PrunableVersion(writer.Value, entry.Location));
                }
            }
        }
    }

    /// <inheritdoc />
    public ValueTask AppendVersionAsync(ulong objectId, ulong entryId, ReadOnlyMemory<byte> payload, TransactionSequence writer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The record space holds the payload; the contract member records the
        // creation in the ledger (entryId is the packed location).
        Record(writer, new LedgerEntry(LedgerEntryKind.Created, entryId));
        return default;
    }

    /// <inheritdoc />
    public ValueTask<ReadOnlyMemory<byte>?> GetVisibleVersionAsync(ulong objectId, ulong entryId, TransactionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        var (pageId, slotIndex) = KeyValueRecordLocation.Unpack(entryId);

        ReadOnlyMemory<byte> record;
        try
        {
            record = _storage.ReadEntry(pageId, slotIndex);
        }
        catch (StorageException)
        {
            return new ValueTask<ReadOnlyMemory<byte>?>((ReadOnlyMemory<byte>?)null);
        }
        catch (ArgumentOutOfRangeException)
        {
            // The slot was reverted out of existence by a bracket rollback.
            return new ValueTask<ReadOnlyMemory<byte>?>((ReadOnlyMemory<byte>?)null);
        }

        if (record.Length < KeyValueRecordCodec.StampHeaderSize)
        {
            return new ValueTask<ReadOnlyMemory<byte>?>((ReadOnlyMemory<byte>?)null);
        }

        var (writer, deleter) = KeyValueRecordCodec.ReadStamps(record.Span);

        bool visible = snapshot.IsVisible(writer)
            && (deleter == TransactionSequence.None || !snapshot.IsVisible(deleter));

        return new ValueTask<ReadOnlyMemory<byte>?>(visible ? record : null);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Physically reclaims committed-tombstoned versions whose deleter is below
    /// <paramref name="oldestActive"/>: every live and future snapshot admits
    /// the deleter, so no one can see the version again. Each candidate is
    /// verified against its current stamps before removal.
    /// </remarks>
    public async ValueTask<long> PruneAsync(TransactionSequence oldestActive, CancellationToken cancellationToken = default)
    {
        List<PrunableVersion> candidates;

        lock (_sync)
        {
            candidates = _prunable.FindAll(version => version.Deleter < oldestActive.Value);
        }

        if (candidates.Count == 0)
        {
            return 0;
        }

        long pruned = 0;

        await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var bracket = _storage.BeginTransaction();

            foreach (var candidate in candidates)
            {
                var (pageId, slotIndex) = KeyValueRecordLocation.Unpack(candidate.Location);

                if (TryReadStamps(pageId, slotIndex, out _, out var deleter) && deleter.Value == candidate.Deleter)
                {
                    _storage.DeleteEntry(bracket, pageId, slotIndex);
                    pruned++;
                }
            }

            bracket.Commit();
        }
        finally
        {
            _applyGate.Release();
        }

        lock (_sync)
        {
            _prunable.RemoveAll(version => candidates.Contains(version));
        }

        return pruned;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The logical undo of an aborted writer: its created versions are deleted
    /// and its tombstones cleared, each verified against current stamps first.
    /// A failure leaves the writer queued for the version-purge worker to retry
    /// (an aborted writer's stamps must not serve snapshots, so retry is mandatory).
    /// </remarks>
    public async ValueTask<long> PurgeWriterAsync(TransactionSequence writer, CancellationToken cancellationToken = default)
    {
        List<LedgerEntry>? entries;

        lock (_sync)
        {
            _ledger.Remove(writer.Value, out entries);
            _prunable.RemoveAll(version => version.Deleter == writer.Value);
        }

        if (entries is null || entries.Count == 0)
        {
            lock (_sync)
            {
                _pendingAbortedPurges.Remove(writer.Value);
            }

            return 0;
        }

        try
        {
            long removed = await UndoAsync(writer, entries, cancellationToken).ConfigureAwait(false);

            lock (_sync)
            {
                _pendingAbortedPurges.Remove(writer.Value);
            }

            return removed;
        }
        catch
        {
            // Requeue for the purge worker; the entries go back so the retry has
            // its targets.
            lock (_sync)
            {
                if (_ledger.TryGetValue(writer.Value, out var existing))
                {
                    existing.AddRange(entries);
                }
                else
                {
                    _ledger[writer.Value] = entries;
                }

                _pendingAbortedPurges.Add(writer.Value);
            }

            throw;
        }
    }

    /// <summary>
    /// The open-time counterpart of <see cref="PurgeWriterAsync"/>, driven by
    /// recovery analysis: one pass over the record space physically deletes
    /// every version created by an unproven writer and clears every tombstone
    /// one stamped — the in-memory ledger died with the previous process, so the
    /// record space itself is the source of targets. Also seeds the prunable set
    /// with the committed tombstones the pass encounters, so pre-restart garbage
    /// is reclaimed by the purge worker. The caller scrubs the primary index
    /// separately (<c>IIndexManager.PurgeWritersAsync</c>).
    /// </summary>
    /// <param name="aborted">The sequences the journal cannot prove committed.</param>
    /// <returns>The number of versions physically undone.</returns>
    internal long ScrubRecovered(IReadOnlySet<TransactionSequence> aborted)
    {
        var deletions = new List<(PageId PageId, int SlotIndex)>();
        var tombstoneClears = new List<(PageId PageId, int SlotIndex, byte[] Restored)>();
        var prunable = new List<PrunableVersion>();

        using (var iterator = _storage.GetUnitIterator())
        {
            while (iterator.MoveNext())
            {
                var unit = iterator.Current;

                if (unit.Data.Length < KeyValueRecordCodec.StampHeaderSize)
                {
                    continue;
                }

                var (writer, deleter) = KeyValueRecordCodec.ReadStamps(unit.Data.Span);

                if (writer != TransactionSequence.None && aborted.Contains(writer))
                {
                    deletions.Add((unit.PageId, unit.SlotIndex));
                    continue;
                }

                if (deleter != TransactionSequence.None)
                {
                    if (aborted.Contains(deleter))
                    {
                        tombstoneClears.Add((unit.PageId, unit.SlotIndex, KeyValueRecordCodec.WithoutDeleter(unit.Data.Span)));
                    }
                    else
                    {
                        // A committed tombstone from before the restart: eligible
                        // for pruning once the bound passes its deleter.
                        prunable.Add(new PrunableVersion(deleter.Value, KeyValueRecordLocation.Pack(unit.PageId, unit.SlotIndex)));
                    }
                }
            }
        }

        if (deletions.Count > 0 || tombstoneClears.Count > 0)
        {
            using var bracket = _storage.BeginTransaction();

            foreach (var (pageId, slotIndex) in deletions)
            {
                _storage.DeleteEntry(bracket, pageId, slotIndex);
            }

            foreach (var (pageId, slotIndex, restored) in tombstoneClears)
            {
                _storage.UpdateEntry(bracket, pageId, slotIndex, restored);
            }

            bracket.Commit();
        }

        lock (_sync)
        {
            _prunable.AddRange(prunable);
        }

        return deletions.Count + tombstoneClears.Count;
    }

    private async ValueTask<long> UndoAsync(TransactionSequence writer, List<LedgerEntry> entries, CancellationToken cancellationToken)
    {
        long removed = 0;

        await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var bracket = _storage.BeginTransaction();

            foreach (var entry in entries)
            {
                var (pageId, slotIndex) = KeyValueRecordLocation.Unpack(entry.Location);

                switch (entry.Kind)
                {
                    case LedgerEntryKind.Created:
                        if (TryReadStamps(pageId, slotIndex, out var createdWriter, out _) && createdWriter == writer)
                        {
                            _storage.DeleteEntry(bracket, pageId, slotIndex);
                            removed++;
                        }

                        break;

                    case LedgerEntryKind.Tombstoned:
                        if (TryReadStamps(pageId, slotIndex, out _, out var deleter) && deleter == writer)
                        {
                            var record = _storage.ReadEntry(pageId, slotIndex);
                            _storage.UpdateEntry(bracket, pageId, slotIndex, KeyValueRecordCodec.WithoutDeleter(record.Span));
                            removed++;
                        }

                        break;

                    case LedgerEntryKind.IndexEntryCreated:
                        // Physical erase of the aborted insert's entry: both index
                        // ops verify the recorded stamp before acting, so a stale
                        // ledger entry is a no-op, never a misdelete.
                        await entry.Index!.EraseAsync(bracket, new IndexKey(entry.Key), entry.Location, writer, cancellationToken).ConfigureAwait(false);
                        removed++;
                        break;

                    case LedgerEntryKind.IndexEntryTombstoned:
                        await entry.Index!.ClearDeleterAsync(bracket, new IndexKey(entry.Key), entry.Location, writer, cancellationToken).ConfigureAwait(false);
                        removed++;
                        break;
                }
            }

            // Durability rides the transaction's abort record (or any later
            // durable record): a crash before that re-runs the same undo from
            // recovery analysis.
            bracket.Commit(awaitDurability: false);
        }
        finally
        {
            _applyGate.Release();
        }

        return removed;
    }

    private void Record(TransactionSequence writer, LedgerEntry entry)
    {
        lock (_sync)
        {
            if (!_ledger.TryGetValue(writer.Value, out var entries))
            {
                entries = new List<LedgerEntry>();
                _ledger[writer.Value] = entries;
            }

            entries.Add(entry);
        }
    }

    private bool TryReadStamps(PageId pageId, int slotIndex, out TransactionSequence writer, out TransactionSequence deleter)
    {
        writer = default;
        deleter = default;

        ReadOnlyMemory<byte> record;
        try
        {
            record = _storage.ReadEntry(pageId, slotIndex);
        }
        catch (StorageException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            // The slot no longer exists: a failed statement's bracket rollback
            // restored the page's before-image, reverting the very insert this
            // ledger entry recorded. Nothing to undo.
            return false;
        }

        if (record.Length < KeyValueRecordCodec.StampHeaderSize)
        {
            return false;
        }

        (writer, deleter) = KeyValueRecordCodec.ReadStamps(record.Span);
        return true;
    }

    private enum LedgerEntryKind : byte
    {
        Created = 0,
        Tombstoned,
        IndexEntryCreated,
        IndexEntryTombstoned,
    }

    private readonly record struct LedgerEntry(
        LedgerEntryKind Kind,
        ulong Location,
        IIndex? Index = null,
        byte[]? Key = null);

    private readonly record struct PrunableVersion(ulong Deleter, ulong Location);
}
