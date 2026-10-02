using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Indexing.Internal;

/// <summary>
/// The B+Tree index: sorted-directory nodes on <see cref="PageType.Index"/> pages,
/// MVCC-stamped leaf entries, and page mutations that ride the owning transaction's
/// write-ahead scope (a crash mid-split reverts to a consistent pre-transaction tree
/// through the storage before-images).
/// </summary>
/// <remarks>
/// <para>
/// Concurrency model (MVP): a tree-level reader/writer latch serializes structural
/// access — writers are exclusive, cursors materialize their results under the read
/// latch. Entry-level write conflicts between transactions are the lock manager's
/// job (unique keys) and the storage page write locks' job (everything else).
/// Deletes are tombstones (the deleter stamp); physical reclamation and node merges
/// belong to the vacuum feature that follows version pruning.
/// </para>
/// <para>
/// Duplicate keys (#1159): keys are not unique — secondary indexes repeat a value per
/// row, and every MVCC version adds an entry — so a run of equal keys can split, and
/// a key equal to a separator can live on either side of it. Three rules keep that
/// sound. Lookups that must see every entry of a key (seeks, deletes, the undo
/// pair, the unique check) descend to the leftmost child that can hold it and walk
/// right along the leaf chain. A split attaches its new node directly after the node
/// that split, by position: equal separators cannot be told apart by value. And the
/// leaf chain order therefore always equals the tree's child order.
/// </para>
/// </remarks>
internal sealed class BTreeIndex : IIndex
{
    private readonly IStorage _storage;
    private readonly IStorageTransactionSource _transactionSource;
    private readonly ILockManager? _lockManager;
    private readonly ulong _objectId;
    private readonly ReaderWriterLockSlim _latch = new(LockRecursionPolicy.NoRecursion);
    private readonly long _rootPageId;

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
    /// Gets the root page. It never moves: a root split relocates the root's contents
    /// to a new child and rewrites the root page in place, so the id a catalog
    /// registered when the tree was created stays valid — through a rollback of the
    /// split (the root page reverts with its before-image) and through a crash
    /// between the split and the catalog's next persistence point.
    /// </summary>
    internal long RootPageId => _rootPageId;

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

        _latch.EnterWriteLock();
        try
        {
            // The live mapping this transaction can see — tombstone it under the
            // transaction's write scope. No match: nothing to delete.
            if (TryFindEntry(key.Encoded.Span, EntryMatch.LiveVisible(entryReference, transaction.Snapshot), out long leafId, out int index))
            {
                using var writable = _storage.OpenPageForWrite(storageTransaction, (PageId)leafId);
                var writableNode = new BTreeNode(writable.Page.AsBodySpan());
                writableNode.SetDeleter(index, transaction.Sequence.Value);
                writable.MarkDirty();
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
            if (TryFindEntry(key.Encoded.Span, EntryMatch.WrittenBy(entryReference, writer.Value), out long leafId, out int index))
            {
                using var writable = _storage.OpenPageForWrite(transaction, (PageId)leafId);
                var writableNode = new BTreeNode(writable.Page.AsBodySpan());
                writableNode.RemoveLeafEntry(index);
                writable.MarkDirty();
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
            if (TryFindEntry(key.Encoded.Span, EntryMatch.DeletedBy(entryReference, deleter.Value), out long leafId, out int index))
            {
                using var writable = _storage.OpenPageForWrite(transaction, (PageId)leafId);
                var writableNode = new BTreeNode(writable.Page.AsBodySpan());
                writableNode.SetDeleter(index, 0);
                writable.MarkDirty();
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

    /// <summary>
    /// Materializes the snapshot-visible entries of <paramref name="range"/> in key
    /// order. An inclusive start descends to the first leaf that can hold the start
    /// key (its duplicates may begin left of an equal separator); an exclusive start
    /// descends to the last such leaf, since nothing to its left is greater. Either
    /// way only the first leaf needs positioning: every later leaf on the chain holds
    /// keys at or past the start.
    /// </summary>
    private void CollectVisible(TransactionSnapshot snapshot, IndexKeyRange range, List<(byte[] Key, ulong EntryReference)> results)
    {
        ReadOnlySpan<byte> startKey = range.Start.HasValue ? range.Start.Value.Encoded.Span : default;
        ReadOnlySpan<byte> endKey = range.End.HasValue ? range.End.Value.Encoded.Span : default;

        long leafId = range.Start is null
            ? DescendToLeftmostLeaf()
            : range.IsStartInclusive ? DescendToFirstLeaf(startKey) : DescendToLastLeaf(startKey, null);

        bool firstLeaf = true;

        while (leafId >= 0)
        {
            long nextLeaf;

            using (var handle = _storage.PageManager.GetPage((PageId)leafId))
            {
                var node = new BTreeNode(handle.Page.AsBodySpan());
                nextLeaf = node.NextLeaf;
                int index = 0;

                if (firstLeaf && range.Start is not null)
                {
                    index = range.IsStartInclusive ? node.FindLowerBound(startKey) : node.FindUpperBound(startKey);
                }

                firstLeaf = false;

                for (; index < node.EntryCount; index++)
                {
                    var key = node.GetKey(index);

                    if (range.End is not null)
                    {
                        int comparison = key.SequenceCompareTo(endKey);
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

    /// <summary>
    /// Whether the key has a live entry (deleter stamp zero) in the LATEST state —
    /// the unique check, which runs under the key's exclusive lock.
    /// </summary>
    private bool HasLiveEntry(ReadOnlySpan<byte> key) => TryFindEntry(key, EntryMatch.Live, out _, out _);

    /// <summary>
    /// Finds the first entry with exactly <paramref name="key"/> that
    /// <paramref name="match"/> accepts. The walk starts at the first leaf that can
    /// hold the key and follows the leaf chain until it passes the key, so it sees
    /// every one of the key's entries however many leaves the run spans. The caller
    /// holds the tree latch, so the returned position stays valid while it acts on it.
    /// </summary>
    private bool TryFindEntry(ReadOnlySpan<byte> key, in EntryMatch match, out long leafId, out int index)
    {
        leafId = DescendToFirstLeaf(key);
        bool firstLeaf = true;

        while (leafId >= 0)
        {
            long nextLeaf;

            using (var handle = _storage.PageManager.GetPage((PageId)leafId))
            {
                var node = new BTreeNode(handle.Page.AsBodySpan());
                nextLeaf = node.NextLeaf;
                index = firstLeaf ? node.FindLowerBound(key) : 0;
                firstLeaf = false;

                for (; index < node.EntryCount; index++)
                {
                    if (!node.GetKey(index).SequenceEqual(key))
                    {
                        leafId = -1;
                        index = -1;
                        return false; // walked past the key
                    }

                    if (match.Accepts(node, index))
                    {
                        return true;
                    }
                }
            }

            leafId = nextLeaf; // the run may continue on the next leaf
        }

        index = -1;
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
        var path = new List<PathEntry>();

        while (true)
        {
            path.Clear();
            long leafId = DescendToLastLeaf(key, path);
            bool fits;
            bool fitsAfterCompaction;

            using (var handle = _storage.PageManager.GetPage((PageId)leafId))
            {
                var node = new BTreeNode(handle.Page.AsBodySpan());
                int needed = node.LeafEntrySize(key.Length);
                fits = needed <= node.FreeSpace;

                // Undo removals (erase, purge) orphan entry bytes. A leaf they fill is
                // compacted rather than split: a split there would divide too few
                // entries — none at all, when an undo emptied the leaf.
                fitsAfterCompaction = !fits && needed <= node.FreeSpace + node.OrphanedLeafBytes;
            }

            if (fits || fitsAfterCompaction)
            {
                using var writable = _storage.OpenPageForWrite(transaction, (PageId)leafId);
                var writableNode = new BTreeNode(writable.Page.AsBodySpan());

                if (fitsAfterCompaction)
                {
                    RebuildLeaf(ref writableNode, writableNode.EntryCount, writableNode.PrevLeaf, writableNode.NextLeaf);
                }

                writableNode.InsertLeafEntry(writableNode.FindLowerBound(key), key, entryReference, writer, deleter);
                writable.MarkDirty();
                return;
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

    /// <summary>
    /// Descends to the first leaf that can hold <paramref name="key"/>: every entry
    /// equal to the key lies in that leaf or to its right on the leaf chain. Lookups
    /// that must see all of a key's entries start here.
    /// </summary>
    private long DescendToFirstLeaf(ReadOnlySpan<byte> key)
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

            current = node.GetChildAt(node.FindFirstChildSlot(key));
        }
    }

    /// <summary>
    /// Descends to the last leaf that can hold <paramref name="key"/>: no entry to its
    /// left is greater than the key. Inserts route here; when <paramref name="path"/>
    /// is supplied, it receives each internal node visited and the child slot taken,
    /// which is where a split attaches its new node.
    /// </summary>
    private long DescendToLastLeaf(ReadOnlySpan<byte> key, List<PathEntry>? path)
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

            int slot = node.FindLastChildSlot(key);
            path?.Add(new PathEntry(current, slot));
            current = node.GetChildAt(slot);
        }
    }

    private void SplitLeaf(IStorageTransaction transaction, List<PathEntry> parentPath, long leafId)
    {
        byte[] separator;
        long siblingId;

        using (var leafHandle = _storage.OpenPageForWrite(transaction, (PageId)leafId))
        using (var siblingHandle = _storage.AllocatePageForWrite(transaction, PageType.Index))
        {
            var leaf = new BTreeNode(leafHandle.Page.AsBodySpan());
            int count = leaf.EntryCount;

            if (count < 2)
            {
                // Unreachable while inserts compact orphaned bytes first: a compacted
                // leaf with fewer than two entries always has room for one more.
                throw new IndexException($"Index '{Name}' cannot split leaf page {leafId} holding {count} entries.");
            }

            var sibling = BTreeNode.Initialize(siblingHandle.Page.AsBodySpan(), BTreeNode.LeafKind);
            siblingId = (long)siblingHandle.Id;

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

        // The caller re-descends, so where a root leaf's lower half lands is moot here.
        InsertIntoParent(transaction, parentPath, leafId, separator, siblingId);
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

    /// <summary>
    /// Attaches <paramref name="childId"/> — the new right half of
    /// <paramref name="splitId"/>, the child the last path entry descended into —
    /// directly after that child in its parent. The position comes from the recorded
    /// slot, not from the separator's value: a parent over a split run of equal keys
    /// holds equal separators, and a value-based position could land the new child
    /// left of its sibling, out of step with the leaf chain.
    /// </summary>
    /// <returns>
    /// The page now holding the split node's lower half: <paramref name="splitId"/>,
    /// unless that node was the root, whose contents move to a new page.
    /// </returns>
    private long InsertIntoParent(IStorageTransaction transaction, List<PathEntry> parentPath, long splitId, byte[] separator, long childId)
    {
        if (parentPath.Count == 0)
        {
            return GrowRoot(transaction, separator, childId);
        }

        var (parentId, splitSlot) = parentPath[^1];

        // Child c(p) sits in slot p - 1 (c(0) is the leftmost child), so the new
        // separator goes to directory index p and its child becomes c(p + 1).
        int position = splitSlot + 1;

        using (var parentHandle = _storage.OpenPageForWrite(transaction, (PageId)parentId))
        {
            var parent = new BTreeNode(parentHandle.Page.AsBodySpan());

            if (parent.InternalEntrySize(separator.Length) <= parent.FreeSpace)
            {
                AssertSeparatorOrder(parent, position, separator);
                parent.InsertInternalEntry(position, separator, childId);
                parentHandle.MarkDirty();
                return splitId;
            }
        }

        // The parent is full: split it, then attach the child to whichever half now
        // holds the child that split. Children c(0)..c(mid) stay left; c(mid + 1)
        // became the sibling's leftmost child.
        var (mid, parentLeftId, parentSiblingId) = SplitInternal(transaction, parentPath);

        long target = position <= mid ? parentLeftId : parentSiblingId;
        int targetPosition = position <= mid ? position : position - mid - 1;

        using var targetHandle = _storage.OpenPageForWrite(transaction, (PageId)target);
        var targetNode = new BTreeNode(targetHandle.Page.AsBodySpan());
        AssertSeparatorOrder(targetNode, targetPosition, separator);
        targetNode.InsertInternalEntry(targetPosition, separator, childId);
        targetHandle.MarkDirty();
        return splitId;
    }

    /// <summary>
    /// Completes a root split without moving the root: the root's contents (its
    /// lower half, after the split) move to a new page, and the root page becomes an
    /// internal node over that page and <paramref name="childId"/>. The root id never
    /// changes, so nothing outside the tree's own pages has to change with it — a
    /// rollback or a crash reverts the root page like any other.
    /// </summary>
    /// <returns>The new page holding the old root's contents.</returns>
    private long GrowRoot(IStorageTransaction transaction, byte[] separator, long childId)
    {
        long leftId;

        using (var rootHandle = _storage.OpenPageForWrite(transaction, (PageId)RootPageId))
        using (var leftHandle = _storage.AllocatePageForWrite(transaction, PageType.Index))
        {
            var rootBody = rootHandle.Page.AsBodySpan();
            var leftBody = leftHandle.Page.AsBodySpan();
            leftId = (long)leftHandle.Id;

            rootBody.CopyTo(leftBody);
            var left = new BTreeNode(leftBody);

            if (left.IsLeaf)
            {
                // A root leaf has no left neighbour; its right neighbour is the new
                // sibling, which must now point back at the relocated half.
                if (left.PrevLeaf >= 0)
                {
                    using var previousHandle = _storage.OpenPageForWrite(transaction, (PageId)left.PrevLeaf);
                    new BTreeNode(previousHandle.Page.AsBodySpan()).NextLeaf = leftId;
                    previousHandle.MarkDirty();
                }

                if (left.NextLeaf >= 0)
                {
                    using var nextHandle = _storage.OpenPageForWrite(transaction, (PageId)left.NextLeaf);
                    new BTreeNode(nextHandle.Page.AsBodySpan()).PrevLeaf = leftId;
                    nextHandle.MarkDirty();
                }
            }

            var root = BTreeNode.Initialize(rootBody, BTreeNode.InternalKind);
            root.LeftmostChild = leftId;
            root.InsertInternalEntry(0, separator, childId);

            leftHandle.MarkDirty();
            rootHandle.MarkDirty();
        }

        return leftId;
    }

    /// <summary>
    /// Splits the internal node the last path entry names and attaches the new right
    /// half to the parent. The split point balances bytes, not entry counts:
    /// separators range from a few bytes to <see cref="BTreeNode.MaxKeyLength"/>, and
    /// a count-balanced split can leave a half too full to take the one separator it
    /// must accept next. With each half holding at most half the node's bytes, both
    /// keep room for a maximum-length separator.
    /// </summary>
    /// <returns>
    /// The directory index that was promoted (and the count kept on the left), the
    /// page holding the left half (a new page when the root split), and the new
    /// sibling's page.
    /// </returns>
    private (int Mid, long LeftId, long SiblingId) SplitInternal(IStorageTransaction transaction, List<PathEntry> path)
    {
        long nodeId = path[^1].PageId;
        byte[] promoted;
        int mid;
        long siblingId;

        using (var nodeHandle = _storage.OpenPageForWrite(transaction, (PageId)nodeId))
        using (var siblingHandle = _storage.AllocatePageForWrite(transaction, PageType.Index))
        {
            var node = new BTreeNode(nodeHandle.Page.AsBodySpan());
            int count = node.EntryCount;

            if (count < 2)
            {
                // Unreachable: a full internal node holds several maximum-length separators.
                throw new IndexException($"Index '{Name}' cannot split internal page {nodeId} holding {count} entries.");
            }

            var sibling = BTreeNode.Initialize(siblingHandle.Page.AsBodySpan(), BTreeNode.InternalKind);
            siblingId = (long)siblingHandle.Id;

            mid = ChooseInternalSplit(node);

            // The separator at mid is promoted; its child becomes the sibling's leftmost.
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

        long leftId = InsertIntoParent(transaction, path.GetRange(0, path.Count - 1), nodeId, promoted, siblingId);
        return (mid, leftId, siblingId);
    }

    /// <summary>
    /// Chooses the separator to promote: the first one at which the entries before it
    /// would exceed half the node's bytes. The left half then holds at most half the
    /// bytes, and the right half — everything after the promoted separator — less
    /// than half.
    /// </summary>
    private static int ChooseInternalSplit(in BTreeNode node)
    {
        int count = node.EntryCount;
        int total = 0;

        for (int i = 0; i < count; i++)
        {
            total += node.InternalEntrySize(node.GetKey(i).Length) + BTreeNode.DirectorySlotSize;
        }

        int half = total / 2;
        int kept = 0;
        int mid = 0;

        while (mid < count - 1)
        {
            int size = node.InternalEntrySize(node.GetKey(mid).Length) + BTreeNode.DirectorySlotSize;
            if (kept + size > half)
            {
                break;
            }

            kept += size;
            mid++;
        }

        return mid;
    }

    /// <summary>
    /// Checks (debug builds) that a separator inserted at <paramref name="position"/>
    /// keeps the directory ordered: it may equal its neighbours, never invert them.
    /// </summary>
    [Conditional("DEBUG")]
    private static void AssertSeparatorOrder(in BTreeNode node, int position, ReadOnlySpan<byte> separator)
    {
        Debug.Assert(position >= 0 && position <= node.EntryCount, "Separator position outside the directory.");
        Debug.Assert(position == 0 || node.GetKey(position - 1).SequenceCompareTo(separator) <= 0,
            "Separator sorts before its left neighbour.");
        Debug.Assert(position == node.EntryCount || node.GetKey(position).SequenceCompareTo(separator) >= 0,
            "Separator sorts after its right neighbour.");
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

    /// <summary>
    /// One step of an insert's descent: the internal node visited and the child slot
    /// taken (<c>-1</c> for the leftmost child).
    /// </summary>
    private readonly record struct PathEntry(long PageId, int Slot);

    private enum EntryMatchKind : byte
    {
        Live,
        LiveVisible,
        WrittenBy,
        DeletedBy,
    }

    /// <summary>
    /// Which of a key's entries a lookup wants: an allocation-free predicate for
    /// <see cref="TryFindEntry"/>.
    /// </summary>
    private readonly struct EntryMatch
    {
        private readonly EntryMatchKind _kind;
        private readonly ulong _entryReference;
        private readonly ulong _stamp;
        private readonly TransactionSnapshot? _snapshot;

        private EntryMatch(EntryMatchKind kind, ulong entryReference, ulong stamp, TransactionSnapshot? snapshot)
        {
            _kind = kind;
            _entryReference = entryReference;
            _stamp = stamp;
            _snapshot = snapshot;
        }

        /// <summary>
        /// Any live entry (deleter stamp zero) — the unique check's latest-state test.
        /// </summary>
        internal static EntryMatch Live => new(EntryMatchKind.Live, 0, 0, null);

        /// <summary>
        /// The live mapping to <paramref name="entryReference"/> whose writer
        /// <paramref name="snapshot"/> sees — the entry a delete tombstones.
        /// </summary>
        internal static EntryMatch LiveVisible(ulong entryReference, TransactionSnapshot snapshot)
            => new(EntryMatchKind.LiveVisible, entryReference, 0, snapshot);

        /// <summary>
        /// The mapping to <paramref name="entryReference"/> stamped by
        /// <paramref name="writer"/> — the entry an erase removes.
        /// </summary>
        internal static EntryMatch WrittenBy(ulong entryReference, ulong writer)
            => new(EntryMatchKind.WrittenBy, entryReference, writer, null);

        /// <summary>
        /// The mapping to <paramref name="entryReference"/> tombstoned by
        /// <paramref name="deleter"/> — the entry a clear-deleter restores.
        /// </summary>
        internal static EntryMatch DeletedBy(ulong entryReference, ulong deleter)
            => new(EntryMatchKind.DeletedBy, entryReference, deleter, null);

        internal bool Accepts(in BTreeNode node, int index) => _kind switch
        {
            EntryMatchKind.Live => node.GetDeleter(index) == 0,
            EntryMatchKind.LiveVisible => node.GetEntryReference(index) == _entryReference
                && node.GetDeleter(index) == 0
                && _snapshot!.IsVisible(new TransactionSequence(node.GetWriter(index))),
            EntryMatchKind.WrittenBy => node.GetEntryReference(index) == _entryReference
                && node.GetWriter(index) == _stamp,
            EntryMatchKind.DeletedBy => node.GetEntryReference(index) == _entryReference
                && node.GetDeleter(index) == _stamp,
            _ => false,
        };
    }
}
