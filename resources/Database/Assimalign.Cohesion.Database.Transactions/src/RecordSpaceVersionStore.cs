using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Transactions;

using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Tracks physical record and index effects over a record space whose versions
/// carry the shared 16-byte writer/deleter stamp prefix. Payloads remain in the
/// record space; the ledger supplies logical undo and safe space reclamation.
/// </summary>
public sealed class RecordSpaceVersionStore : IVersionStore
{
    private readonly IStorage _storage;
    private readonly ITransactionRecordSpace _records;
    private readonly SemaphoreSlim _applyGate;
    private readonly Dictionary<ulong, List<LedgerEntry>> _ledger = new();
    private readonly List<PrunableVersion> _prunable = new();
    private readonly HashSet<ulong> _pendingAbortedPurges = new();
    private readonly object _sync = new();

    internal RecordSpaceVersionStore(IStorage storage, ITransactionRecordSpace records, SemaphoreSlim applyGate)
    {
        _storage = storage;
        _records = records;
        _applyGate = applyGate;
    }

    /// <summary>
    /// Gets the number of in-flight ledger entries plus retained prunable
    /// versions (test observability: "version-store size").
    /// </summary>
    public int TrackedVersionCount
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
    public IReadOnlyCollection<ulong> PendingAbortedPurges
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
    /// location (an insert or an update's new version). Called inside the
    /// statement's apply bracket.
    /// </summary>
    /// <param name="writer">The transaction creating the version.</param>
    /// <param name="pageId">The page containing the version.</param>
    /// <param name="slotIndex">The version's slot within the page.</param>
    public void RecordCreated(TransactionSequence writer, PageId pageId, int slotIndex)
        => Record(writer, new LedgerEntry(LedgerEntryKind.Created, _records.PackLocation(pageId, slotIndex)));

    /// <summary>
    /// Records that <paramref name="writer"/> tombstoned the version at the
    /// given location (a delete, or the old version of an update).
    /// </summary>
    /// <param name="writer">The transaction tombstoning the version.</param>
    /// <param name="pageId">The page containing the version.</param>
    /// <param name="slotIndex">The version's slot within the page.</param>
    public void RecordTombstoned(TransactionSequence writer, PageId pageId, int slotIndex)
        => Record(writer, new LedgerEntry(LedgerEntryKind.Tombstoned, _records.PackLocation(pageId, slotIndex)));

    /// <summary>
    /// Records that <paramref name="writer"/> inserted an index entry — the
    /// index-side mirror of <see cref="RecordCreated"/>, so a logical rollback
    /// physically erases the aborted writer's entries from the index too.
    /// </summary>
    /// <param name="writer">The transaction creating the index entry.</param>
    /// <param name="index">The index's stamp-verified undo adapter.</param>
    /// <param name="key">The encoded key; a private copy is retained for undo.</param>
    /// <param name="entryReference">The index entry's record reference.</param>
    public void RecordIndexEntryCreated(TransactionSequence writer, IRecordVersionIndex index, ReadOnlyMemory<byte> key, ulong entryReference)
        => Record(writer, new LedgerEntry(LedgerEntryKind.IndexEntryCreated, entryReference, index, key.ToArray()));

    /// <summary>
    /// Records that <paramref name="writer"/> tombstoned an index entry —
    /// the index-side mirror of <see cref="RecordTombstoned"/>, so a logical
    /// rollback restores the entry's deleter stamp.
    /// </summary>
    /// <param name="writer">The transaction tombstoning the index entry.</param>
    /// <param name="index">The index's stamp-verified undo adapter.</param>
    /// <param name="key">The encoded key; a private copy is retained for undo.</param>
    /// <param name="entryReference">The index entry's record reference.</param>
    public void RecordIndexEntryTombstoned(TransactionSequence writer, IRecordVersionIndex index, ReadOnlyMemory<byte> key, ulong entryReference)
        => Record(writer, new LedgerEntry(LedgerEntryKind.IndexEntryTombstoned, entryReference, index, key.ToArray()));

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

        var (pageId, slotIndex) = _records.UnpackLocation(entryId);

        ReadOnlyMemory<byte> record;
        try
        {
            record = _records.Read(pageId, slotIndex);
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

        if (record.Length < RecordVersionStamp.HeaderSize)
        {
            return new ValueTask<ReadOnlyMemory<byte>?>((ReadOnlyMemory<byte>?)null);
        }

        var (writer, deleter) = RecordVersionStamp.ReadStamps(record.Span);

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
                var (pageId, slotIndex) = _records.UnpackLocation(candidate.Location);

                if (TryReadStamps(pageId, slotIndex, out _, out var deleter) && deleter.Value == candidate.Deleter)
                {
                    _records.Delete(bracket, pageId, slotIndex);
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
    /// is reclaimed by the purge worker. The caller scrubs indexes separately through its model-specific index manager.
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

                if (unit.Data.Length < RecordVersionStamp.HeaderSize)
                {
                    continue;
                }

                var (writer, deleter) = RecordVersionStamp.ReadStamps(unit.Data.Span);

                if (writer != TransactionSequence.None && aborted.Contains(writer))
                {
                    deletions.Add((unit.PageId, unit.SlotIndex));
                    continue;
                }

                if (deleter != TransactionSequence.None)
                {
                    if (aborted.Contains(deleter))
                    {
                        tombstoneClears.Add((unit.PageId, unit.SlotIndex, RecordVersionStamp.WithoutDeleter(unit.Data.Span)));
                    }
                    else
                    {
                        // A committed tombstone from before the restart: eligible
                        // for pruning once the bound passes its deleter.
                        prunable.Add(new PrunableVersion(deleter.Value, _records.PackLocation(unit.PageId, unit.SlotIndex)));
                    }
                }
            }
        }

        if (deletions.Count > 0 || tombstoneClears.Count > 0)
        {
            using var bracket = _storage.BeginTransaction();

            foreach (var (pageId, slotIndex) in deletions)
            {
                _records.Delete(bracket, pageId, slotIndex);
            }

            foreach (var (pageId, slotIndex, restored) in tombstoneClears)
            {
                _records.Update(bracket, pageId, slotIndex, restored);
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
                var (pageId, slotIndex) = _records.UnpackLocation(entry.Location);

                switch (entry.Kind)
                {
                    case LedgerEntryKind.Created:
                        if (TryReadStamps(pageId, slotIndex, out var createdWriter, out _) && createdWriter == writer)
                        {
                            _records.Delete(bracket, pageId, slotIndex);
                            removed++;
                        }

                        break;

                    case LedgerEntryKind.Tombstoned:
                        if (TryReadStamps(pageId, slotIndex, out _, out var deleter) && deleter == writer)
                        {
                            var record = _records.Read(pageId, slotIndex);
                            _records.Update(bracket, pageId, slotIndex, RecordVersionStamp.WithoutDeleter(record.Span));
                            removed++;
                        }

                        break;

                    case LedgerEntryKind.IndexEntryCreated:
                        // Physical erase of the aborted insert's entry: both index
                        // ops verify the recorded stamp before acting, so a stale
                        // ledger entry is a no-op, never a misdelete.
                        await entry.Index!.EraseAsync(bracket, entry.Key, entry.Location, writer, cancellationToken).ConfigureAwait(false);
                        removed++;
                        break;

                    case LedgerEntryKind.IndexEntryTombstoned:
                        await entry.Index!.ClearDeleterAsync(bracket, entry.Key, entry.Location, writer, cancellationToken).ConfigureAwait(false);
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
            record = _records.Read(pageId, slotIndex);
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

        if (record.Length < RecordVersionStamp.HeaderSize)
        {
            return false;
        }

        (writer, deleter) = RecordVersionStamp.ReadStamps(record.Span);
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
        IRecordVersionIndex? Index = null,
        byte[]? Key = null);

    private readonly record struct PrunableVersion(ulong Deleter, ulong Location);
}
