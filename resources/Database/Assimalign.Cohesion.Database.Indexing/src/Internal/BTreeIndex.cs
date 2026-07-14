using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Indexing;

/// <summary>
/// The B+Tree index: sorted-directory nodes on <see cref="PageType.Index"/> pages,
/// MVCC-stamped leaf entries, and page mutations that ride the owning transaction's
/// write-ahead scope (a crash mid-split reverts to a consistent pre-transaction tree
/// through the storage before-images).
/// </summary>
/// <remarks>
/// Concurrency model (MVP): a tree-level reader/writer latch serializes structural
/// access — writers are exclusive, cursors materialize their results under the read
/// latch. Entry-level write conflicts between transactions are the lock manager's
/// job (unique keys) and the storage page write locks' job (everything else).
/// Deletes are tombstones (the deleter stamp); physical reclamation and node merges
/// belong to the vacuum feature that follows version pruning.
/// </remarks>
internal sealed class BTreeIndex : IIndex
{
    private readonly IStorage _storage;
    private readonly IStorageTransactionSource _transactionSource;
    private readonly ILockManager? _lockManager;
    private readonly ulong _objectId;
    private readonly ReaderWriterLockSlim _latch = new(LockRecursionPolicy.NoRecursion);
    private long _rootPageId;

    internal BTreeIndex(
        IStorage storage,
        IStorageTransactionSource transactionSource,
        ILockManager? lockManager,
        ulong objectId,
        IndexDefinition definition,
        long rootPageId)
    {
        _storage = storage;
        _transactionSource = transactionSource;
        _lockManager = lockManager;
        _objectId = objectId;
        Name = definition.Name;
        IsUnique = definition.IsUnique;
        _rootPageId = rootPageId;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public IndexKind Kind => IndexKind.BTree;

    /// <inheritdoc />
    public bool IsUnique { get; }

    /// <summary>
    /// Gets the current root page. Root splits change it; catalogs persist it.
    /// </summary>
    internal long RootPageId => Interlocked.Read(ref _rootPageId);

    /// <summary>
    /// Allocates the root leaf of a new tree inside the given storage transaction.
    /// </summary>
    internal static long CreateRoot(IStorage storage, IStorageTransaction transaction)
    {
        using var handle = storage.AllocatePageForWrite(transaction, PageType.Index);
        BTreeNode.Initialize(handle.Page.AsBodySpan(), BTreeNode.LeafKind);
        handle.MarkDirty();
        return (long)handle.Id;
    }

    /// <inheritdoc />
    public async ValueTask InsertAsync(ITransactionContext transaction, IndexKey key, ulong entryReference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        if (key.Length > BTreeNode.MaxKeyLength)
        {
            throw new IndexException($"Index key of {key.Length} bytes exceeds the {BTreeNode.MaxKeyLength}-byte maximum.");
        }

        // Unique keys arbitrate concurrent writers through the lock manager before
        // touching the tree: the winner proceeds, others block or deadlock-abort.
        // (IndexKey.Hash is the published lock identity, so writers that must not
        // wait here — statements inside an apply gate — pre-acquire the same
        // resource in their lock phase and re-enter for free.)
        if (IsUnique && _lockManager is not null)
        {
            await _lockManager.AcquireAsync(
                transaction.Sequence,
                LockResource.Entry(_objectId, key.Hash()),
                LockMode.Exclusive,
                cancellationToken).ConfigureAwait(false);
        }

        var storageTransaction = _transactionSource.GetStorageTransaction(transaction);

        _latch.EnterWriteLock();
        try
        {
            // Uniqueness is enforced against the LATEST state, not the begin
            // snapshot — snapshot-based checks would let two transactions that
            // began before each other's commit both insert (write skew). With the
            // key lock held, any live entry (deleter stamp zero) is either
            // committed or our own: uncommitted others are excluded by the lock,
            // and aborted writers' entries were physically reverted by rollback.
            if (IsUnique && HasLiveEntry(key.Encoded.Span))
            {
                throw new IndexUniqueViolationException(Name, key);
            }

            InsertCore(storageTransaction, key.Encoded.Span, entryReference, transaction.Sequence.Value, 0);
        }
        finally
        {
            _latch.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(ITransactionContext transaction, IndexKey key, ulong entryReference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        cancellationToken.ThrowIfCancellationRequested();

        // Deletes on unique indexes take the key lock too: an uncommitted delete
        // must not let a concurrent insert treat the key as free (the deleter's
        // rollback would revive the entry and break uniqueness).
        if (IsUnique && _lockManager is not null)
        {
            await _lockManager.AcquireAsync(
                transaction.Sequence,
                LockResource.Entry(_objectId, key.Hash()),
                LockMode.Exclusive,
                cancellationToken).ConfigureAwait(false);
        }

        var storageTransaction = _transactionSource.GetStorageTransaction(transaction);
        var snapshot = transaction.Snapshot;

        _latch.EnterWriteLock();
        try
        {
            long leafId = DescendToLeaf(key.Encoded.Span, null);

            while (leafId >= 0)
            {
                int index;
                int count;
                long nextLeaf;

                using (var handle = _storage.PageManager.GetPage((PageId)leafId))
                {
                    var node = new BTreeNode(handle.Page.AsBodySpan());
                    index = node.FindLowerBound(key.Encoded.Span);
                    count = node.EntryCount;
                    nextLeaf = node.NextLeaf;

                    for (; index < count; index++)
                    {
                        if (!node.GetKey(index).SequenceEqual(key.Encoded.Span))
                        {
                            return; // walked past the key: nothing to delete
                        }

                        if (node.GetEntryReference(index) != entryReference || node.GetDeleter(index) != 0)
                        {
                            continue;
                        }

                        if (!snapshot.IsVisible(new TransactionSequence(node.GetWriter(index))))
                        {
                            continue;
                        }

                        // Found the live, visible mapping — tombstone it under the
                        // transaction's write scope.
                        using var writable = _storage.OpenPageForWrite(storageTransaction, (PageId)leafId);
                        var writableNode = new BTreeNode(writable.Page.AsBodySpan());
                        writableNode.SetDeleter(index, transaction.Sequence.Value);
                        writable.MarkDirty();
                        return;
                    }
                }

                leafId = nextLeaf; // equal keys may continue on the next leaf
            }
        }
        finally
        {
            _latch.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public IIndexCursor OpenCursor(ITransactionContext transaction, IndexKeyRange range, bool reverse = false)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return OpenCursor(transaction.Snapshot, range, reverse);
    }

    /// <inheritdoc />
    public IIndexCursor OpenCursor(TransactionSnapshot snapshot, IndexKeyRange range, bool reverse = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var results = new List<(byte[] Key, ulong EntryReference)>();

        _latch.EnterReadLock();
        try
        {
            CollectVisible(snapshot, range, results);
        }
        finally
        {
            _latch.ExitReadLock();
        }

        if (reverse)
        {
            results.Reverse();
        }

        return new BTreeCursor(results);
    }

    /// <inheritdoc />
    public ValueTask InsertVersionAsync(IStorageTransaction transaction, IndexKey key, ulong entryReference, TransactionSequence writer, TransactionSequence deleter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        cancellationToken.ThrowIfCancellationRequested();

        if (key.Length > BTreeNode.MaxKeyLength)
        {
            throw new IndexException($"Index key of {key.Length} bytes exceeds the {BTreeNode.MaxKeyLength}-byte maximum.");
        }

        _latch.EnterWriteLock();
        try
        {
            InsertCore(transaction, key.Encoded.Span, entryReference, writer.Value, deleter.Value);
        }
        finally
        {
            _latch.ExitWriteLock();
        }

        return default;
    }

    /// <inheritdoc />
    public ValueTask EraseAsync(IStorageTransaction transaction, IndexKey key, ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        cancellationToken.ThrowIfCancellationRequested();

        _latch.EnterWriteLock();
        try
        {
            long leafId = DescendToLeaf(key.Encoded.Span, null);

            while (leafId >= 0)
            {
                long nextLeaf;

                using (var handle = _storage.PageManager.GetPage((PageId)leafId))
                {
                    var node = new BTreeNode(handle.Page.AsBodySpan());
                    nextLeaf = node.NextLeaf;

                    for (int index = node.FindLowerBound(key.Encoded.Span); index < node.EntryCount; index++)
                    {
                        if (!node.GetKey(index).SequenceEqual(key.Encoded.Span))
                        {
                            return default; // walked past the key: nothing to erase
                        }

                        if (node.GetEntryReference(index) != entryReference || node.GetWriter(index) != writer.Value)
                        {
                            continue;
                        }

                        using var writable = _storage.OpenPageForWrite(transaction, (PageId)leafId);
                        var writableNode = new BTreeNode(writable.Page.AsBodySpan());
                        writableNode.RemoveLeafEntry(index);
                        writable.MarkDirty();
                        return default;
                    }
                }

                leafId = nextLeaf; // equal keys may continue on the next leaf
            }
        }
        finally
        {
            _latch.ExitWriteLock();
        }

        return default;
    }

    /// <inheritdoc />
    public ValueTask ClearDeleterAsync(IStorageTransaction transaction, IndexKey key, ulong entryReference, TransactionSequence deleter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        cancellationToken.ThrowIfCancellationRequested();

        _latch.EnterWriteLock();
        try
        {
            long leafId = DescendToLeaf(key.Encoded.Span, null);

            while (leafId >= 0)
            {
                long nextLeaf;

                using (var handle = _storage.PageManager.GetPage((PageId)leafId))
                {
                    var node = new BTreeNode(handle.Page.AsBodySpan());
                    nextLeaf = node.NextLeaf;

                    for (int index = node.FindLowerBound(key.Encoded.Span); index < node.EntryCount; index++)
                    {
                        if (!node.GetKey(index).SequenceEqual(key.Encoded.Span))
                        {
                            return default; // walked past the key: nothing to restore
                        }

                        if (node.GetEntryReference(index) != entryReference || node.GetDeleter(index) != deleter.Value)
                        {
                            continue;
                        }

                        using var writable = _storage.OpenPageForWrite(transaction, (PageId)leafId);
                        var writableNode = new BTreeNode(writable.Page.AsBodySpan());
                        writableNode.SetDeleter(index, 0);
                        writable.MarkDirty();
                        return default;
                    }
                }

                leafId = nextLeaf;
            }
        }
        finally
        {
            _latch.ExitWriteLock();
        }

        return default;
    }

    /// <summary>
    /// Walks every leaf once, physically removing entries written by any of the
    /// given writers and clearing tombstones they stamped — the open-time
    /// aborted-writer purge (see <see cref="IIndexManager.PurgeWritersAsync"/>).
    /// </summary>
    internal long PurgeWriters(IStorageTransaction transaction, IReadOnlySet<TransactionSequence> writers)
    {
        long purged = 0;

        _latch.EnterWriteLock();
        try
        {
            long leafId = DescendToLeftmostLeaf();

            while (leafId >= 0)
            {
                long nextLeaf;

                using (var handle = _storage.PageManager.GetPage((PageId)leafId))
                {
                    var node = new BTreeNode(handle.Page.AsBodySpan());
                    nextLeaf = node.NextLeaf;

                    IStoragePageHandle? writable = null;
                    try
                    {
                        for (int index = 0; index < node.EntryCount;)
                        {
                            bool remove = writers.Contains(new TransactionSequence(node.GetWriter(index)));
                            ulong deleter = node.GetDeleter(index);
                            bool restore = !remove && deleter != 0 && writers.Contains(new TransactionSequence(deleter));

                            if (!remove && !restore)
                            {
                                index++;
                                continue;
                            }

                            if (writable is null)
                            {
                                writable = _storage.OpenPageForWrite(transaction, (PageId)leafId);
                                node = new BTreeNode(writable.Page.AsBodySpan());
                            }

                            if (remove)
                            {
                                node.RemoveLeafEntry(index); // do not advance: entries shifted left
                            }
                            else
                            {
                                node.SetDeleter(index, 0);
                                index++;
                            }

                            purged++;
                        }

                        writable?.MarkDirty();
                    }
                    finally
                    {
                        writable?.Dispose();
                    }
                }

                leafId = nextLeaf;
            }
        }
        finally
        {
            _latch.ExitWriteLock();
        }

        return purged;
    }

    private void CollectVisible(TransactionSnapshot snapshot, IndexKeyRange range, List<(byte[] Key, ulong EntryReference)> results)
    {
        ReadOnlySpan<byte> startKey = range.Start.HasValue ? range.Start.Value.Encoded.Span : default;
        long leafId = range.Start is null ? DescendToLeftmostLeaf() : DescendToLeaf(startKey, null);

        bool positioned = false;

        while (leafId >= 0)
        {
            long nextLeaf;

            using (var handle = _storage.PageManager.GetPage((PageId)leafId))
            {
                var node = new BTreeNode(handle.Page.AsBodySpan());
                nextLeaf = node.NextLeaf;
                int index = 0;

                if (!positioned && range.Start is not null)
                {
                    index = node.FindLowerBound(startKey);
                    positioned = true;
                }

                for (; index < node.EntryCount; index++)
                {
                    var key = node.GetKey(index);

                    if (range.Start is not null && !range.IsStartInclusive && key.SequenceEqual(range.Start.Value.Encoded.Span))
                    {
                        continue;
                    }

                    if (range.End is not null)
                    {
                        int comparison = key.SequenceCompareTo(range.End.Value.Encoded.Span);
                        if (comparison > 0 || (comparison == 0 && !range.IsEndInclusive))
                        {
                            return;
                        }
                    }

                    if (!IsEntryVisible(node, index, snapshot))
                    {
                        continue;
                    }

                    results.Add((key.ToArray(), node.GetEntryReference(index)));
                }
            }

            leafId = nextLeaf;
        }
    }

    private bool HasLiveEntry(ReadOnlySpan<byte> key)
    {
        long leafId = DescendToLeaf(key, null);

        while (leafId >= 0)
        {
            long nextLeaf;

            using (var handle = _storage.PageManager.GetPage((PageId)leafId))
            {
                var node = new BTreeNode(handle.Page.AsBodySpan());
                nextLeaf = node.NextLeaf;

                for (int index = node.FindLowerBound(key); index < node.EntryCount; index++)
                {
                    if (!node.GetKey(index).SequenceEqual(key))
                    {
                        return false;
                    }

                    if (node.GetDeleter(index) == 0)
                    {
                        return true;
                    }
                }
            }

            leafId = nextLeaf;
        }

        return false;
    }

    private static bool IsEntryVisible(in BTreeNode node, int index, TransactionSnapshot snapshot)
    {
        if (!snapshot.IsVisible(new TransactionSequence(node.GetWriter(index))))
        {
            return false;
        }

        ulong deleter = node.GetDeleter(index);
        return deleter == 0 || !snapshot.IsVisible(new TransactionSequence(deleter));
    }

    private void InsertCore(IStorageTransaction transaction, ReadOnlySpan<byte> key, ulong entryReference, ulong writer, ulong deleter)
    {
        while (true)
        {
            var path = new List<long>();
            long leafId = DescendToLeaf(key, path);

            using (var handle = _storage.PageManager.GetPage((PageId)leafId))
            {
                var node = new BTreeNode(handle.Page.AsBodySpan());

                if (node.LeafEntrySize(key.Length) <= node.FreeSpace)
                {
                    using var writable = _storage.OpenPageForWrite(transaction, (PageId)leafId);
                    var writableNode = new BTreeNode(writable.Page.AsBodySpan());
                    writableNode.InsertLeafEntry(writableNode.FindLowerBound(key), key, entryReference, writer, deleter);
                    writable.MarkDirty();
                    return;
                }
            }

            SplitLeaf(transaction, path, leafId);
            // Re-descend: the split changed the structure; the target leaf now has room.
        }
    }

    private long DescendToLeftmostLeaf()
    {
        long current = RootPageId;

        while (true)
        {
            using var handle = _storage.PageManager.GetPage((PageId)current);
            var node = new BTreeNode(handle.Page.AsBodySpan());

            if (node.IsLeaf)
            {
                return current;
            }

            current = node.LeftmostChild;
        }
    }

    private long DescendToLeaf(ReadOnlySpan<byte> key, List<long>? path)
    {
        long current = RootPageId;

        while (true)
        {
            using var handle = _storage.PageManager.GetPage((PageId)current);
            var node = new BTreeNode(handle.Page.AsBodySpan());

            if (node.IsLeaf)
            {
                return current;
            }

            path?.Add(current);
            current = node.FindChild(key);
        }
    }

    private void SplitLeaf(IStorageTransaction transaction, List<long> parentPath, long leafId)
    {
        byte[] separator;
        long siblingId;

        using (var leafHandle = _storage.OpenPageForWrite(transaction, (PageId)leafId))
        using (var siblingHandle = _storage.AllocatePageForWrite(transaction, PageType.Index))
        {
            var leaf = new BTreeNode(leafHandle.Page.AsBodySpan());
            var sibling = BTreeNode.Initialize(siblingHandle.Page.AsBodySpan(), BTreeNode.LeafKind);
            siblingId = (long)siblingHandle.Id;

            int count = leaf.EntryCount;
            int mid = count / 2;

            // Move the upper half to the sibling.
            for (int i = mid; i < count; i++)
            {
                sibling.InsertLeafEntry(
                    i - mid, leaf.GetKey(i), leaf.GetEntryReference(i), leaf.GetWriter(i), leaf.GetDeleter(i));
            }

            separator = sibling.GetKey(0).ToArray();

            // Fix the sibling chain.
            long oldNext = leaf.NextLeaf;
            sibling.NextLeaf = oldNext;
            sibling.PrevLeaf = leafId;

            if (oldNext >= 0)
            {
                using var oldNextHandle = _storage.OpenPageForWrite(transaction, (PageId)oldNext);
                var oldNextNode = new BTreeNode(oldNextHandle.Page.AsBodySpan());
                oldNextNode.PrevLeaf = siblingId;
                oldNextHandle.MarkDirty();
            }

            // Rebuild the source with the lower half (reclaims the moved bytes).
            RebuildLeaf(ref leaf, mid, leaf.PrevLeaf, siblingId);

            leafHandle.MarkDirty();
            siblingHandle.MarkDirty();
        }

        InsertIntoParent(transaction, parentPath, separator, siblingId);
    }

    private static void RebuildLeaf(ref BTreeNode leaf, int keepCount, long prevLeaf, long nextLeaf)
    {
        int count = keepCount;
        var keys = new byte[count][];
        var references = new ulong[count];
        var writers = new ulong[count];
        var deleters = new ulong[count];

        for (int i = 0; i < count; i++)
        {
            keys[i] = leaf.GetKey(i).ToArray();
            references[i] = leaf.GetEntryReference(i);
            writers[i] = leaf.GetWriter(i);
            deleters[i] = leaf.GetDeleter(i);
        }

        leaf = BTreeNode.Initialize(leaf.Body, BTreeNode.LeafKind);
        leaf.PrevLeaf = prevLeaf;
        leaf.NextLeaf = nextLeaf;

        for (int i = 0; i < count; i++)
        {
            leaf.InsertLeafEntry(i, keys[i], references[i], writers[i], deleters[i]);
        }
    }

    private void InsertIntoParent(IStorageTransaction transaction, List<long> parentPath, byte[] separator, long childId)
    {
        if (parentPath.Count == 0)
        {
            // Root split: a new internal root over the old root and the new child.
            using var rootHandle = _storage.AllocatePageForWrite(transaction, PageType.Index);
            var root = BTreeNode.Initialize(rootHandle.Page.AsBodySpan(), BTreeNode.InternalKind);
            root.LeftmostChild = RootPageId;
            root.InsertInternalEntry(0, separator, childId);
            rootHandle.MarkDirty();
            Interlocked.Exchange(ref _rootPageId, (long)rootHandle.Id);
            return;
        }

        long parentId = parentPath[^1];

        using (var parentHandle = _storage.OpenPageForWrite(transaction, (PageId)parentId))
        {
            var parent = new BTreeNode(parentHandle.Page.AsBodySpan());

            if (parent.InternalEntrySize(separator.Length) <= parent.FreeSpace)
            {
                parent.InsertInternalEntry(parent.FindLowerBound(separator), separator, childId);
                parentHandle.MarkDirty();
                return;
            }
        }

        // The parent is full: split it, then route the separator to the correct half.
        var (promoted, parentSiblingId) = SplitInternal(transaction, parentPath);

        long target = separator.AsSpan().SequenceCompareTo(promoted) < 0 ? parentId : parentSiblingId;

        using var targetHandle = _storage.OpenPageForWrite(transaction, (PageId)target);
        var targetNode = new BTreeNode(targetHandle.Page.AsBodySpan());
        targetNode.InsertInternalEntry(targetNode.FindLowerBound(separator), separator, childId);
        targetHandle.MarkDirty();
    }

    private (byte[] Promoted, long SiblingId) SplitInternal(IStorageTransaction transaction, List<long> path)
    {
        long nodeId = path[^1];
        byte[] promoted;
        long siblingId;

        using (var nodeHandle = _storage.OpenPageForWrite(transaction, (PageId)nodeId))
        using (var siblingHandle = _storage.AllocatePageForWrite(transaction, PageType.Index))
        {
            var node = new BTreeNode(nodeHandle.Page.AsBodySpan());
            var sibling = BTreeNode.Initialize(siblingHandle.Page.AsBodySpan(), BTreeNode.InternalKind);
            siblingId = (long)siblingHandle.Id;

            int count = node.EntryCount;
            int mid = count / 2;

            // The middle separator is promoted; its child becomes the sibling's leftmost.
            promoted = node.GetKey(mid).ToArray();
            sibling.LeftmostChild = node.GetChild(mid);

            for (int i = mid + 1; i < count; i++)
            {
                sibling.InsertInternalEntry(i - mid - 1, node.GetKey(i), node.GetChild(i));
            }

            RebuildInternal(ref node, mid);

            nodeHandle.MarkDirty();
            siblingHandle.MarkDirty();
        }

        InsertIntoParent(transaction, path.GetRange(0, path.Count - 1), promoted, siblingId);
        return (promoted, siblingId);
    }

    private static void RebuildInternal(ref BTreeNode node, int keepCount)
    {
        long leftmost = node.LeftmostChild;
        var keys = new byte[keepCount][];
        var children = new long[keepCount];

        for (int i = 0; i < keepCount; i++)
        {
            keys[i] = node.GetKey(i).ToArray();
            children[i] = node.GetChild(i);
        }

        node = BTreeNode.Initialize(node.Body, BTreeNode.InternalKind);
        node.LeftmostChild = leftmost;

        for (int i = 0; i < keepCount; i++)
        {
            node.InsertInternalEntry(i, keys[i], children[i]);
        }
    }

}
