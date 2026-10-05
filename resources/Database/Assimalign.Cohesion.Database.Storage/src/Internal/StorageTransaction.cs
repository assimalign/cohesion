using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Storage.Internal;

/// <summary>
/// Internal storage transaction scope. Holds the pre-image of every page the transaction
/// touches: rollback restores it in memory, and commit encodes each page's changes as the byte
/// runs that differ from it (#1253).
/// </summary>
/// <remarks>
/// <para>
/// A pre-image is kept as its full page image encoding (<see cref="PageImageCodec"/>): the
/// page's non-zero byte runs. A page the transaction allocated, or one with a large free gap,
/// costs a few hundred bytes instead of 8 KiB, which is what keeps an index build in one
/// bracket small. Past the storage's pre-image budget a page's pre-image is not kept at all:
/// the storage journals it as a full page image and the transaction keeps only where that
/// record lies (<see cref="StorageJournalLocation"/>), reading it back for its commit or its
/// rollback.
/// </para>
/// </remarks>
internal sealed class StorageTransaction : IStorageTransaction
{
    /// <summary>
    /// The bytes a kept pre-image costs beyond its runs: the array header, the dictionary entry
    /// and the entry's value. An estimate for the budget, not a measurement.
    /// </summary>
    internal const int PreImageOverhead = 64;

    private readonly Storage _owner;
    private readonly Dictionary<long, StoragePreImage> _preImages = new();
    private Dictionary<long, ulong>? _pendingFrees;
    private long _preImageBytes;
    private int _spilledPreImages;
    private bool _active = true;

    internal StorageTransaction(Storage owner, long sequence)
    {
        _owner = owner;
        Sequence = sequence;
    }

    /// <inheritdoc />
    public long Sequence { get; }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public void Commit() => Commit(awaitDurability: true);

    /// <inheritdoc />
    public void Commit(bool awaitDurability)
    {
        ThrowIfCompleted();
        _owner.CommitTransaction(this, awaitDurability);
    }

    /// <inheritdoc />
    public void Rollback()
    {
        ThrowIfCompleted();
        _owner.RollbackTransaction(this);
    }

    /// <inheritdoc />
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

