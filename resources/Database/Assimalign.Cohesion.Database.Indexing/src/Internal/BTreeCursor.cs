using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Indexing;

/// <summary>
/// Cursor over the entries a scan materialized under the tree's read latch: the
/// snapshot-visible members of the range at open time, in scan order.
/// </summary>
internal sealed class BTreeCursor : IIndexCursor
{
    private readonly List<(byte[] Key, ulong EntryReference)> _entries;
    private int _position = -1;

    internal BTreeCursor(List<(byte[] Key, ulong EntryReference)> entries)
    {
        _entries = entries;
    }

    /// <inheritdoc />
    public IndexKey CurrentKey
    {
        get
        {
            EnsurePositioned();
            return new IndexKey(_entries[_position].Key);
        }
    }

    /// <inheritdoc />
    public ulong CurrentEntryReference
    {
        get
        {
            EnsurePositioned();
            return _entries[_position].EntryReference;
        }
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public ValueTask DisposeAsync() => default;

    private void EnsurePositioned()
    {
        if (_position < 0 || _position >= _entries.Count)
        {
            throw new InvalidOperationException("The cursor is not positioned on an entry.");
        }
    }
}
