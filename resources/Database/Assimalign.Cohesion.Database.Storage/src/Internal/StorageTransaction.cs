using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Storage.Internal;

/// <summary>
/// Internal storage transaction scope. Tracks the before image of every page the
/// transaction touches so rollback can restore in-memory state and recovery can
/// undo stolen writes.
/// </summary>
internal sealed class StorageTransaction : IStorageTransaction
{
    private readonly Storage _owner;
    private readonly Dictionary<long, byte[]> _beforeImages = new();
    private Dictionary<long, ulong>? _pendingFrees;
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
    /// Gets the pages this transaction has modified, keyed by page id, with the
    /// full page image captured before the first modification.
    /// </summary>
    internal IReadOnlyDictionary<long, byte[]> BeforeImages => _beforeImages;

    /// <summary>
    /// Returns true when the transaction has already captured the page's before image.
    /// </summary>
    internal bool HasTouched(long pageId) => _beforeImages.ContainsKey(pageId);

    /// <summary>
    /// Records the before image of a page on first touch.
    /// </summary>
    internal void RecordBeforeImage(long pageId, byte[] image) => _beforeImages.Add(pageId, image);

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
