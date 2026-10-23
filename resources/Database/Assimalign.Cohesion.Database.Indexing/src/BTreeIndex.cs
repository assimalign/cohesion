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
/// access — writers are exclusive, cursors copy bounded batches under the read
/// latch and resume by entry identity. Entry-level write conflicts are the lock manager's
/// job (unique keys) and the storage page write locks' job (everything else).
/// Deletes are tombstones (the deleter stamp); physical reclamation and node merges
/// belong to the vacuum feature that follows version pruning.
/// </para>
/// <para>
/// The latch is a <see cref="StoragePageLatch"/>, and every page write goes through the storage
/// overloads that take it, so a storage transaction that changed the tree restores its pages
/// under the latch when it rolls back (#1371): a failed statement's bracket rollback rewrites
/// each page in place, and a cursor that read during it saw cleared leaves (an entry reference
/// of zero, an empty seek) and a root that was no node.
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
    // Optional test observer of physical entry-directory reads. The ordinary path neither
    // counts work nor performs a process-wide atomic operation (#1285, #1194).
    internal static Action? EntryAccessed { get; set; }
    internal static Action? DescentStarted { get; set; }

    /// <summary>
    /// The maximum encoded key length accepted by a B+Tree, in bytes (1,024).
    /// </summary>
    /// <remarks>
    /// The limit includes all encoded components, NULL markers and collation transforms.
    /// Entry-reference and writer-stamp tiebreakers are stored separately and do not count
    /// toward it. Keys exactly this long are accepted. The bound ensures a fresh node
    /// holds several entries, as required for splitting. Models can validate write keys
    /// against this limit before beginning a storage mutation.
    /// </remarks>
    public const int MaxKeyLength = 1024;

    private readonly Storage.Storage _storage;
    private readonly Func<TransactionContext, StorageTransaction> _transactionSource;
    private readonly LockManager? _lockManager;
    private readonly ulong _objectId;
    private readonly StoragePageLatch _latch;
    private readonly long _rootPageId;
    private readonly Func<TransactionSequence>? _pruneBound;
    private long _nextPruneLeaf = -1;

    internal BTreeIndex(
        Storage.Storage storage,
        Func<TransactionContext, StorageTransaction> transactionSource,
        LockManager? lockManager,
        ulong objectId,
        IndexDefinition definition,
        long rootPageId,
        Func<TransactionSequence>? pruneBound = null)
    {
        _storage = storage;
        _transactionSource = transactionSource;
        _lockManager = lockManager;
        _objectId = objectId;
        Name = definition.Name;
        IsUnique = definition.IsUnique;
        _rootPageId = rootPageId;
        _latch = BTreeLatches.Get(storage, rootPageId);
        _pruneBound = pruneBound;
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
    /// Gets the tree latch: the storage page latch every reader holds shared and every writer, and
    /// a rolling-back storage transaction that changed the tree, hold exclusively.
    /// </summary>
    internal StoragePageLatch Latch => _latch;

    /// <summary>
    /// Allocates the root leaf of a new tree inside the given storage transaction.
    /// </summary>
    internal static long CreateRoot(Storage.Storage storage, StorageTransaction transaction, ulong objectId)
    {
        using var handle = storage.AllocatePageForWrite(transaction, PageType.Index, objectId);
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
        var latch = BTreeLatches.Get(storage, registration.RootPageId);
        latch.EnterRead();
        try
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
        finally { latch.ExitRead(); }
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

        if (key.Length > MaxKeyLength)
        {
            throw new IndexException($"Index key of {key.Length} bytes exceeds the {MaxKeyLength}-byte maximum.");
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

        cancellationToken.ThrowIfCancellationRequested();
        var storageTransaction = _transactionSource(transaction);
        var pruneBound = IsUnique ? _pruneBound?.Invoke() ?? TransactionSequence.None : TransactionSequence.None;

        _latch.EnterWrite();
        try
        {
            // Uniqueness is enforced against the LATEST state, not the begin
            // snapshot — snapshot-based checks would let two transactions that
            // began before each other's commit both insert (write skew). With the
            // key lock held, any live entry (deleter stamp zero) is either
            // committed or our own: uncommitted others are excluded by the lock,
            // and aborted writers' entries were physically reverted by rollback.
            InsertCore(storageTransaction, key.Encoded.Span, entryReference, transaction.Sequence.Value, 0,
                IsUnique, pruneBound, cancellationToken);
        }
        finally
        {
            _latch.ExitWrite();
        }
    }

    /// <summary>
    /// Deletes a key → entry-reference mapping: tombstones the reference's live entry under the
    /// key with the transaction's sequence. A reference with no live entry under the key is a
    /// no-op.
    /// </summary>
    /// <remarks>
    /// The caller's write locks decide the record version the reference names, and the caller
    /// has checked that version's stamps under them, so the version's writer is decided,
    /// committed or the caller itself, and the reference's live entry is that version's.
    /// KeyValuePair's key lock and the Documents and Graph database writer lock exclude every
    /// other writer of the version outright. Sql takes no row lock on a version it creates, so
    /// its row lock does not exclude a version's creator: a Sql statement deletes rows it found
    /// through its snapshot, whose writers are committed or itself, and rows a cascade found
    /// below them, whose writers the parent-row lock of its referential protocol decides. The
    /// one case that lock misses, a parent version a committed transaction has since replaced,
    /// fails the statement before any row found below that version is deleted (the cascade's
    /// snapshot check, or the phase-two latest-version check on the stale version). The match
    /// therefore reads the entry's stamps, not the transaction's snapshot: a write may delete a
    /// version its snapshot does not see, and a match through that snapshot finds nothing and
    /// leaves the deleted version's entry live, which a unique index then reports as a duplicate
    /// of every later insert of the key. A Sql cascade at <c>Snapshot</c> isolation did exactly
    /// that until #1370 (it now fails first-updater-wins before it deletes such a version).
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

        _latch.EnterWrite();
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
                using var writable = _storage.OpenPageForWrite(storageTransaction, (PageId)leafId, _latch);
                var writableNode = OpenNode(writable.Page.AsBodySpan(), leafId);
                writableNode.SetDeleter(index, transaction.Sequence.Value);
                writable.MarkDirty();
            }
        }
        finally
        {
            _latch.ExitWrite();
        }
    }

    /// <summary>
    /// Opens a cursor over the specified key range, positioned before the first match.
    /// </summary>
    /// <remarks>
    /// Entries with equal keys are returned in entry-reference order (reversed when
    /// <paramref name="reverse"/> is true), with writer stamps breaking reference ties.
    /// Opening reads no pages. Keep the transaction alive throughout enumeration and
    /// collect mutation targets before applying writes; see <see cref="BTreeCursor"/>.
    /// </remarks>
    /// <param name="transaction">The transaction whose snapshot reads resolve through.</param>
    /// <param name="range">The key range to scan.</param>
    /// <param name="reverse">Whether to scan in descending key order.</param>
    /// <param name="cancellationToken">Cancellation token for opening and reading the cursor.</param>
    /// <returns>A cursor over the visible entries in the range.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    public BTreeCursor OpenCursor(TransactionContext transaction, IndexKeyRange range, bool reverse = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        cancellationToken.ThrowIfCancellationRequested();
        return OpenCursor(transaction.Snapshot, range, reverse, cancellationToken);
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
    /// <paramref name="reverse"/> is true), with writer stamps breaking reference ties.
    /// Opening reads no pages. The caller retains the snapshot pin throughout enumeration;
    /// see <see cref="BTreeCursor"/> for mutation and lifetime requirements.
    /// </remarks>
    /// <param name="snapshot">The visibility snapshot entries filter through.</param>
    /// <param name="range">The key range to scan.</param>
    /// <param name="reverse">Whether to scan in descending key order.</param>
    /// <param name="cancellationToken">Cancellation token for opening and reading the cursor.</param>
    /// <returns>A cursor over the visible entries in the range.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    public BTreeCursor OpenCursor(TransactionSnapshot snapshot, IndexKeyRange range, bool reverse = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        return new BTreeCursor(this, snapshot, range, reverse, cancellationToken);
    }

    /// <summary>Locks a unique key and finds its live entry in the latest index state.</summary>
    /// <remarks>
    /// Acquires the key's Exclusive entry lock before taking the tree's read latch. The lock
    /// belongs to the transaction until commit or rollback, keeping the result stable for its
    /// decision. Call before entering a physical apply bracket; pre-held locks are re-granted.
    /// No snapshot filter is applied: a live entry is committed or belongs to this transaction.
    /// </remarks>
    /// <param name="transaction">The transaction that owns the key lock.</param>
    /// <param name="key">The complete unique index key.</param>
    /// <param name="cancellationToken">Cancellation token for lock acquisition.</param>
    /// <returns>The live entry reference, or null if no live entry exists.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The index is not unique.</exception>
    /// <exception cref="OperationCanceledException">Lock acquisition is canceled.</exception>
    /// <exception cref="TransactionDeadlockException">The key lock closes a wait cycle.</exception>
    /// <exception cref="IndexCorruptionException">A tree page is damaged.</exception>
    public async ValueTask<ulong?> FindLiveEntryAsync(TransactionContext transaction, IndexKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsUnique) { throw new InvalidOperationException(ErrorMessages.LiveLookupRequiresUnique); }
        if (_lockManager is not null)
        {
            await _lockManager.AcquireAsync(transaction.Sequence, LockResource.Entry(_objectId, key.Hash()),
                LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
        }
        _latch.EnterRead();
        try
        {
            if (!TryFindEntry(BTreeSearchKey.AtKey(key.Encoded.Span), EntryMatch.Live, out long leafId, out int index)) { return null; }
            using var handle = _storage.PageManager.GetPage((PageId)leafId);
            return OpenNode(handle.Page.AsBodySpan(), leafId).GetEntryReference(index);
        }
        finally { _latch.ExitRead(); }
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

        if (key.Length > MaxKeyLength)
        {
            throw new IndexException($"Index key of {key.Length} bytes exceeds the {MaxKeyLength}-byte maximum.");
        }

        _latch.EnterWrite();
        try
        {
            InsertCore(transaction, key.Encoded.Span, entryReference, writer.Value, deleter.Value);
        }
        finally
        {
            _latch.ExitWrite();
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

        _latch.EnterWrite();
        try
        {
            // The full identity is known: the descent lands on the entry's leaf.
            if (TryFindEntry(
                BTreeSearchKey.AtEntry(key.Encoded.Span, entryReference, writer.Value),
                EntryMatch.WrittenBy(entryReference, writer.Value),
                out long leafId,
                out int index))
            {
                using var writable = _storage.OpenPageForWrite(transaction, (PageId)leafId, _latch);
                var writableNode = OpenNode(writable.Page.AsBodySpan(), leafId);
                writableNode.RemoveLeafEntry(index);
                writable.MarkDirty();
            }
        }
        finally
        {
            _latch.ExitWrite();
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

        _latch.EnterWrite();
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
                using var writable = _storage.OpenPageForWrite(transaction, (PageId)leafId, _latch);
                var writableNode = OpenNode(writable.Page.AsBodySpan(), leafId);
                writableNode.SetDeleter(index, 0);
                writable.MarkDirty();
            }
        }
        finally
        {
            _latch.ExitWrite();
        }

        return default;
    }

    internal (long Removed, bool Completed) PruneLeaf(TransactionSequence bound, CancellationToken cancellationToken)
    {
        StorageTransaction? transaction = null;
        long removed = 0;
        long nextLeaf = -1;
        try
        {
            _latch.EnterWrite();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                long leafId = _nextPruneLeaf < 0 ? DescendToLeftmostLeaf() : _nextPruneLeaf;
                using var handle = _storage.PageManager.GetPage((PageId)leafId);
                var node = OpenNode(handle.Page.AsBodySpan(), leafId);
                nextLeaf = node.NextLeaf;
                StoragePageHandle? writable = null;
                try
                {
                    for (int index = 0; index < node.EntryCount;)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ulong deleter = node.GetDeleter(index);
                        if (deleter == 0 || deleter >= bound.Value) { index++; continue; }
                        if (writable is null)
                        {
                            transaction = _storage.BeginTransaction();
                            writable = _storage.OpenPageForWrite(transaction, (PageId)leafId, _latch);
                            node = OpenNode(writable.Page.AsBodySpan(), leafId);
                        }
                        node.RemoveLeafEntry(index);
                        removed++;
                    }
                    writable?.MarkDirty();
                }
                finally { writable?.Dispose(); }
            }
            finally { _latch.ExitWrite(); }

            // Publish continuation only after the page batch commits. A failed/cancelled batch
            // restores under the enlisted tree latch and retries the same leaf next time.
            transaction?.Commit();
            _nextPruneLeaf = nextLeaf;
            return (removed, nextLeaf < 0);
        }
        finally { transaction?.Dispose(); }
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

        _latch.EnterWrite();
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
                                writable = _storage.OpenPageForWrite(transaction, (PageId)leafId, _latch);
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
            _latch.ExitWrite();
        }

        return purged;
    }

    /// <summary>Counts a range without retaining keys, subject to physical work limits.</summary>
    /// <param name="snapshot">Visibility of the caller's pinned statement.</param>
    /// <param name="range">Key bounds.</param>
    /// <param name="entryLimit">Maximum physical entries to examine, including invisible entries.</param>
    /// <param name="pageLimit">Maximum page visits, including the descent and empty leaves.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An exact count or an explicitly incomplete lower bound.</returns>
    public BTreeRangeCount CountRange(TransactionSnapshot snapshot, IndexKeyRange range,
        int entryLimit = 512, int pageLimit = 32, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entryLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageLimit, 1);
        _latch.EnterRead(cancellationToken);
        try
        {
            long count = 0;
            int entries = 0, pages = 0;
            long current = RootPageId;
            var lower = range.Start;
            var position = lower is { } start
                ? range.IsStartInclusive ? BTreeSearchKey.AtKey(start.Encoded.Span) : BTreeSearchKey.AfterKey(start.Encoded.Span)
                : BTreeSearchKey.AtKey(default);
            bool descending = true, firstLeaf = true;
            while (current >= 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pages == pageLimit) { return new(count, false, entries, pages); }
                using var handle = _storage.PageManager.GetPage((PageId)current);
                pages++;
                var node = OpenNode(handle.Page.AsBodySpan(), current);
                if (descending && !node.IsLeaf)
                {
                    current = node.GetChildAt(lower.HasValue ? node.FindChildSlot(position) : -1);
                    continue;
                }
                descending = false;
                int at = firstLeaf && lower.HasValue ? node.FindLowerBound(position) : 0;
                firstLeaf = false;
                for (; at < node.EntryCount; at++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entries == entryLimit) { return new(count, false, entries, pages); }
                    entries++;
                    if (range.End is { } end)
                    {
                        int comparison = node.GetKey(at).SequenceCompareTo(end.Encoded.Span);
                        if (comparison > 0 || comparison == 0 && !range.IsEndInclusive)
                        { return new(count, true, entries, pages); }
                    }
                    if (IsEntryVisible(node, at, snapshot)) { count++; }
                }
                current = node.NextLeaf;
            }
            return new(count, true, entries, pages);
        }
        finally { _latch.ExitRead(); }
    }

    /// <summary>
    /// Copies a bounded batch, re-descending strictly past the last examined identity.
    /// Invisible entries consume the examination limit; no physical position survives a call.
    /// </summary>
    internal void ReadCursorBatch(BTreeCursor cursor, CancellationToken cancellationToken)
    {
        // Order: transaction/key locks (if any) -> tree latch -> page pin/pool monitor.
        // Never acquire a transaction lock or await under the tree latch. Rollback takes
        // the same latch exclusively; all page handles leave before the shared hold does.
        _latch.EnterRead(cancellationToken);
        try
        {
            bool reverse = cursor.Reverse;
            var range = cursor.Range;
            var continuation = cursor.Continuation;
            var boundary = cursor.Boundary;
            var bound = reverse ? range.End : range.Start;
            ReadOnlySpan<byte> boundKey = bound.HasValue ? bound.Value.Encoded.Span : default;
            var position = boundary is not null ? boundary.AsSearchKey() : continuation is { } last
                ? BTreeSearchKey.AtEntry(last.Key, last.EntryReference, last.Writer)
                : (reverse ? range.IsEndInclusive : !range.IsStartInclusive)
                    ? BTreeSearchKey.AfterKey(boundKey) : BTreeSearchKey.AtKey(boundKey);
            long leafId = DescendCursor(position, bound.HasValue || continuation.HasValue || boundary is not null,
                reverse, cursor, cancellationToken, out var leafBoundary);
            int examined = 0;
            bool firstLeaf = true;

            while (leafId >= 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var handle = _storage.PageManager.GetPage((PageId)leafId);
                cursor.PagesVisited++;
                var node = OpenNode(handle.Page.AsBodySpan(), leafId);
                int index = reverse ? node.EntryCount - 1 : 0;
                if (firstLeaf && (bound.HasValue || continuation.HasValue || boundary is not null))
                {
                    index = node.FindLowerBound(position);
                    if (reverse) { index--; }
                    else if (boundary is null && continuation.HasValue && index < node.EntryCount && node.CompareToEntry(position, index) == 0) { index++; }
                }
                if (index < 0 || index >= node.EntryCount)
                {
                    // Undo can leave arbitrarily long chains of empty leaves. Yield after
                    // one empty leaf. A separator is a logical fence even after its source
                    // entry is erased: resume at it, never at a retained physical page id.
                    // On a later leaf we have made identity progress; the next descent can
                    // move past that exhausted leaf's fence before visiting the empty chain.
                    if (firstLeaf)
                    {
                        cursor.Continuation = null;
                        if (leafBoundary is { } fence)
                        {
                            using var fencePage = _storage.PageManager.GetPage((PageId)fence.PageId);
                            cursor.PagesVisited++;
                            cursor.Boundary = Separator.FromNode(OpenNode(fencePage.Page.AsBodySpan(), fence.PageId), fence.Slot);
                        }
                        else { cursor.Boundary = null; }
                        cursor.Completed = leafBoundary is null;
                    }
                    return;
                }
                firstLeaf = false;
                int lastExamined = -1;
                bool lastVisible = false;
                for (; index >= 0 && index < node.EntryCount; index += reverse ? -1 : 1)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = node.GetKey(index);
                    var stop = reverse ? range.Start : range.End;
                    if (stop.HasValue)
                    {
                        int comparison = key.SequenceCompareTo(stop.Value.Encoded.Span);
                        if ((reverse ? comparison < 0 : comparison > 0) ||
                            (comparison == 0 && !(reverse ? range.IsStartInclusive : range.IsEndInclusive)))
                        {
                            cursor.Completed = true;
                            return;
                        }
                    }
                    cursor.EntriesExamined++;
                    examined++;
                    bool visible = IsEntryVisible(node, index, cursor.Snapshot);
                    lastExamined = index;
                    lastVisible = visible;
                    if (visible || examined == BTreeCursor.ExaminationLimit)
                    {
                        // Own the key before releasing the pin. Keep an invisible identity
                        // only at the work boundary, so long dead runs allocate per batch.
                        var entry = new BTreeCursor.Entry(key.ToArray(), node.GetEntryReference(index), node.GetWriter(index));
                        cursor.Continuation = entry;
                        cursor.Boundary = null;
                        if (visible) { cursor.Entries[cursor.Count++] = entry; }
                    }
                    if (cursor.Count == (cursor.BatchSizeOverride > 0 ? cursor.BatchSizeOverride : cursor.ReadLimit) ||
                        examined == BTreeCursor.ExaminationLimit) { return; }
                }
                if (lastExamined >= 0 && !lastVisible)
                {
                    cursor.Continuation = new BTreeCursor.Entry(node.GetKey(lastExamined).ToArray(),
                        node.GetEntryReference(lastExamined), node.GetWriter(lastExamined));
                    cursor.Boundary = null;
                }
                leafId = reverse ? node.PrevLeaf : node.NextLeaf;
            }
            cursor.Completed = true;
        }
        finally { _latch.ExitRead(); }
    }

    private long DescendCursor(in BTreeSearchKey position, bool bounded, bool reverse, BTreeCursor cursor,
        CancellationToken cancellationToken, out (long PageId, int Slot)? leafBoundary)
    {
        long current = RootPageId;
        leafBoundary = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var handle = _storage.PageManager.GetPage((PageId)current);
            cursor.PagesVisited++;
            var node = OpenNode(handle.Page.AsBodySpan(), current);
            if (node.IsLeaf) { return current; }
            int slot = bounded ? node.FindChildSlot(position) : reverse ? node.EntryCount - 1 : -1;
            // Reverse positions are exclusive (an inclusive upper key uses +inf).
            // Equality with a separator therefore belongs in its left child.
            if (bounded && reverse && slot >= 0 && node.CompareToSeparator(position, slot) == 0) { slot--; }
            int fence = reverse ? slot : slot + 1;
            if (fence >= 0 && fence < node.EntryCount) { leafBoundary = (current, fence); }
            current = node.GetChildAt(slot);
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

    private int CheckUniqueAndPrune(StorageTransaction transaction, in BTreeSearchKey position, long leafId,
        TransactionSequence bound, CancellationToken cancellationToken)
    {
        var first = CheckUniqueLeaf(transaction, position, leafId, bound, cancellationToken, insertionLeaf: true);
        long previous = first.Previous;
        bool more = first.Left;
        while (more && previous >= 0)
        {
            var current = CheckUniqueLeaf(transaction, position, previous, bound, cancellationToken, insertionLeaf: false);
            previous = current.Previous;
            more = current.Left;
        }
        long next = first.Next;
        more = first.Right;
        while (more && next >= 0)
        {
            var current = CheckUniqueLeaf(transaction, position, next, bound, cancellationToken, insertionLeaf: false);
            next = current.Next;
            more = current.Right;
        }
        return first.InsertAt;
    }

    private (int InsertAt, long Previous, long Next, bool Left, bool Right) CheckUniqueLeaf(StorageTransaction transaction,
        in BTreeSearchKey position, long leafId, TransactionSequence bound, CancellationToken cancellationToken, bool insertionLeaf)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var handle = _storage.PageManager.GetPage((PageId)leafId);
        var node = OpenNode(handle.Page.AsBodySpan(), leafId);
        long previous = node.PrevLeaf;
        long next = node.NextLeaf;
        // Decide continuation from the original bounds, before pruning can empty a leaf.
        bool left = node.EntryCount == 0 || node.GetKey(0).SequenceCompareTo(position.Key) >= 0;
        bool right = node.EntryCount == 0 || node.GetKey(node.EntryCount - 1).SequenceCompareTo(position.Key) <= 0;
        int at = insertionLeaf ? node.FindLowerBound(position) : node.FindLowerBound(BTreeSearchKey.AtKey(position.Key));
        int first = at;
        if (insertionLeaf)
        {
            while (first > 0 && node.GetKey(first - 1).SequenceEqual(position.Key))
            {
                cancellationToken.ThrowIfCancellationRequested();
                first--;
            }
        }
        StoragePageHandle? writable = null;
        try
        {
            for (int index = first; index < node.EntryCount && node.GetKey(index).SequenceEqual(position.Key);)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ulong deleter = node.GetDeleter(index);
                if (deleter == 0) { throw new IndexUniqueViolationException(Name, new IndexKey(position.Key.ToArray())); }
                if (deleter >= bound.Value) { index++; continue; }
                if (writable is null)
                {
                    writable = _storage.OpenPageForWrite(transaction, (PageId)leafId, _latch);
                    node = OpenNode(writable.Page.AsBodySpan(), leafId);
                }
                node.RemoveLeafEntry(index);
                if (index < at) { at--; }
            }
        }
        finally
        {
            writable?.MarkDirty();
            writable?.Dispose();
        }
        return (at, previous, next, left, right);
    }

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

    private void InsertCore(StorageTransaction transaction, ReadOnlySpan<byte> key, ulong entryReference, ulong writer, ulong deleter,
        bool unique = false, TransactionSequence pruneBound = default, CancellationToken cancellationToken = default)
    {
        var position = BTreeSearchKey.AtEntry(key, entryReference, writer);
        var path = new List<PathEntry>();
        long leafId = Descend(position, path);
        int? cachedAt = unique ? CheckUniqueAndPrune(transaction, position, leafId, pruneBound, cancellationToken) : null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                at = cachedAt ?? node.FindLowerBound(position);
                cachedAt = null;
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
                using var writable = _storage.OpenPageForWrite(transaction, (PageId)leafId, _latch);
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

            leafId = SplitLeaf(transaction, path, leafId, at, position);
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
        DescentStarted?.Invoke();
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

    private long SplitLeaf(StorageTransaction transaction, List<PathEntry> parentPath, long leafId, int at, in BTreeSearchKey position)
    {
        Separator separator;
        long siblingId;
        int count;

        using (var leafHandle = _storage.OpenPageForWrite(transaction, (PageId)leafId, _latch))
        using (var siblingHandle = _storage.AllocatePageForWrite(transaction, PageType.Index, _latch, _objectId))
        {
            var leaf = OpenNode(leafHandle.Page.AsBodySpan(), leafId);
            count = leaf.EntryCount;

            if (count < 2)
            {
                // Unreachable while inserts compact orphaned bytes first: a compacted
                // leaf with fewer than two entries always has room for one more.
                throw InvariantViolated(leafId, $"Index '{Name}' cannot split leaf page {leafId} holding {count} entries.");
            }

            bool endOfRun = at == count && leaf.GetKey(0).SequenceEqual(position.Key)
                && (leaf.NextLeaf < 0 || IsEndOfRun(parentPath, position.Key));
            int mid = ChooseLeafSplit(leaf, at, endOfRun);
            separator = BTreeEntryOrder.BuildSeparator(leaf, mid - 1, mid);
            EnsureLeafSeparator(leaf, leafId, mid, separator);

            var sibling = BTreeNode.Initialize(siblingHandle.Page.AsBodySpan(), BTreeNode.LeafKind);
            siblingId = (long)siblingHandle.Id;

            // Move the entries after the split point to the sibling.
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
                using var oldNextHandle = _storage.OpenPageForWrite(transaction, (PageId)oldNext, _latch);
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

        long leftId = InsertIntoParent(transaction, parentPath, leafId, separator, siblingId);
        int comparison = position.Key.SequenceCompareTo(separator.Key);
        if (comparison == 0)
        {
            comparison = BTreeEntryOrder.CompareTiebreakers(position, separator.Tiebreaker, separator.EntryReference, separator.Writer);
        }
        if (comparison < 0) { return leftId; }
        var parent = parentPath[^1];
        parentPath[^1] = new PathEntry(parent.PageId, parent.Slot + 1);
        return siblingId;
    }

    /// <summary>
    /// Chooses where a full leaf splits: the number of entries that stay on the left.
    /// Appends to the tree, or to a leaf containing only the incoming key whose
    /// upper fence proves its duplicate run ends here, keep the last existing entry
    /// on the right to supply that side of the separator. Since the insertion slot
    /// is after every entry, equality with the first key proves the whole page,
    /// including the new item, has one value. Mixed-key leaves retain the midpoint
    /// unless this is a rightmost append. This adapts PostgreSQL's rightmost and
    /// single-value strategies (<c>nbtsplitloc.c:287-292,971,987-1033</c>): its
    /// single-value strategy also requires the whole page, including the new item,
    /// to contain duplicates. Both existing halves must be nonempty; the incoming
    /// entry follows on re-descent.
    /// </summary>
    private static int ChooseLeafSplit(in BTreeNode leaf, int at, bool endOfRun)
    {
        int count = leaf.EntryCount;
        if (!endOfRun && !(at == count && leaf.NextLeaf < 0))
        {
            return count / 2;
        }

        return count - 1;
    }

    /// <summary>
    /// Uses the nearest upper separator as the leaf's high key. A strictly greater
    /// key proves this is the last leaf of the incoming key's run; an equal key with
    /// a reference or writer tiebreaker means the run can continue to the right.
    /// Retained separators remain valid fences after erase/purge, including when
    /// every entry in a neighbouring leaf has gone. No sibling-chain scan is needed.
    /// </summary>
    private bool IsEndOfRun(List<PathEntry> path, ReadOnlySpan<byte> key)
    {
        for (int i = path.Count - 1; i >= 0; i--)
        {
            var step = path[i];
            using var handle = _storage.PageManager.GetPage((PageId)step.PageId);
            var node = OpenNode(handle.Page.AsBodySpan(), step.PageId);
            int next = step.Slot + 1;
            if (next < node.EntryCount)
            {
                return key.SequenceCompareTo(node.GetKey(next)) < 0;
            }
        }

        return true; // No upper fence: the tree's rightmost leaf.
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
            long leftId = GrowRoot(transaction, separator, childId);
            parentPath.Add(new PathEntry(RootPageId, -1));
            return leftId;
        }

        var (parentId, splitSlot) = parentPath[^1];

        // Child c(p) sits in slot p - 1 (c(0) is the leftmost child), so the new
        // separator goes to directory index p and its child becomes c(p + 1).
        int position = splitSlot + 1;

        using (var parentHandle = _storage.OpenPageForWrite(transaction, (PageId)parentId, _latch))
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

        // SplitInternal leaves the repaired ancestry of the parent's lower half. Moving
        // right changes only its last child slot; append the relocated parent/child slot.
        if (position > mid)
        {
            var ancestor = parentPath[^1];
            parentPath[^1] = new PathEntry(ancestor.PageId, ancestor.Slot + 1);
        }
        parentPath.Add(new PathEntry(target, targetPosition - 1));

        using var targetHandle = _storage.OpenPageForWrite(transaction, (PageId)target, _latch);
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

        using (var rootHandle = _storage.OpenPageForWrite(transaction, (PageId)RootPageId, _latch))
        using (var leftHandle = _storage.AllocatePageForWrite(transaction, PageType.Index, _latch, _objectId))
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
                    using var previousHandle = _storage.OpenPageForWrite(transaction, (PageId)left.PrevLeaf, _latch);
                    OpenNode(previousHandle.Page.AsBodySpan(), left.PrevLeaf).NextLeaf = leftId;
                    previousHandle.MarkDirty();
                }

                if (left.NextLeaf >= 0)
                {
                    using var nextHandle = _storage.OpenPageForWrite(transaction, (PageId)left.NextLeaf, _latch);
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

        using (var nodeHandle = _storage.OpenPageForWrite(transaction, (PageId)nodeId, _latch))
        using (var siblingHandle = _storage.AllocatePageForWrite(transaction, PageType.Index, _latch, _objectId))
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

        var ancestors = path.GetRange(0, path.Count - 1);
        long leftId = InsertIntoParent(transaction, ancestors, nodeId, promoted, siblingId);
        path.Clear();
        path.AddRange(ancestors);
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
