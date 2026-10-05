using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Indexing;

/// <summary>
/// A forward-only cursor over the visible entries of an index scan.
/// </summary>
/// <remarks>
/// <see cref="BTreeIndex.OpenCursor(Transactions.TransactionSnapshot, IndexKeyRange, bool)"/>
/// materializes the snapshot-visible members of the range under the tree's read latch when
/// the cursor opens, so the cursor holds no page and no latch while it is read; disposing it
/// releases nothing today, and callers dispose it so that a cursor that pins pages later
/// needs no call-site change.
/// </remarks>
public sealed class BTreeCursor : IAsyncDisposable
{
    private readonly List<(byte[] Key, ulong EntryReference)> _entries;
    private int _position = -1;

    internal BTreeCursor(List<(byte[] Key, ulong EntryReference)> entries)
    {
        _entries = entries;
    }

    /// <summary>
    /// Gets the key at the current position.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown before the first <see cref="MoveNextAsync"/> or after the scan is exhausted.</exception>
    public IndexKey CurrentKey
    {
        get
        {
            EnsurePositioned();
            return new IndexKey(_entries[_position].Key);
        }
    }

    /// <summary>
    /// Gets the entry reference at the current position.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown before the first <see cref="MoveNextAsync"/> or after the scan is exhausted.</exception>
    public ulong CurrentEntryReference
    {
        get
        {
            EnsurePositioned();
            return _entries[_position].EntryReference;
        }
    }

    /// <summary>
    /// Advances the cursor to the next visible entry.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True when the cursor advanced; false when the scan is exhausted.</returns>
    public ValueTask<bool> MoveNextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_position + 1 >= _entries.Count)
        {
            _position = _entries.Count;
            return new ValueTask<bool>(false);
        }

        _position++;
        return new ValueTask<bool>(true);
    }

    /// <summary>
    /// Releases the cursor. It holds nothing today (see the remarks).
    /// </summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync() => default;

    private void EnsurePositioned()
    {
        if (_position < 0 || _position >= _entries.Count)
        {
            throw new InvalidOperationException("The cursor is not positioned on an entry.");
        }
    }
}
