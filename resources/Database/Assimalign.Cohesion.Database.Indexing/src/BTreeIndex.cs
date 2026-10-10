using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

using Assimalign.Cohesion.Database.Indexing.Internal;

using Separator = Assimalign.Cohesion.Database.Indexing.Internal.BTreeEntryOrder.Separator;

namespace Assimalign.Cohesion.Database.Indexing;

/// <summary>
/// The B+Tree index: sorted-directory nodes on <see cref="PageType.Index"/> pages,
/// MVCC-stamped leaf entries, and page mutations that ride the owning transaction's
/// write-ahead scope (a crash mid-split recovers the tree its committed brackets left:
/// recovery rebuilds every page from its full page image and the committed deltas after
/// it, and never applies an uncommitted bracket's changes).
/// </summary>
/// <remarks>
/// <para>
/// A single index over an object's entries: ordered key → entry-reference mappings. Index
/// mutations ride the owning transaction — they are stamped with the writing transaction's
/// sequence and become visible under the same MVCC rules as the data they reference. Values
/// are opaque entry references (typically a page address or entry identity) supplied by the
/// model's storage layer.
/// </para>
/// <para>
/// An entry is identified by its key, its entry reference and its writer stamp, and
/// that identity is unique: inserting an identity that is already present is a defect
/// in the caller and fails with <see cref="IndexException"/>. Any operation fails with
/// <see cref="IndexCorruptionException"/> (<c>COHDBI002</c>) when it reaches a damaged
/// index page.
/// </para>
/// <para>
/// The type is sealed, and only a <see cref="BTreeIndexManager"/> creates one: it is the one
/// index structure the engine ships, and every call on it is direct.
/// </para>
/// <para>
/// Concurrency model (MVP): a tree-level reader/writer latch serializes structural
/// access — writers are exclusive, cursors materialize their results under the read
/// latch. Entry-level write conflicts between transactions are the lock manager's
/// job (unique keys) and the storage page write locks' job (everything else).
/// Deletes are tombstones (the deleter stamp); physical reclamation and node merges
/// belong to the vacuum feature that follows version pruning.
/// </para>
/// <para>
/// Entry order (#1194): keys repeat — secondary indexes repeat a value per row, and
/// every MVCC version adds an entry — so entries are ordered by their identity
/// <c>(key, entry reference, writer)</c> (<see cref="BTreeEntryOrder"/>), which is
/// unique, and separators carry as much of that identity as they need to separate
/// their neighbours. Every operation that targets one entry descends straight to
/// it; seeks start before the first entry of their key, at <c>(key, -inf)</c>.
/// </para>
/// </remarks>
public sealed class BTreeIndex
{
    private readonly Storage.Storage _storage;
    private readonly Func<TransactionContext, StorageTransaction> _transactionSource;
    private readonly LockManager? _lockManager;
    private readonly ulong _objectId;
    private readonly ReaderWriterLockSlim _latch = new(LockRecursionPolicy.NoRecursion);
    private readonly long _rootPageId;

    internal BTreeIndex(
        Storage.Storage storage,
        Func<TransactionContext, StorageTransaction> transactionSource,
        LockManager? lockManager,
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

    /// <summary>
    /// Gets the name of the index, unique within its owning object.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the physical structure backing the index.
    /// </summary>
    public IndexKind Kind => IndexKind.BTree;

    /// <summary>
    /// Gets a value indicating whether the index enforces key uniqueness.
    /// </summary>
    public bool IsUnique { get; }

    /// <summary>
    /// Gets the root page. It never moves: a root split relocates the root's contents
    /// to a new child and rewrites the root page in place, so the id a catalog
    /// registered when the tree was created stays valid — through a rollback of the
    /// split (the root page reverts to the bracket's in-memory pre-image) and through a crash
    /// between the split and the catalog's next persistence point.
    /// </summary>
    internal long RootPageId => _rootPageId;

    /// <summary>
    /// Allocates the root leaf of a new tree inside the given storage transaction.
    /// </summary>
    internal static long CreateRoot(Storage.Storage storage, StorageTransaction transaction)
    {
        using var handle = storage.AllocatePageForWrite(transaction, PageType.Index);
        BTreeNode.Initialize(handle.Page.AsBodySpan(), BTreeNode.LeafKind);
        handle.MarkDirty();
        return (long)handle.Id;
    }

    /// <summary>
    /// Verifies that the registered root page of an existing tree is a node of this
    /// engine's page format — the attach-time check, PostgreSQL's metapage
    /// <c>btm_magic</c>/<c>btm_version</c> test (<c>nbtpage.c</c> <c>_bt_getmeta</c>)
    /// moved onto the root page, which never moves. A tree is written by one engine
    /// from its root down, so the root's format is the tree's.
    /// </summary>
    /// <exception cref="IndexFormatException">The root page is not a node of this format.</exception>
    internal static void EnsureFormat(Storage.Storage storage, BTreeIndexRegistration registration)
    {
        int found;

        using (var handle = storage.PageManager.GetPage((PageId)registration.RootPageId))
        {
            found = handle.Page.Type == PageType.Index
                ? BTreeNode.ReadFormatVersion(handle.Page.AsBodySpan())
                : 0;

            if (found == BTreeNode.FormatVersion)
            {
                if (BTreeNode.IsCurrentFormat(handle.Page.AsBodySpan()))
                {
                    return;
                }

                // The current stamp over a body that is no node of this format (its
                // kind byte is neither leaf nor internal) is damage, not a format.
                found = 0;
            }
        }

        IndexEventSource.Log.IndexFormatRefused(storage, registration, found);
        throw new IndexFormatException(registration.Definition.Name, registration.ObjectId, registration.RootPageId, found);
    }

    /// <summary>
    /// Inserts a key → entry-reference mapping.
    /// </summary>
    /// <param name="transaction">The transaction the mutation belongs to.</param>
    /// <param name="key">The key to insert.</param>
    /// <param name="entryReference">The opaque entry reference the key maps to.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task representing the insert.</returns>
    /// <exception cref="IndexUniqueViolationException">
    /// The index is unique and the key already maps to a live entry in the latest
    /// state (not only in the transaction's snapshot).
    /// </exception>
    /// <exception cref="IndexException">
    /// The key exceeds the maximum key length, or an entry with the same key, entry
    /// reference and writer already exists (a defect: entry identities are unique).
    /// </exception>
    public async ValueTask InsertAsync(TransactionContext transaction, IndexKey key, ulong entryReference, CancellationToken cancellationToken = default)
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

        var storageTransaction = _transactionSource(transaction);

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

    /// <summary>
    /// Deletes a key → entry-reference mapping: tombstones the reference's live entry under the
    /// key with the transaction's sequence. A reference with no live entry under the key is a
    /// no-op.
    /// </summary>
    /// <remarks>
    /// The caller holds a write lock that excludes every other writer of the record version the
    /// reference names (Sql's row lock, KeyValuePair's key lock, the Documents and Graph database
    /// writer lock) and has checked that version's stamps under it, so the version's writer is
    /// decided, committed or the caller itself, and the reference's live entry is that
    /// version's. The match therefore reads the entry's stamps, not the transaction's snapshot:
    /// a write may delete a version its snapshot does not see, and a match through that snapshot
    /// finds nothing and leaves the deleted version's entry live, which a unique index then
    /// reports as a duplicate of every later insert of the key. A Sql cascade at
    /// <c>Snapshot</c> isolation did exactly that until #1370 (it now fails first-updater-wins
    /// before it deletes such a version).
    /// </remarks>
    /// <param name="transaction">The transaction the mutation belongs to.</param>
    /// <param name="key">The key to delete.</param>
    /// <param name="entryReference">The entry reference to remove for the key.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task representing the delete.</returns>
    public async ValueTask DeleteAsync(TransactionContext transaction, IndexKey key, ulong entryReference, CancellationToken cancellationToken = default)
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

        var storageTransaction = _transactionSource(transaction);

        _latch.EnterWriteLock();
        try
        {
            // The reference's live mapping — tombstone it under the transaction's
            // write scope. No match: nothing to delete. The caller knows the key and
            // the reference but not the writer; the live version is the reference's
            // newest (TryFindNewestEntry), so the lookup starts after the reference's
            // last version and reads backward.
            if (TryFindNewestEntry(
                key.Encoded.Span,
                entryReference,
                EntryMatch.LiveOf(entryReference),
                out long leafId,
                out int index))
            {
                using var writable = _storage.OpenPageForWrite(storageTransaction, (PageId)leafId);
                var writableNode = OpenNode(writable.Page.AsBodySpan(), leafId);
                writableNode.SetDeleter(index, transaction.Sequence.Value);
                writable.MarkDirty();
            }
        }
        finally
        {
            _latch.ExitWriteLock();
        }
    }

    /// <summary>
    /// Opens a cursor over the specified key range, positioned before the first match.
    /// </summary>
    /// <remarks>
    /// Entries with equal keys are returned in entry-reference order (reversed when
    /// <paramref name="reverse"/> is true).
    /// </remarks>
    /// <param name="transaction">The transaction whose snapshot reads resolve through.</param>
    /// <param name="range">The key range to scan.</param>
    /// <param name="reverse">Whether to scan in descending key order.</param>
    /// <returns>A cursor over the visible entries in the range.</returns>
    public BTreeCursor OpenCursor(TransactionContext transaction, IndexKeyRange range, bool reverse = false)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return OpenCursor(transaction.Snapshot, range, reverse);
    }

    /// <summary>
    /// Opens a cursor over the specified key range through an explicit visibility
    /// snapshot. This is the seam statement-scoped readers use: a statement's
    /// snapshot is captured once (per-statement under ReadCommitted), and reading
    /// through the same snapshot the row scan uses is what keeps an index seek
    /// exactly equivalent to the scan it replaces.
    /// </summary>
    /// <remarks>
    /// Entries with equal keys are returned in entry-reference order (reversed when
    /// <paramref name="reverse"/> is true).
    /// </remarks>
    /// <param name="snapshot">The visibility snapshot entries filter through.</param>
    /// <param name="range">The key range to scan.</param>
    /// <param name="reverse">Whether to scan in descending key order.</param>
    /// <returns>A cursor over the visible entries in the range.</returns>
    public BTreeCursor OpenCursor(TransactionSnapshot snapshot, IndexKeyRange range, bool reverse = false)
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

    /// <summary>
    /// Inserts an entry carrying explicit version stamps inside the given physical
    /// write-ahead bracket — the offline (DDL-blocking) build path: an index built
    /// over existing rows preserves each stored version's writer and deleter, so
    /// snapshots older than the index read exactly what the equivalent row scan
    /// shows them. No uniqueness check is performed; the builder owns duplicate
    /// detection over the live versions it feeds in (it holds the object's
    /// exclusive lock, so no concurrent writer can race the build).
    /// </summary>
    /// <param name="transaction">The physical storage bracket the build rides.</param>
    /// <param name="key">The key to insert.</param>
    /// <param name="entryReference">The opaque entry reference the key maps to.</param>
    /// <param name="writer">The version's writer stamp, preserved from the source row.</param>
    /// <param name="deleter">The version's deleter stamp, preserved from the source row (<see cref="TransactionSequence.None"/> for live versions).</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task representing the insert.</returns>
    /// <exception cref="IndexException">
    /// The key exceeds the maximum key length, or an entry with the same key, entry
    /// reference and writer already exists (a defect: entry identities are unique).
    /// </exception>
    public ValueTask InsertVersionAsync(StorageTransaction transaction, IndexKey key, ulong entryReference, TransactionSequence writer, TransactionSequence deleter, CancellationToken cancellationToken = default)
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

    /// <summary>
    /// Physically removes the entry mapping <paramref name="key"/> to
    /// <paramref name="entryReference"/> when its writer stamp equals
    /// <paramref name="writer"/> — the logical undo of an aborted writer's insert.
    /// Runs inside the given physical bracket (an undo executes while the aborting
    /// transaction still holds its locks, outside any statement bracket). A no-op
    /// when no matching entry exists (undo is idempotent by construction).
    /// </summary>
    /// <param name="transaction">The physical storage bracket the undo rides.</param>
    /// <param name="key">The key of the entry to remove.</param>
    /// <param name="entryReference">The entry reference the key maps to.</param>
    /// <param name="writer">The writer stamp the entry must carry to be removed.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task representing the undo.</returns>
    public ValueTask EraseAsync(StorageTransaction transaction, IndexKey key, ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        cancellationToken.ThrowIfCancellationRequested();

        _latch.EnterWriteLock();
        try
        {
            // The full identity is known: the descent lands on the entry's leaf.
            if (TryFindEntry(
                BTreeSearchKey.AtEntry(key.Encoded.Span, entryReference, writer.Value),
                EntryMatch.WrittenBy(entryReference, writer.Value),
                out long leafId,
                out int index))
            {
                using var writable = _storage.OpenPageForWrite(transaction, (PageId)leafId);
                var writableNode = OpenNode(writable.Page.AsBodySpan(), leafId);
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

    /// <summary>
    /// Clears the deleter stamp of the entry mapping <paramref name="key"/> to
    /// <paramref name="entryReference"/> when it equals <paramref name="deleter"/> —
    /// the logical undo of an aborted writer's tombstone. Runs inside the given
    /// physical bracket; a no-op when no matching entry exists.
    /// </summary>
    /// <param name="transaction">The physical storage bracket the undo rides.</param>
    /// <param name="key">The key of the tombstoned entry.</param>
    /// <param name="entryReference">The entry reference the key maps to.</param>
    /// <param name="deleter">The deleter stamp the entry must carry to be cleared.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task representing the undo.</returns>
    public ValueTask ClearDeleterAsync(StorageTransaction transaction, IndexKey key, ulong entryReference, TransactionSequence deleter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        cancellationToken.ThrowIfCancellationRequested();

        _latch.EnterWriteLock();
        try
        {
            // The deleter is not part of the order: look among the reference's
            // versions, newest first — the only one an aborted deleter can have
            // stamped is the reference's newest (TryFindNewestEntry).
            if (TryFindNewestEntry(
                key.Encoded.Span,
                entryReference,
                EntryMatch.DeletedBy(entryReference, deleter.Value),
                out long leafId,
                out int index))
            {
                using var writable = _storage.OpenPageForWrite(transaction, (PageId)leafId);
                var writableNode = OpenNode(writable.Page.AsBodySpan(), leafId);
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
    /// aborted-writer purge (see <see cref="BTreeIndexManager.PurgeWritersAsync"/>).
    /// Removing entries and clearing deleter stamps never reorders a leaf, and every
    /// separator stays a valid bound for the entries that remain.
    /// </summary>
    internal long PurgeWriters(StorageTransaction transaction, IReadOnlySet<TransactionSequence> writers)
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
                    var node = OpenNode(handle.Page.AsBodySpan(), leafId);
                    nextLeaf = node.NextLeaf;

                    StoragePageHandle? writable = null;
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
                                node = OpenNode(writable.Page.AsBodySpan(), leafId);
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
    /// Materializes the snapshot-visible entries of <paramref name="range"/> in
    /// order. An inclusive start descends to <c>(start, -inf)</c>, before the first
    /// entry of the start key; an exclusive start to <c>(start, +inf)</c>, after its
    /// last. Only the first leaf needs positioning: every later leaf on the chain
    /// holds entries past the start. The first leaf may hold no entry at or past the
    /// start (the start falls between its last entry and the next separator); the
    /// walk then begins on the next leaf.
    /// </summary>
    private void CollectVisible(TransactionSnapshot snapshot, IndexKeyRange range, List<(byte[] Key, ulong EntryReference)> results)
    {
        ReadOnlySpan<byte> startKey = range.Start.HasValue ? range.Start.Value.Encoded.Span : default;
        ReadOnlySpan<byte> endKey = range.End.HasValue ? range.End.Value.Encoded.Span : default;
        var start = range.IsStartInclusive ? BTreeSearchKey.AtKey(startKey) : BTreeSearchKey.AfterKey(startKey);

        long leafId = range.Start is null ? DescendToLeftmostLeaf() : Descend(start, null);
        bool firstLeaf = true;

        while (leafId >= 0)
        {
            long nextLeaf;

            using (var handle = _storage.PageManager.GetPage((PageId)leafId))
            {
                var node = OpenNode(handle.Page.AsBodySpan(), leafId);
                nextLeaf = node.NextLeaf;
                int index = 0;

                if (firstLeaf && range.Start is not null)
                {
                    index = node.FindLowerBound(start);
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
    /// the unique check, which runs under the key's exclusive lock. A key's live
    /// version can sit anywhere among its dead ones, so the check reads the key's
    /// entries from <c>(key, -inf)</c> until it finds a live one or passes the key:
    /// one descent, then a walk over the run on consecutive leaves. Bounding that
    /// walk needs dead versions pruned (#1195).
    /// </summary>
    private bool HasLiveEntry(ReadOnlySpan<byte> key) => TryFindEntry(BTreeSearchKey.AtKey(key), EntryMatch.Live, out _, out _);

    /// <summary>
    /// Finds the first entry at or after <paramref name="start"/> that still shares
    /// its prefix — the key, plus the entry reference and writer when
    /// <paramref name="start"/> names them — and that <paramref name="match"/>
    /// accepts. The descent lands on the leaf whose range holds
    /// <paramref name="start"/>; the walk follows the leaf chain until an entry leaves
    /// the prefix. For a full identity that is at most one entry (identities are
    /// unique); for a key, its whole run (a reference's versions are read newest
    /// first instead, <see cref="TryFindNewestEntry"/>). The caller holds the tree
    /// latch, so the returned position stays valid while it acts on it.
    /// </summary>
    private bool TryFindEntry(in BTreeSearchKey start, in EntryMatch match, out long leafId, out int index)
    {
        leafId = Descend(start, null);
        bool firstLeaf = true;

        while (leafId >= 0)
        {
            long nextLeaf;

            using (var handle = _storage.PageManager.GetPage((PageId)leafId))
            {
                var node = OpenNode(handle.Page.AsBodySpan(), leafId);
                nextLeaf = node.NextLeaf;
                index = firstLeaf ? node.FindLowerBound(start) : 0;
                firstLeaf = false;

                for (; index < node.EntryCount; index++)
                {
                    if (!SharesPrefix(node, index, start))
                    {
                        leafId = -1;
                        index = -1;
                        return false; // walked past the prefix
                    }

                    if (match.Accepts(node, index))
                    {
                        return true;
                    }
                }
            }

            leafId = nextLeaf; // the prefix may continue on the next leaf
        }

        index = -1;
        return false;
    }

    /// <summary>
    /// Finds the newest version of <paramref name="entryReference"/> under
    /// <paramref name="key"/> that <paramref name="match"/> accepts, reading the
    /// reference's versions from the last one backward: the descent goes to the
    /// position after the reference's last version
    /// (<see cref="BTreeSearchKey.AfterReference"/>), and the walk follows the leaf
    /// chain leftward until an entry leaves the reference.
    /// </summary>
    /// <remarks>
    /// The newest version is the one delete and clear-deleter want. A record slot holds
    /// one version at a time, and an engine reuses a slot under the same key only after
    /// the version purge reclaimed the slot's previous version, which needs that
    /// version's deleter committed below every active transaction. Every version of a
    /// reference but the newest therefore carries a committed deleter: the newest is
    /// the only one that can be live, and the only one an in-flight or aborting deleter
    /// can have stamped. The lookup is then one descent, however many dead versions a
    /// reused slot has left under the key until they are pruned (#1195). When the
    /// newest does not match — a replayed or stale undo, which is a no-op — the walk
    /// reads the older versions too, so the result never depends on that invariant;
    /// only the cost does.
    /// </remarks>
    private bool TryFindNewestEntry(ReadOnlySpan<byte> key, ulong entryReference, in EntryMatch match, out long leafId, out int index)
    {
        var end = BTreeSearchKey.AfterReference(key, entryReference);
        var prefix = BTreeSearchKey.AtReference(key, entryReference);
        leafId = Descend(end, null);
        bool firstLeaf = true;

        while (leafId >= 0)
        {
            long previousLeaf;

            using (var handle = _storage.PageManager.GetPage((PageId)leafId))
            {
                var node = OpenNode(handle.Page.AsBodySpan(), leafId);
                previousLeaf = node.PrevLeaf;

                // On the first leaf, the entries before the end position; on every
                // earlier leaf, all of them. An undo can leave a leaf empty, and the
                // walk then simply moves on to the leaf before it.
                index = (firstLeaf ? node.FindLowerBound(end) : node.EntryCount) - 1;
                firstLeaf = false;

                for (; index >= 0; index--)
                {
                    if (!SharesPrefix(node, index, prefix))
                    {
                        leafId = -1;
                        index = -1;
                        return false; // walked past the reference's first version
                    }

                    if (match.Accepts(node, index))
                    {
                        return true;
                    }
                }
            }

            leafId = previousLeaf; // the reference's versions may continue on the leaf before
        }

        index = -1;
        return false;
    }

    /// <summary>
    /// Whether leaf entry <paramref name="index"/> lies inside the prefix
    /// <paramref name="start"/> names: the same key, and the same entry reference and
    /// writer as far as <paramref name="start"/> carries them.
    /// </summary>
    private static bool SharesPrefix(in BTreeNode node, int index, in BTreeSearchKey start)
    {
        if (!node.GetKey(index).SequenceEqual(start.Key))
        {
            return false;
        }

        return start.Tiebreaker switch
        {
            BTreeTiebreaker.Reference => node.GetEntryReference(index) == start.EntryReference,
            BTreeTiebreaker.Entry => node.GetEntryReference(index) == start.EntryReference && node.GetWriter(index) == start.Writer,
            _ => true,
        };
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

    private void InsertCore(StorageTransaction transaction, ReadOnlySpan<byte> key, ulong entryReference, ulong writer, ulong deleter)
    {
        var position = BTreeSearchKey.AtEntry(key, entryReference, writer);
        var path = new List<PathEntry>();

        while (true)
        {
            path.Clear();
            long leafId = Descend(position, path);
            bool fits;
            bool fitsAfterCompaction;
            int at;

            using (var handle = _storage.PageManager.GetPage((PageId)leafId))
            {
                var node = OpenNode(handle.Page.AsBodySpan(), leafId);

                // An entry's identity is unique — the order is total only because it
                // is, and a separator can only fall between two different entries.
                // An equal entry would be in this leaf, at the insert position.
                // PostgreSQL treats a duplicate heap TID the same way, as corruption
                // (nbtsearch.c, _bt_binsrch_insert).
                at = node.FindLowerBound(position);
                if (at < node.EntryCount && node.CompareToEntry(position, at) == 0)
                {
                    throw new IndexException(
                        $"Index '{Name}' already holds the entry for reference {entryReference} written by transaction {writer} under this key; " +
                        "an entry's (key, reference, writer) identity must be unique.");
                }

                int needed = BTreeNode.LeafEntrySize(key.Length);
                fits = needed <= node.FreeSpace;

                // Undo removals (erase, purge) orphan entry bytes. A leaf they fill is
                // compacted rather than split: a split there would divide too few
                // entries — none at all, when an undo emptied the leaf.
                fitsAfterCompaction = !fits && needed <= node.FreeSpace + node.OrphanedLeafBytes;
            }

            if (fits || fitsAfterCompaction)
            {
                using var writable = _storage.OpenPageForWrite(transaction, (PageId)leafId);
                var writableNode = OpenNode(writable.Page.AsBodySpan(), leafId);

                // The position the read pass found still holds: the write latch kept
                // the leaf unchanged, and compaction keeps its entries and their order.
                if (fitsAfterCompaction)
                {
                    RebuildLeaf(ref writableNode, writableNode.EntryCount, writableNode.PrevLeaf, writableNode.NextLeaf);
                }

                writableNode.InsertLeafEntry(at, key, entryReference, writer, deleter);
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
            var node = OpenNode(handle.Page.AsBodySpan(), current);

            if (node.IsLeaf)
            {
                return current;
            }

            current = node.LeftmostChild;
        }
    }

    /// <summary>
    /// Descends to the leaf whose range holds <paramref name="key"/>: at every level,
    /// the child of the last separator not greater than the key. An entry equal to a
    /// full identity is in that leaf; entries after a partial position start in it or
    /// on the next leaf. When <paramref name="path"/> is supplied, it receives each
    /// internal node visited and the child slot taken, which is where a split
    /// attaches its new node.
    /// </summary>
    private long Descend(in BTreeSearchKey key, List<PathEntry>? path)
    {
        long current = RootPageId;

        while (true)
        {
            using var handle = _storage.PageManager.GetPage((PageId)current);
            var node = OpenNode(handle.Page.AsBodySpan(), current);

            if (node.IsLeaf)
            {
                return current;
            }

            int slot = node.FindChildSlot(key);
            path?.Add(new PathEntry(current, slot));
            current = node.GetChildAt(slot);
        }
    }

    private void SplitLeaf(StorageTransaction transaction, List<PathEntry> parentPath, long leafId)
    {
        Separator separator;
        long siblingId;
        int count;

        using (var leafHandle = _storage.OpenPageForWrite(transaction, (PageId)leafId))
        using (var siblingHandle = _storage.AllocatePageForWrite(transaction, PageType.Index))
        {
            var leaf = OpenNode(leafHandle.Page.AsBodySpan(), leafId);
            count = leaf.EntryCount;

            if (count < 2)
            {
                // Unreachable while inserts compact orphaned bytes first: a compacted
                // leaf with fewer than two entries always has room for one more.
                throw InvariantViolated(leafId, $"Index '{Name}' cannot split leaf page {leafId} holding {count} entries.");
            }

            int mid = ChooseLeafSplit(leaf);
            separator = BTreeEntryOrder.BuildSeparator(leaf, mid - 1, mid);
            EnsureLeafSeparator(leaf, leafId, mid, separator);

            var sibling = BTreeNode.Initialize(siblingHandle.Page.AsBodySpan(), BTreeNode.LeafKind);
            siblingId = (long)siblingHandle.Id;

            // Move the upper half to the sibling.
            for (int i = mid; i < count; i++)
            {
                sibling.InsertLeafEntry(
                    i - mid, leaf.GetKey(i), leaf.GetEntryReference(i), leaf.GetWriter(i), leaf.GetDeleter(i));
            }

            // Fix the sibling chain.
            long oldNext = leaf.NextLeaf;
            sibling.NextLeaf = oldNext;
            sibling.PrevLeaf = leafId;

            if (oldNext >= 0)
            {
                using var oldNextHandle = _storage.OpenPageForWrite(transaction, (PageId)oldNext);
                var oldNextNode = OpenNode(oldNextHandle.Page.AsBodySpan(), oldNext);
                oldNextNode.PrevLeaf = siblingId;
                oldNextHandle.MarkDirty();
            }

            // Rebuild the source with the lower half (reclaims the moved bytes).
            RebuildLeaf(ref leaf, mid, leaf.PrevLeaf, siblingId);

            leafHandle.MarkDirty();
            siblingHandle.MarkDirty();
        }

        IndexEventSource.Log.PageSplit(_storage, Name, leafId, leaf: true, count);

        // The caller re-descends, so where a root leaf's lower half lands is moot here.
        InsertIntoParent(transaction, parentPath, leafId, separator, siblingId);
    }

    /// <summary>
    /// Chooses where a full leaf splits: the number of entries that stay on the left.
    /// The one place the leaf split point is decided, so a better policy (#1196: split
    /// at the insertion point for rightmost and duplicate-run inserts, PostgreSQL's
    /// <c>nbtsplitloc.c</c> <c>_bt_findsplitloc</c>) replaces this function alone. The
    /// separator is built from the entries on either side of the point, and any point
    /// in <c>[1, count - 1]</c> is valid: adjacent entries always differ.
    /// </summary>
    private static int ChooseLeafSplit(in BTreeNode leaf) => leaf.EntryCount / 2;

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
    /// directly after that child in its parent, at the slot the descent recorded.
    /// Separators are unique under the entry order, so the slot always agrees with
    /// the separator's value; <see cref="EnsureSeparatorOrder"/> checks that it does.
    /// </summary>
    /// <returns>
    /// The page now holding the split node's lower half: <paramref name="splitId"/>,
    /// unless that node was the root, whose contents move to a new page.
    /// </returns>
    private long InsertIntoParent(StorageTransaction transaction, List<PathEntry> parentPath, long splitId, Separator separator, long childId)
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
            var parent = OpenNode(parentHandle.Page.AsBodySpan(), parentId);

            // Checked before the parent changes at all — a full parent would
            // otherwise split around a separator it cannot accept.
            EnsureSeparatorOrder(parent, parentId, position, separator);

            if (BTreeNode.InternalEntrySize(separator.Key.Length, separator.Tiebreaker) <= parent.FreeSpace)
            {
                parent.InsertInternalEntry(position, separator.AsSearchKey(), childId);
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
        var targetNode = OpenNode(targetHandle.Page.AsBodySpan(), target);
        EnsureSeparatorOrder(targetNode, target, targetPosition, separator);
        targetNode.InsertInternalEntry(targetPosition, separator.AsSearchKey(), childId);
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
    private long GrowRoot(StorageTransaction transaction, Separator separator, long childId)
    {
        long leftId;

        using (var rootHandle = _storage.OpenPageForWrite(transaction, (PageId)RootPageId))
        using (var leftHandle = _storage.AllocatePageForWrite(transaction, PageType.Index))
        {
            var rootBody = rootHandle.Page.AsBodySpan();
            var leftBody = leftHandle.Page.AsBodySpan();
            leftId = (long)leftHandle.Id;

            rootBody.CopyTo(leftBody);
            var left = OpenNode(leftBody, leftId);

            if (left.IsLeaf)
            {
                // A root leaf has no left neighbour; its right neighbour is the new
                // sibling, which must now point back at the relocated half.
                if (left.PrevLeaf >= 0)
                {
                    using var previousHandle = _storage.OpenPageForWrite(transaction, (PageId)left.PrevLeaf);
                    OpenNode(previousHandle.Page.AsBodySpan(), left.PrevLeaf).NextLeaf = leftId;
                    previousHandle.MarkDirty();
                }

                if (left.NextLeaf >= 0)
                {
                    using var nextHandle = _storage.OpenPageForWrite(transaction, (PageId)left.NextLeaf);
                    OpenNode(nextHandle.Page.AsBodySpan(), left.NextLeaf).PrevLeaf = leftId;
                    nextHandle.MarkDirty();
                }
            }

            var root = BTreeNode.Initialize(rootBody, BTreeNode.InternalKind);
            root.LeftmostChild = leftId;
            root.InsertInternalEntry(0, separator.AsSearchKey(), childId);

            leftHandle.MarkDirty();
            rootHandle.MarkDirty();
        }

        IndexEventSource.Log.RootGrown(_storage, Name, RootPageId);
        return leftId;
    }

    /// <summary>
    /// Splits the internal node the last path entry names and attaches the new right
    /// half to the parent. The split point balances bytes, not entry counts:
    /// separators range from one byte to a maximum-length key with both tiebreaker
    /// attributes, and a count-balanced split can leave a half too full to take the
    /// one separator it must accept next. With each half holding at most half the
    /// node's bytes, both keep room for a maximum-size separator.
    /// </summary>
    /// <returns>
    /// The directory index that was promoted (and the count kept on the left), the
    /// page holding the left half (a new page when the root split), and the new
    /// sibling's page.
    /// </returns>
    private (int Mid, long LeftId, long SiblingId) SplitInternal(StorageTransaction transaction, List<PathEntry> path)
    {
        long nodeId = path[^1].PageId;
        Separator promoted;
        int mid;
        long siblingId;
        int count;

        using (var nodeHandle = _storage.OpenPageForWrite(transaction, (PageId)nodeId))
        using (var siblingHandle = _storage.AllocatePageForWrite(transaction, PageType.Index))
        {
            var node = OpenNode(nodeHandle.Page.AsBodySpan(), nodeId);
            count = node.EntryCount;

            if (count < 2)
            {
                // Unreachable: a full internal node holds several maximum-size separators.
                throw InvariantViolated(nodeId, $"Index '{Name}' cannot split internal page {nodeId} holding {count} entries.");
            }

            var sibling = BTreeNode.Initialize(siblingHandle.Page.AsBodySpan(), BTreeNode.InternalKind);
            siblingId = (long)siblingHandle.Id;

            mid = ChooseInternalSplit(node);

            // The separator at mid is promoted; its child becomes the sibling's leftmost.
            promoted = Separator.FromNode(node, mid);
            sibling.LeftmostChild = node.GetChild(mid);

            for (int i = mid + 1; i < count; i++)
            {
                sibling.InsertInternalEntry(i - mid - 1, node.GetSeparator(i), node.GetChild(i));
            }

            RebuildInternal(ref node, mid);

            nodeHandle.MarkDirty();
            siblingHandle.MarkDirty();
        }

        IndexEventSource.Log.PageSplit(_storage, Name, nodeId, leaf: false, count);

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
            total += node.InternalEntryFootprint(i);
        }

        int half = total / 2;
        int kept = 0;
        int mid = 0;

        while (mid < count - 1)
        {
            int size = node.InternalEntryFootprint(mid);
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
    /// Fails a leaf split, in every build, unless its separator falls strictly after
    /// the last entry kept on the left and at or before the first entry moved right —
    /// the bound every descent relies on. Adjacent leaf entries always differ, so
    /// this holds for any sorted leaf; a failure means the leaf is out of order, and
    /// the caller's storage bracket rolls the split back.
    /// </summary>
    /// <exception cref="IndexException">The separator would not separate the two halves.</exception>
    private void EnsureLeafSeparator(in BTreeNode leaf, long leafId, int mid, Separator separator)
    {
        var bound = separator.AsSearchKey();

        if (leaf.CompareToEntry(bound, mid - 1) <= 0 || leaf.CompareToEntry(bound, mid) > 0)
        {
            throw InvariantViolated(
                leafId, $"Index '{Name}' cannot split leaf page {leafId}: its entries {mid - 1} and {mid} are out of order.");
        }
    }

    /// <summary>
    /// Fails the split, in every build, when a separator inserted at
    /// <paramref name="position"/> would leave the directory unordered: separators
    /// are unique under the entry order, so each must be strictly greater than its
    /// left neighbour and strictly less than its right one. A misordered separator
    /// would be committed with the split and misroute lookups from then on, and there
    /// is no repair path for a persisted tree (#1152). Throwing instead leaves the
    /// caller's storage bracket to roll the half-done split back. The cost is two
    /// comparisons per split.
    /// </summary>
    /// <exception cref="IndexException">The separator or its position would break the directory's order.</exception>
    private void EnsureSeparatorOrder(in BTreeNode node, long pageId, int position, Separator separator)
    {
        if (position < 0 || position > node.EntryCount)
        {
            throw InvariantViolated(
                pageId, $"Index '{Name}' split would insert a separator at position {position} of the {node.EntryCount}-entry directory on page {pageId}.");
        }

        var bound = separator.AsSearchKey();

        if ((position > 0 && node.CompareToSeparator(bound, position - 1) <= 0)
            || (position < node.EntryCount && node.CompareToSeparator(bound, position) >= 0))
        {
            throw InvariantViolated(pageId, $"Index '{Name}' split would misorder separators on page {pageId}.");
        }
    }

    /// <summary>
    /// Reports a split invariant the tree failed and returns the <see cref="IndexException"/> the
    /// insert fails with, for the caller to throw. The message names pages and positions, never a
    /// key, so the event carries it as is.
    /// </summary>
    private IndexException InvariantViolated(long pageId, string message)
    {
        IndexEventSource.Log.IndexInvariantViolated(_storage, Name, pageId, message);
        return new IndexException(message);
    }

    private static void RebuildInternal(ref BTreeNode node, int keepCount)
    {
        long leftmost = node.LeftmostChild;
        var separators = new Separator[keepCount];
        var children = new long[keepCount];

        for (int i = 0; i < keepCount; i++)
        {
            separators[i] = Separator.FromNode(node, i);
            children[i] = node.GetChild(i);
        }

        node = BTreeNode.Initialize(node.Body, BTreeNode.InternalKind);
        node.LeftmostChild = leftmost;

        for (int i = 0; i < keepCount; i++)
        {
            node.InsertInternalEntry(i, separators[i].AsSearchKey(), children[i]);
        }
    }

    /// <summary>
    /// Overlays a node on a page body, checking the page is a node of this format
    /// first — PostgreSQL checks every B-tree page it reads the same way and reports a
    /// failure as index corruption (<c>nbtpage.c</c> <c>_bt_checkpage</c>,
    /// <c>ERRCODE_INDEX_CORRUPTED</c>). A tree is attached only after its root passed
    /// <see cref="EnsureFormat"/>, so a failure here means a damaged page or a pointer
    /// to one.
    /// </summary>
    /// <exception cref="IndexCorruptionException">The page is not a node of this format.</exception>
    private BTreeNode OpenNode(Span<byte> body, long pageId)
    {
        if (!BTreeNode.IsCurrentFormat(body))
        {
            int found = BTreeNode.ReadFormatVersion(body);
            IndexEventSource.Log.IndexCorruptionDetected(_storage, Name, pageId, found);
            throw new IndexCorruptionException(Name, pageId, found);
        }

        return new BTreeNode(body);
    }

    /// <summary>
    /// One step of an insert's descent: the internal node visited and the child slot
    /// taken (<c>-1</c> for the leftmost child).
    /// </summary>
    private readonly record struct PathEntry(long PageId, int Slot);

    private enum EntryMatchKind : byte
    {
        Live,
        LiveOf,
        WrittenBy,
        DeletedBy,
    }

    /// <summary>
    /// Which of the entries a lookup walks it wants: an allocation-free predicate for
    /// <see cref="TryFindEntry"/>.
    /// </summary>
    private readonly struct EntryMatch
    {
        private readonly EntryMatchKind _kind;
        private readonly ulong _entryReference;
        private readonly ulong _stamp;

        private EntryMatch(EntryMatchKind kind, ulong entryReference, ulong stamp)
        {
            _kind = kind;
            _entryReference = entryReference;
            _stamp = stamp;
        }

        /// <summary>
        /// Any live entry (deleter stamp zero) — the unique check's latest-state test.
        /// </summary>
        internal static EntryMatch Live => new(EntryMatchKind.Live, 0, 0);

        /// <summary>
        /// The live mapping to <paramref name="entryReference"/> (deleter stamp zero), whoever
        /// wrote it — the entry a delete tombstones.
        /// </summary>
        internal static EntryMatch LiveOf(ulong entryReference)
            => new(EntryMatchKind.LiveOf, entryReference, 0);

        /// <summary>
        /// The mapping to <paramref name="entryReference"/> stamped by
        /// <paramref name="writer"/> — the entry an erase removes.
        /// </summary>
        internal static EntryMatch WrittenBy(ulong entryReference, ulong writer)
            => new(EntryMatchKind.WrittenBy, entryReference, writer);

        /// <summary>
        /// The mapping to <paramref name="entryReference"/> tombstoned by
        /// <paramref name="deleter"/> — the entry a clear-deleter restores.
        /// </summary>
        internal static EntryMatch DeletedBy(ulong entryReference, ulong deleter)
            => new(EntryMatchKind.DeletedBy, entryReference, deleter);

        internal bool Accepts(in BTreeNode node, int index) => _kind switch
        {
            EntryMatchKind.Live => node.GetDeleter(index) == 0,
            EntryMatchKind.LiveOf => node.GetEntryReference(index) == _entryReference
                && node.GetDeleter(index) == 0,
            EntryMatchKind.WrittenBy => node.GetEntryReference(index) == _entryReference
                && node.GetWriter(index) == _stamp,
            EntryMatchKind.DeletedBy => node.GetEntryReference(index) == _entryReference
                && node.GetDeleter(index) == _stamp,
            _ => false,
        };
    }
}
