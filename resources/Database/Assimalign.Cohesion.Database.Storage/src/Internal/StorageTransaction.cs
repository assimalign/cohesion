using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Internal storage transaction scope. Tracks the before image of every page the
/// transaction touches so rollback can restore in-memory state and recovery can
/// undo stolen writes.
/// </summary>
internal sealed class StorageTransaction : IStorageTransaction
{
    private readonly Storage _owner;
    private readonly Dictionary<long, byte[]> _beforeImages = new();
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

    /// <inheritdoc />
    public void Commit()
    {
        ThrowIfCompleted();
        _owner.CommitTransaction(this);
        _active = false;
    }

    /// <inheritdoc />
    public void Rollback()
    {
        ThrowIfCompleted();
        _owner.RollbackTransaction(this);
        _active = false;
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
