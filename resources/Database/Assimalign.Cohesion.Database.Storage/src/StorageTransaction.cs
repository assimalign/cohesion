using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Storage;

using Assimalign.Cohesion.Database.Storage.Internal;

/// <summary>
/// A storage-level transaction scope: the unit of atomicity and durability for
/// record mutations against a storage file.
/// </summary>
/// <remarks>
/// <para>
/// Mutations made through a transaction are staged in the buffer pool and protected
/// by the write-ahead log: the first modification of a page since the last checkpoint
/// journals its full image, and <see cref="Commit()"/> journals the bytes each page changed
/// (a page delta) followed by a commit record (storage format 3, #1253). Durable storage
/// modes flush that record before the call returns; non-durable mode makes no persistence
/// promise. Data pages are <i>not</i> forced to disk at commit — recovery replays committed
/// changes from the journal (no-force), and uncommitted changes that reached disk early are
/// overwritten by the page's full image and the committed changes after it (steal).
/// </para>
/// <para>
/// Pages modified by an active transaction are write-locked to that transaction
/// until it completes; a second transaction touching the same page fails rather
/// than waits. Fine-grained (record-level) concurrency control is the transaction
/// layer's responsibility (<c>Database.Transactions</c>), built above this scope.
/// </para>
/// <para>
/// The transaction holds the pre-image of every page it touches: rollback restores it in
/// memory, and commit encodes each page's changes as the byte runs that differ from it. A
/// pre-image is kept as its full page image encoding, the page's non-zero byte runs, so a page
/// the transaction allocated, or one with a large free gap, costs a few hundred bytes instead
/// of 8 KiB, which is what keeps an index build in one bracket small. Past the storage's
/// pre-image budget a page's pre-image is not kept at all: the storage journals it as a full
/// page image and the transaction keeps only where that record lies, reading it back for its
/// commit or its rollback.
/// </para>
/// <para>
/// A rollback restores pages in place, and no reader sees a page half restored (#1371). A record
/// read holds only a pin and confirms its read against the page's restore, which copies the
/// restored page over the live one in one pass. A structure whose readers read its pages without a
/// page write lock (a B-tree) changes them through the overloads that take its
/// <see cref="StoragePageLatch"/>, and the rollback restores those pages, after every other page,
/// while it holds each such latch exclusively, so those readers see the structure whole.
/// </para>
/// <para>
/// Disposing an active transaction rolls it back. The type is sealed and created only by its
/// <see cref="Storage"/> (<see cref="Storage.BeginTransaction()"/>): every record and page
/// operation of a statement calls through it.
/// </para>
/// </remarks>
public sealed class StorageTransaction : IDisposable
{
    /// <summary>
    /// The bytes a kept pre-image costs beyond its runs: the array header, the dictionary entry
    /// and the entry's value. An estimate for the budget, not a measurement.
    /// </summary>
    internal const int PreImageOverhead = 64;

    private readonly Storage _owner;
    private readonly Dictionary<long, StoragePreImage> _preImages = new();
    private List<StoragePageLatch>? _latches;
    private HashSet<long>? _latchedPages;
    private Dictionary<long, ulong>? _pendingFrees;
    private long _preImageBytes;
    private int _spilledPreImages;
    private bool _active = true;

    internal StorageTransaction(Storage owner, long sequence)
    {
        _owner = owner;
        Sequence = sequence;
    }

    /// <summary>
    /// Gets the storage-level transaction sequence. Sequences are monotonic within
    /// a storage instance and identify the transaction in the journal.
    /// </summary>
    public long Sequence { get; }

    /// <summary>
    /// Gets a value indicating whether the transaction is still active (neither
    /// committed nor rolled back).
    /// </summary>
    public bool IsActive => _active;

    /// <summary>
    /// Gets the pages this transaction has touched, each with the pre-image captured before its
    /// first change.
    /// </summary>
    internal IReadOnlyDictionary<long, StoragePreImage> PreImages => _preImages;

    /// <summary>
    /// Gets the bytes the pre-images this transaction keeps in memory cost
    /// (<see cref="PreImageOverhead"/> each, plus their runs).
    /// </summary>
    internal long PreImageBytes => _preImageBytes;

    /// <summary>
    /// Gets the number of pre-images this transaction spilled to the journal.
    /// </summary>
    internal int SpilledPreImages => _spilledPreImages;

    /// <summary>
    /// Gets or sets the LSN of the transaction's commit record once it is appended; zero before.
    /// </summary>
    internal long CommitRecordLsn { get; set; }

    /// <summary>
    /// Returns true when the transaction has already captured the page's pre-image.
    /// </summary>
    internal bool HasTouched(long pageId) => _preImages.ContainsKey(pageId);

    /// <summary>
    /// Records the pre-image of a page on first touch, kept in memory as its encoded runs.
    /// </summary>
    /// <param name="pageId">The page.</param>
    /// <param name="runs">The page's full image encoding (<see cref="PageImageCodec.EncodeImage"/>).</param>
    /// <param name="baseLsn">The LSN of the page's last record once touched (<see cref="StoragePreImage.BaseLsn"/>).</param>
    /// <returns>The bytes the pre-image costs.</returns>
    internal long RecordPreImage(long pageId, byte[] runs, long baseLsn)
    {
        _preImages.Add(pageId, new StoragePreImage(runs, default, baseLsn));
        long cost = runs.Length + PreImageOverhead;
        _preImageBytes += cost;
        return cost;
    }

    /// <summary>
    /// Records that a page's pre-image was spilled: journaled as a full page image whose frame
    /// lies at <paramref name="location"/>, which is also the page's base LSN.
    /// </summary>
    internal void RecordSpilledPreImage(long pageId, StorageJournalLocation location)
    {
        _preImages.Add(pageId, new StoragePreImage(null, location, location.Lsn));
        _spilledPreImages++;
    }

    /// <summary>
    /// Gets the latches of the latched structures whose pages this transaction changed
    /// (<see cref="StoragePageLatch"/>), each once, in the order they were enlisted.
    /// </summary>
    internal IReadOnlyList<StoragePageLatch> Latches => (IReadOnlyList<StoragePageLatch>?)_latches ?? [];

    /// <summary>
    /// Enlists the latch of a structure whose page the transaction is about to change; a latch
    /// already enlisted is kept once. A transaction changes the pages of a few structures (a
    /// statement's indexes), so the lookup is a scan.
    /// </summary>
    internal void EnlistLatch(StoragePageLatch latch)
    {
        var latches = _latches ??= new List<StoragePageLatch>(2);

        foreach (var enlisted in latches)
        {
            if (ReferenceEquals(enlisted, latch))
            {
                return;
            }
        }

        latches.Add(latch);
    }

    /// <summary>
    /// Records that a page belongs to a latched structure: it was changed through an overload that
    /// takes the structure's latch, so the rollback restores it while it holds the enlisted latches,
    /// and every other page before it takes them.
    /// </summary>
    internal void MarkLatchedPage(long pageId) => (_latchedPages ??= new HashSet<long>()).Add(pageId);

    /// <summary>
    /// Returns true when the page was changed through an overload that takes a latch
    /// (<see cref="MarkLatchedPage"/>).
    /// </summary>
    internal bool IsLatchedPage(long pageId) => _latchedPages is { } pages && pages.Contains(pageId);

    /// <summary>
    /// Takes every enlisted latch exclusively for a rollback's restore, in the order the latches
    /// were created (<see cref="StoragePageLatch.Order"/>), so two rollbacks never hold one latch
    /// each while waiting for the other's. A latch the current thread already holds exclusively
    /// is left as it is: that hold already excludes the structure's readers.
    /// </summary>
    /// <returns>The latches this call took, for <see cref="ExitLatches"/>; null when it took none.</returns>
    internal List<StoragePageLatch>? EnterLatchesForRestore()
    {
        if (_latches is not { Count: > 0 } latches)
        {
            return null;
        }

        var ordered = latches.ToArray();
        Array.Sort(ordered, static (left, right) => left.Order.CompareTo(right.Order));
        var entered = new List<StoragePageLatch>(ordered.Length);

        try
        {
            foreach (var latch in ordered)
            {
                if (latch.IsWriteHeld)
                {
                    continue;
                }

                latch.EnterWrite();
                entered.Add(latch);
            }
        }
        catch
        {
            ExitLatches(entered);
            throw;
        }

        return entered;
    }

    /// <summary>
    /// Releases the latches <see cref="EnterLatchesForRestore"/> took, in the reverse order.
    /// </summary>
    internal static void ExitLatches(List<StoragePageLatch>? latches)
    {
        if (latches is null)
        {
            return;
        }

        for (int index = latches.Count - 1; index >= 0; index--)
        {
            latches[index].ExitWrite();
        }
    }

    /// <summary>
    /// Gets the pages this transaction has released, keyed by page id, with the owner
    /// chain each one belonged to. The free-space map and owner directory are updated
    /// from this set at commit — never before — so a rollback restores the chain untouched.
    /// </summary>
    internal IReadOnlyDictionary<long, ulong>? PendingFrees => _pendingFrees;

    /// <summary>
    /// Registers a page release to apply when the transaction commits. A page released
    /// again is registered once: deleting a page's last record and then releasing its
    /// whole chain in the same transaction releases it twice, and a second free at commit
    /// could put it back on the free list after another allocation took it, handing one
    /// page to two owners.
    /// </summary>
    internal void RegisterPendingFree(long pageId, ulong ownerId)
        => (_pendingFrees ??= new Dictionary<long, ulong>()).TryAdd(pageId, ownerId);

    /// <summary>
    /// Marks the transaction completed. The storage calls it in the step that releases the
    /// transaction's page write locks and active count, so a rollback whose record append
    /// then fails is complete already, and <see cref="Dispose"/> does not roll it back again.
    /// </summary>
    internal void MarkCompleted() => _active = false;

    /// <summary>
    /// Commits the transaction: journals the changed bytes of every modified page and a
    /// commit record, then applies the owning storage's durability policy. Durable
    /// modes return only after the journal is durable up to that commit record.
    /// </summary>
    /// <exception cref="StorageTransactionException">The transaction is not active.</exception>
    public void Commit() => Commit(awaitDurability: true);

    /// <summary>
    /// Commits the transaction, optionally without awaiting durability. A
    /// non-durable commit appends the same records (page deltas + commit
    /// record) but returns before they are flushed — for inner physical brackets
    /// whose durability is owned by an outer logical commit: the journal is
    /// ordered, so making any later record durable makes these durable first,
    /// and a crash before that leaves the bracket unproven (recovery redoes none
    /// of its changes), which is exactly the outer transaction's abort semantics.
    /// The write-ahead gate still protects stolen pages regardless.
    /// </summary>
    /// <param name="awaitDurability">False to skip the durable flush; true is equivalent to <see cref="Commit()"/>.</param>
    /// <exception cref="StorageTransactionException">The transaction is not active.</exception>
    public void Commit(bool awaitDurability)
    {
        ThrowIfCompleted();
        _owner.CommitTransaction(this, awaitDurability);
    }

    /// <summary>
    /// Rolls the transaction back: restores every modified page to its pre-image
    /// in the buffer pool and journals a rollback record.
    /// </summary>
    /// <remarks>
    /// The pages of latched structures (<see cref="StoragePageLatch"/>) are restored last, while
    /// the rollback holds each of their latches exclusively, so it waits for those structures'
    /// readers; every other page is restored first, with no latch held. A thread that holds one of
    /// those latches shared must release it before it rolls back.
    /// </remarks>
    /// <exception cref="StorageTransactionException">The transaction is not active.</exception>
    public void Rollback()
    {
        ThrowIfCompleted();
        _owner.RollbackTransaction(this);
    }

    /// <summary>
    /// Rolls the transaction back if it is still active; otherwise does nothing.
    /// </summary>
    public void Dispose()
    {
        if (_active)
        {
            Rollback();
        }
    }

    private void ThrowIfCompleted()
    {
        if (!_active)
        {
            throw new StorageTransactionException($"Storage transaction {Sequence} has already completed.");
        }
    }
}

