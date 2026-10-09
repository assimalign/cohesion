using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Indexing.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Indexing;

/// <summary>
/// Creates, opens, and drops the B+Tree indexes belonging to one logical database, over the
/// pages of one storage instance, and exposes their physical registrations so model catalogs
/// can persist them.
/// </summary>
/// <remarks>
/// <para>
/// The manager is an in-memory directory of live indexes. Index-directory persistence
/// deliberately belongs to the model catalog, not this project: the catalog owns schema
/// metadata and its transactional DDL apply, and the index manager stays a purely physical
/// component. The catalog exports the registrations (<see cref="ExportRegistrations"/>) and
/// re-attaches them on open through <see cref="BTreeIndexManagerOptions.ExistingIndexes"/>. A
/// tree's root page stays fixed through splits (#1159), so a registration changes only through
/// index DDL; catalogs' re-export at their persistence points is a backstop.
/// </para>
/// <para>
/// One sealed type since #1258: it absorbed the static factory of the same name, the
/// <c>IIndexManager</c> and <c>IIndexRegistry</c> interfaces, and their one internal
/// implementation, so catalogs no longer cast the manager to its registry.
/// </para>
/// </remarks>
public sealed class BTreeIndexManager
{
    private readonly BTreeIndexManagerOptions _options;
    private readonly Dictionary<(ulong ObjectId, string Name), BTreeIndex> _indexes = new();
    private readonly object _sync = new();

    private BTreeIndexManager(BTreeIndexManagerOptions options)
    {
        _options = options;

        if (options.ExistingIndexes is not null)
        {
            // The open-time format check: every tree's root page is read once, before
            // any tree is attached, so a database holding a tree of another page
            // format is refused whole instead of misread (no upgrade path, #1152).
            EnsureFormat(options.Storage, options.ExistingIndexes);

            foreach (var registration in options.ExistingIndexes)
            {
                _indexes[(registration.ObjectId, registration.Definition.Name)] = new BTreeIndex(
                    options.Storage,
                    options.TransactionSource,
                    options.LockManager,
                    registration.ObjectId,
                    registration.Definition,
                    registration.RootPageId);
            }
        }
    }

    /// <summary>
    /// Gets the B-tree page format this engine writes and the only one it reads.
    /// Format 2 (#1194) orders entries by <c>(key, entry reference, writer)</c> and
    /// stamps a magic and this version on every node page; format 1 ordered entries by
    /// key alone and carried no stamp.
    /// </summary>
    /// <remarks>
    /// A property, not a constant: the value changes with every page-format change, and
    /// a constant would be compiled into separately built consumers, which would then
    /// report the format of the engine they were built against.
    /// </remarks>
    public static int FormatVersion => BTreeNode.FormatVersion;

    /// <summary>
    /// Creates an index manager over the specified storage, attaching the trees in
    /// <see cref="BTreeIndexManagerOptions.ExistingIndexes"/>.
    /// </summary>
    /// <param name="options">The composition options.</param>
    /// <returns>The index manager.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="IndexFormatException">
    /// An existing tree's root page is not in page format <see cref="FormatVersion"/>;
    /// no tree is attached.
    /// </exception>
    public static BTreeIndexManager Create(BTreeIndexManagerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new BTreeIndexManager(options);
    }

    /// <summary>
    /// Checks that every tree in <paramref name="registrations"/> is in page format
    /// <see cref="FormatVersion"/> — the check <see cref="Create"/> makes when it
    /// attaches existing trees, on its own. It reads each tree's root page and writes
    /// nothing, so a model whose open recovers its record space before it attaches its
    /// indexes calls it first, and refuses a database it cannot read before recovery
    /// writes to it.
    /// </summary>
    /// <param name="storage">The storage whose pages hold the trees.</param>
    /// <param name="registrations">The registrations of the trees to check.</param>
    /// <exception cref="IndexFormatException">A tree's root page is not in page format <see cref="FormatVersion"/>.</exception>
    public static void EnsureFormat(Storage.Storage storage, IEnumerable<BTreeIndexRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(registrations);

        foreach (var registration in registrations)
        {
            BTreeIndex.EnsureFormat(storage, registration);
        }
    }

    /// <summary>
    /// Creates a new index on the specified object.
    /// </summary>
    /// <param name="transaction">The transaction the DDL operation belongs to.</param>
    /// <param name="objectId">The identity of the object (table, collection, container) being indexed.</param>
    /// <param name="definition">The definition of the index to create.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The created index.</returns>
    /// <exception cref="IndexException">Thrown when an index with the same name already exists on the object.</exception>
    public ValueTask<BTreeIndex> CreateIndexAsync(TransactionContext transaction, ulong objectId, IndexDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
        cancellationToken.ThrowIfCancellationRequested();

        if (definition.Kind != IndexKind.BTree)
        {
            throw new IndexException($"Index kind {definition.Kind} is not supported yet; only BTree indexes exist in the MVP.");
        }

        var storageTransaction = _options.TransactionSource(transaction);
        BTreeIndex index;

        lock (_sync)
        {
            var key = (objectId, definition.Name);

            if (_indexes.ContainsKey(key))
            {
                throw new IndexException($"An index named '{definition.Name}' already exists on object {objectId}.");
            }

            long rootPageId = BTreeIndex.CreateRoot(_options.Storage, storageTransaction);
            index = new BTreeIndex(
                _options.Storage, _options.TransactionSource, _options.LockManager, objectId, definition, rootPageId);
            _indexes[key] = index;
        }

        IndexEventSource.Log.IndexCreated(_options.Storage, objectId, definition);
        return new ValueTask<BTreeIndex>(index);
    }

    /// <summary>
    /// Drops an index from the specified object.
    /// </summary>
    /// <param name="transaction">The transaction the DDL operation belongs to.</param>
    /// <param name="objectId">The identity of the object the index belongs to.</param>
    /// <param name="name">The name of the index to drop.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task representing the drop.</returns>
    /// <exception cref="IndexException">Thrown when the index does not exist.</exception>
    public ValueTask DropIndexAsync(TransactionContext transaction, ulong objectId, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (!_indexes.Remove((objectId, name)))
            {
                throw new IndexException($"No index named '{name}' exists on object {objectId}.");
            }
        }

        IndexEventSource.Log.IndexDropped(_options.Storage, objectId, name);

        // The tree's pages are left for the vacuum feature to reclaim — dropping is
        // a directory operation, not a physical walk.
        return default;
    }

    /// <summary>
    /// Opens an existing index by name.
    /// </summary>
    /// <param name="objectId">The identity of the object the index belongs to.</param>
    /// <param name="name">The name of the index.</param>
    /// <param name="index">When this method returns true, the opened index.</param>
    /// <returns>True when the index exists; otherwise false.</returns>
    public bool TryGetIndex(ulong objectId, string name, out BTreeIndex index)
    {
        lock (_sync)
        {
            if (_indexes.TryGetValue((objectId, name), out var found))
            {
                index = found;
                return true;
            }
        }

        index = null!;
        return false;
    }

    /// <summary>
    /// Enumerates the indexes defined on the specified object.
    /// </summary>
    /// <param name="objectId">The identity of the object.</param>
    /// <returns>The indexes defined on the object.</returns>
    public IReadOnlyList<BTreeIndex> GetIndexes(ulong objectId)
    {
        lock (_sync)
        {
            return _indexes.Where(pair => pair.Key.ObjectId == objectId).Select(pair => pair.Value).ToList();
        }
    }

    /// <summary>
    /// Purges the given writers' stamps out of every live index in one walk per
    /// tree: entries the writers inserted are physically removed and tombstones
    /// they stamped are cleared. This is the open-time recovery obligation — the
    /// journal cannot prove these writers committed, the in-memory undo ledger
    /// died with the process, and snapshots have no commit-log awareness, so an
    /// unproven writer's stamps must not remain visible-by-default in any index.
    /// Idempotent: re-running after a crash mid-purge removes what remains.
    /// </summary>
    /// <param name="transaction">The physical storage bracket the purge rides.</param>
    /// <param name="writers">The transaction sequences the journal cannot prove committed.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The number of entries removed or restored.</returns>
    public ValueTask<long> PurgeWritersAsync(StorageTransaction transaction, IReadOnlySet<TransactionSequence> writers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(writers);

        if (writers.Count == 0)
        {
            return new ValueTask<long>(0L);
        }

        // Timed only while a listener takes the purge event (event-source.md, rule 9).
        bool timed = IndexEventSource.Log.IsEnabled(EventLevel.Verbose, EventKeywords.None);
        long started = timed ? Stopwatch.GetTimestamp() : 0;

        List<BTreeIndex> indexes;
        lock (_sync)
        {
            indexes = _indexes.Values.ToList();
        }

        long purged = 0;

        foreach (var index in indexes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            purged += index.PurgeWriters(transaction, writers);
        }

        if (timed)
        {
            IndexEventSource.Log.WritersPurged(_options.Storage, writers.Count, purged, started);
        }

        return new ValueTask<long>(purged);
    }

    /// <summary>
    /// Gets the current registrations of every live index.
    /// </summary>
    /// <returns>The registrations, one per index.</returns>
    public IReadOnlyList<BTreeIndexRegistration> ExportRegistrations()
    {
        lock (_sync)
        {
            return _indexes
                .Select(pair => new BTreeIndexRegistration(
                    pair.Key.ObjectId,
                    new IndexDefinition(pair.Value.Name, pair.Value.Kind, pair.Value.IsUnique),
                    pair.Value.RootPageId))
                .ToList();
        }
    }
}
