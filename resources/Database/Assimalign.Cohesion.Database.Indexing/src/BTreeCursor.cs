using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Indexing.Internal;

namespace Assimalign.Cohesion.Database.Indexing;

/// <summary>A cursor over snapshot-visible index entries, in the selected scan direction.</summary>
/// <remarks>
/// Reads bounded batches and retains owned keys, never pages or latches between calls.
/// The caller must keep the statement or transaction's snapshot pin alive until disposal.
/// Do not mutate through the snapshot owner while enumerating: stamps identify transactions,
/// not statements. Writers must collect their targets before applying changes.
/// Instances support one reader at a time; concurrent moves and disposal are unsupported.
/// </remarks>
public sealed class BTreeCursor : IAsyncDisposable
{
    internal const int BatchSize = 64;
    internal const int ExaminationLimit = 256;

    private BTreeIndex? _index;
    private readonly CancellationToken _openingToken;
    private int _position = -1;
    private bool _disposed;

    internal BTreeCursor(BTreeIndex index, TransactionSnapshot snapshot, IndexKeyRange range, bool reverse, CancellationToken cancellationToken)
    {
        _index = index;
        Snapshot = snapshot;
        // IndexKey accepts borrowed memory. Bounds must stay fixed for the cursor's lifetime.
        Range = new IndexKeyRange(
            range.Start is { } start ? new IndexKey(start.Encoded.ToArray()) : null,
            range.End is { } end ? new IndexKey(end.Encoded.ToArray()) : null,
            range.IsStartInclusive, range.IsEndInclusive);
        Reverse = reverse;
        _openingToken = cancellationToken;
    }

    internal TransactionSnapshot Snapshot { get; private set; }
    internal IndexKeyRange Range { get; private set; }
    internal bool Reverse { get; }
    internal Entry[] Entries { get; private set; } = new Entry[BatchSize];
    internal Entry? Continuation { get; set; }
    internal BTreeEntryOrder.Separator? Boundary { get; set; }
    internal int Count { get; set; }
    internal bool Completed { get; set; }
    // Per-cursor work counts, visible only to this assembly's tests; no process-global hot counter.
    internal long PagesVisited { get; set; }
    internal long EntriesExamined { get; set; }
    internal Action? BatchRead { get; set; }
    // The first result is commonly a probe (notably Graph type discovery). Grow only
    // after it is consumed, without exposing a batching policy across assemblies.
    internal int BatchSizeOverride { get; set; }
    internal int ReadLimit { get; private set; } = 1;

    internal readonly record struct Entry(byte[] Key, ulong EntryReference, ulong Writer);

    /// <summary>Gets the key at the current position.</summary>
    /// <exception cref="InvalidOperationException">The cursor is not positioned on an entry.</exception>
    /// <exception cref="ObjectDisposedException">The cursor is disposed.</exception>
    public IndexKey CurrentKey
    {
        get { EnsurePositioned(); return new IndexKey(Entries[_position].Key); }
    }

    /// <summary>Gets the entry reference at the current position.</summary>
    /// <exception cref="InvalidOperationException">The cursor is not positioned on an entry.</exception>
    /// <exception cref="ObjectDisposedException">The cursor is disposed.</exception>
    public ulong CurrentEntryReference
    {
        get { EnsurePositioned(); return Entries[_position].EntryReference; }
    }

    /// <summary>Advances to the next visible entry, reading another batch when needed.</summary>
    /// <param name="cancellationToken">Cancellation token for this move, in addition to the opening token.</param>
    /// <returns>True when positioned on an entry; false after exhaustion.</returns>
    /// <exception cref="ObjectDisposedException">The cursor is disposed.</exception>
    /// <exception cref="OperationCanceledException">Either token is canceled. The cursor is disposed.</exception>
    /// <exception cref="IndexCorruptionException">An index page is damaged. The cursor is disposed.</exception>
    /// <remarks>A failed move releases its batch and disposes the cursor. Storage errors propagate.</remarks>
    public ValueTask<bool> MoveNextAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            _openingToken.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            if (_position + 1 < Count)
            {
                _position++;
                return new ValueTask<bool>(true);
            }

            Array.Clear(Entries);
            Count = 0;
            _position = -1;
            using var linked = _openingToken.CanBeCanceled && cancellationToken.CanBeCanceled && _openingToken != cancellationToken
                ? CancellationTokenSource.CreateLinkedTokenSource(_openingToken, cancellationToken) : null;
            var token = linked?.Token ?? (_openingToken.CanBeCanceled ? _openingToken : cancellationToken);
            while (!Completed && Count == 0)
            {
                _index!.ReadCursorBatch(this, token);
                if (Count > 0) { ReadLimit = BatchSize; }
                BatchRead?.Invoke(); // Test seam, after every page pin and the tree latch have left.
            }
            if (Count == 0) { Continuation = null; return new ValueTask<bool>(false); }
            _position = 0;
            return new ValueTask<bool>(true);
        }
        catch
        {
            // Cleanup for every failure, including cancellation and storage exceptions.
            Release();
            throw;
        }
    }

    /// <summary>Releases the owned batch and snapshot reference. Repeated disposal is harmless.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        Release();
        return default;
    }

    private void Release()
    {
        Entries = [];
        Continuation = null;
        Boundary = null;
        Snapshot = null!;
        Range = default;
        _index = null;
        BatchRead = null;
        _position = -1;
        Count = 0;
        _disposed = true;
    }

    private void EnsurePositioned()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_position < 0 || _position >= Count) { throw new InvalidOperationException(ErrorMessages.CursorNotPositioned); }
    }
}
