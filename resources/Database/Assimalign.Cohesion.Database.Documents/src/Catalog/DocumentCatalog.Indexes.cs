using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Storage;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Transactions;

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Catalog;

public sealed partial class DocumentCatalog
{
    private readonly Dictionary<(Guid CollectionId, string Name), Versions> _indexDefinitions = new();
    private readonly Dictionary<ulong, (PageId Page, int Slot, long Root)> _registrations = new();
    private readonly Dictionary<ulong, bool> _uniqueIndexes = new();
    private BTreeIndexManager _indexes = null!;

    // Instance-local fault/cancellation seam; each callback observes a completed
    // posting application inside the still-uncommitted physical bracket.
    internal Action<int>? AfterIndexPostingApplied { get; set; }

    // Instance-local work observations for bounded build staging and lock-order tests.
    internal Action<int>? IndexBuildPostingsBuffered { get; set; }
    internal Action<bool>? BeforeIndexKeysRead { get; set; }

    /// <summary>Lists visible index definitions in ordinal name order.</summary>
    /// <param name="collectionId">The collection identity.</param>
    /// <param name="snapshot">The visibility snapshot.</param>
    /// <returns>The visible definitions.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="DocumentCatalogException">A persisted catalog record is malformed.</exception>
    public IReadOnlyList<DocumentIndexMetadata> GetIndexes(Guid collectionId, TransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            var result = new List<DocumentIndexMetadata>();
            foreach (var key in _indexDefinitions.Keys.Where(key => key.CollectionId == collectionId)
                .OrderBy(key => key.Name, StringComparer.Ordinal).ToArray())
            {
                if (FindIndex(key.CollectionId, key.Name, snapshot) is { } found)
                {
                    result.Add(found.Record.Index!.Value);
                }
            }
            return result;
        }
    }

    /// <summary>Builds a nonunique B+Tree and publishes its definition in the supplied transaction.</summary>
    /// <param name="collectionId">The collection identity.</param>
    /// <param name="name">The new index name.</param>
    /// <param name="path">The document field path.</param>
    /// <param name="context">The active transaction; the caller holds the database writer lock.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The new index definition.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or white space, or <paramref name="path"/> is not a document field path.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="context"/> is not active.</exception>
    /// <exception cref="DocumentCatalogException">
    /// The collection is absent, the index already exists, or an existing document's indexed scalar
    /// encodes beyond the index key limit, or the encoded definition exceeds 8,092 bytes (CD03026).
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the build applied.</exception>
    /// <exception cref="TransactionAbortedException">The transaction ended, or its end began, before the build was applied.</exception>
    public async ValueTask<DocumentIndexMetadata> CreateIndexAsync(Guid collectionId, string name, string path,
        TransactionContext context, CancellationToken cancellationToken = default)
        => await CreateIndexAsync(collectionId, name, [path], false, context, cancellationToken).ConfigureAwait(false);

    /// <summary>Builds a composite index and checks valued scalar uniqueness before publication.</summary>
    /// <param name="collectionId">The collection identity.</param>
    /// <param name="name">The new ordinal index name.</param>
    /// <param name="paths">One to 32 ordered document paths.</param>
    /// <param name="isUnique">Whether keys containing only valued scalars must be unique.</param>
    /// <param name="context">The active transaction; the caller holds the database writer lock.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The published index definition.</returns>
    /// <exception cref="ArgumentNullException">The paths or context are null.</exception>
    /// <exception cref="ArgumentException">A name or path is invalid.</exception>
    /// <exception cref="DatabaseException">The path count is zero or exceeds 32 (CD03022).</exception>
    /// <exception cref="DocumentCatalogException">The collection is absent, the name exists, a key exceeds 1024 bytes,
    /// or the encoded definition exceeds 8,092 bytes (CD03026).</exception>
    /// <exception cref="DocumentConstraintViolationException">A UNIQUE key is duplicate or non-scalar.</exception>
    /// <exception cref="InvalidOperationException">The context is not active.</exception>
    /// <exception cref="TransactionAbortedException">The transaction ended before publication.</exception>
    /// <exception cref="OperationCanceledException">The build was canceled.</exception>
    public async ValueTask<DocumentIndexMetadata> CreateIndexAsync(Guid collectionId, string name, IReadOnlyList<string> paths,
        bool isUnique, TransactionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return await CreateIndexAsync(collectionId, name, paths.Select(DocumentIndexKey.Scalar).ToArray(),
            isUnique, false, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds a typed index with DISTINCT array postings and optional leading MISSING inclusion.</summary>
    /// <param name="collectionId">The collection identity.</param>
    /// <param name="name">The ordinal index name.</param>
    /// <param name="keys">One to 32 keys, containing at most one DISTINCT array projection.</param>
    /// <param name="isUnique">Whether scalar keys are unique; must be false for array indexes.</param>
    /// <param name="includeMissing">Whether to index a MISSING leading scalar key; must be false for a leading array key.</param>
    /// <param name="context">The active transaction; the caller holds the database writer lock.</param>
    /// <param name="cancellationToken">Cancels extraction or physical application.</param>
    /// <returns>The new index definition.</returns>
    /// <exception cref="ArgumentNullException">The keys, a key, or context are null.</exception>
    /// <exception cref="ArgumentException">The name is invalid.</exception>
    /// <exception cref="DatabaseException">The key count is outside 1..32 (CD03022).</exception>
    /// <exception cref="DocumentCatalogException">The array definition is unsupported (CD03025), a key exceeds
    /// 1024 bytes, the encoded definition exceeds 8,092 bytes (CD03026), the collection is absent, or the name exists.</exception>
    /// <exception cref="DocumentConstraintViolationException">A UNIQUE key is duplicate or non-scalar.</exception>
    /// <exception cref="InvalidOperationException">The context is not active.</exception>
    /// <exception cref="TransactionAbortedException">The transaction ended before publication.</exception>
    /// <exception cref="OperationCanceledException">The build was canceled.</exception>
    public async ValueTask<DocumentIndexMetadata> CreateIndexAsync(Guid collectionId, string name, IReadOnlyList<DocumentIndexKey> keys,
        bool isUnique, bool includeMissing, TransactionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(keys);
        EnsureActive(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();
        if (keys.Count is < 1 or > 32)
        {
            string message = ErrorMessages.IndexPathCount;
            throw new DatabaseException(message, [new Types.Diagnostic { Code = "CD03022", Message = message, Severity = Types.DiagnosticSeverity.Error }]);
        }
        // Snapshot every caller-owned list before asynchronous work begins.
        var metadata = new DocumentIndexMetadata(collectionId, name, keys, isUnique, includeMissing,
            (ulong)_storage.ReserveTransactionSequence());
        if (!GetCollections(context.Snapshot).Any(collection => collection.Id == collectionId))
        {
            throw new DocumentCatalogException("CD01002", ErrorMessages.CreateIndexAsyncDocumentCatalog);
        }
        lock (_sync)
        {
            if (FindIndex(collectionId, name, context.Snapshot) is not null)
            {
                throw new DocumentCatalogException("CD03024", string.Format(CultureInfo.CurrentCulture, ErrorMessages.IndexAlreadyExists, name));
            }
        }
        var encoded = DocumentCatalogCodec.Encode(new CatalogRecord(null, null, metadata), context.Sequence);
        if (encoded.Length > SlottedPage.MaxRecordSize)
        { throw new DocumentCatalogException("CD03026", ErrorMessages.IndexDefinitionTooLarge); }
        var documents = new List<(DocumentCatalogEntry Document, Reference Reference)>();
        lock (_sync)
        {
            foreach (var document in GetDocuments(collectionId, null, context.Snapshot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var found = FindDocumentVersion(collectionId, document.Id, context.Snapshot)!.Value;
                documents.Add((document, found.Reference));
            }
        }
        // Only scalar UNIQUE builds need collection-wide keys, to refuse duplicates
        // before creating a physical tree. Extraction never needs the catalog monitor.
        if (isUnique)
        {
            var uniqueKeys = new HashSet<IndexKey>();
            foreach (var (document, _) in documents)
            {
                foreach (var key in KeysFor(document, metadata, out bool distinct, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!distinct && !uniqueKeys.Add(key)) { throw Duplicate(metadata, context.Snapshot); }
                }
            }
        }
        var location = await ApplyAsync(context, async bracket =>
        {
            // The catalog pre-check implements NULLS DISTINCT under the writer lock;
            // physical trees remain nonunique even when registrations persist UNIQUE.
            var index = await _indexes.CreateIndexAsync(context, metadata.ObjectId, new IndexDefinition(name), cancellationToken).ConfigureAwait(false);
            _uniqueIndexes[metadata.ObjectId] = metadata.IsUnique;
            int applied = 0;
            foreach (var (document, reference) in documents)
            {
                var documentKeys = KeysFor(document, metadata, out _, cancellationToken);
                IndexBuildPostingsBuffered?.Invoke(documentKeys.Count);
                foreach (var key in documentKeys)
                {
                    // Preserve source stamps so a newly visible definition agrees with scans.
                    await index.InsertVersionAsync(bracket, key,
                        DocumentStorage.PackLocation(reference.PageId, reference.SlotIndex),
                        reference.Writer, TransactionSequence.None, cancellationToken).ConfigureAwait(false);
                    AfterIndexPostingApplied?.Invoke(++applied);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var inserted = _storage.InsertEntry(bracket, encoded);
            _coordinator.VersionStore.RecordCreated(context.Sequence, inserted.PageId, inserted.SlotIndex);
            SaveRegistrations(bracket);
            return inserted;
        }, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            Add(new CatalogRecord(null, null, metadata), new Reference(location.PageId, location.SlotIndex, context.Sequence));
        }
        return metadata;
    }

    /// <summary>Tombstones an index definition, retaining its tree for older snapshots.</summary>
    /// <param name="collectionId">The collection identity.</param>
    /// <param name="name">The index name.</param>
    /// <param name="context">The active transaction.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task representing the operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="context"/> is null.</exception>
    /// <exception cref="DocumentCatalogException">The index does not exist in the transaction's snapshot.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="context"/> is not active.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the physical application.</exception>
    /// <exception cref="TransactionAbortedException">The transaction ended, or its end began, before the mutation was applied.</exception>
    public async ValueTask DeleteIndexAsync(Guid collectionId, string name, TransactionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(context);
        Found? previous;
        lock (_sync)
        {
            previous = FindIndex(collectionId, name, context.Snapshot);
        }
        if (previous is null)
        {
            throw new DocumentCatalogException("CD01025", string.Format(CultureInfo.CurrentCulture, ErrorMessages.IndexDoesNotExist, name));
        }
        await DeleteAsync(previous, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Seeks a scalar key range and returns visible documents in ordinal identity order.</summary>
    /// <param name="collectionId">The collection identity.</param>
    /// <param name="indexName">The visible index name.</param>
    /// <param name="lower">The inclusive or exclusive scalar lower bound, or null for unbounded.</param>
    /// <param name="includeLower">Whether the lower bound is included.</param>
    /// <param name="upper">The scalar upper bound, or null for unbounded.</param>
    /// <param name="includeUpper">Whether the upper bound is included.</param>
    /// <param name="snapshot">The visibility snapshot shared with the residual predicate.</param>
    /// <param name="cancellationToken">Cancels iteration.</param>
    /// <returns>The matching visible metadata.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="indexName"/> or <paramref name="snapshot"/> is null.</exception>
    /// <exception cref="ArgumentException">A bound is not a scalar an index key can encode.</exception>
    /// <exception cref="DocumentCatalogException">
    /// The index is not visible in <paramref name="snapshot"/>, its physical tree is missing, or a
    /// persisted catalog record is malformed.
    /// </exception>
    /// <exception cref="StorageCorruptionException">A matching entry's catalog page failed its checksum or is malformed.</exception>
    /// <exception cref="StorageIOException">A matching entry's catalog page could not be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled during the iteration.</exception>
    public async ValueTask<IReadOnlyList<DocumentCatalogEntry>> SearchIndexAsync(Guid collectionId, string indexName,
        object? lower, bool includeLower, object? upper, bool includeUpper, TransactionSnapshot snapshot,
        CancellationToken cancellationToken = default)
        => await SearchIndexAsync(collectionId, indexName, [], lower, includeLower, upper, includeUpper,
            snapshot, cancellationToken).ConfigureAwait(false);

    /// <summary>Seeks a leading equality prefix and a range on the next key component.</summary>
    /// <param name="collectionId">The collection identity.</param>
    /// <param name="indexName">The visible index name.</param>
    /// <param name="equalityPrefix">Ordered scalar values for leading paths.</param>
    /// <param name="lower">The next component's lower bound, or null.</param>
    /// <param name="includeLower">Whether the lower bound is inclusive.</param>
    /// <param name="upper">The next component's upper bound, or null.</param>
    /// <param name="includeUpper">Whether the upper bound is inclusive.</param>
    /// <param name="snapshot">The visibility snapshot.</param>
    /// <param name="cancellationToken">Cancels iteration.</param>
    /// <returns>Visible candidate documents in ordinal identity order; apply the residual predicate.</returns>
    /// <exception cref="ArgumentNullException">The name, prefix or snapshot is null.</exception>
    /// <exception cref="ArgumentException">The bounds are not scalar or exceed the index's key count.</exception>
    /// <exception cref="DocumentCatalogException">The definition or tree is absent or malformed.</exception>
    /// <exception cref="StorageCorruptionException">A matching entry's catalog page failed its checksum or is malformed.</exception>
    /// <exception cref="StorageIOException">A matching entry's catalog page could not be read.</exception>
    /// <exception cref="OperationCanceledException">Iteration was canceled.</exception>
    public async ValueTask<IReadOnlyList<DocumentCatalogEntry>> SearchIndexAsync(Guid collectionId, string indexName,
        IReadOnlyList<object> equalityPrefix, object? lower, bool includeLower, object? upper, bool includeUpper,
        TransactionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(indexName);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(equalityPrefix);
        DocumentIndexMetadata definition;
        lock (_sync)
        {
            definition = FindIndex(collectionId, indexName, snapshot)?.Record.Index
                ?? throw new DocumentCatalogException("CD01025", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SearchIndexAsyncDocumentCatalog, indexName));
        }
        var index = ResolveIndex(definition);
        var range = SeekRange(definition, equalityPrefix, lower, includeLower, upper, includeUpper);
        var result = new Dictionary<string, DocumentCatalogEntry>(StringComparer.Ordinal);
        await using var cursor = index.OpenCursor(snapshot, range, cancellationToken: cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            var (page, slot) = DocumentStorage.UnpackLocation(cursor.CurrentEntryReference);

            // An entry whose catalog record was reclaimed beneath it (a deleted or reverted slot, an
            // owner-zero page freed or reallocated) is stale, as in Find; a page that fails its
            // checksum or cannot be read fails the search (#1342). The identity and stamp checks
            // below reject a slot that now holds another record.
            if (!_storage.TryReadRecord(page, slot, 0, out var bytes))
            {
                continue;
            }

            var record = DocumentCatalogCodec.Decode(bytes.Span, page, slot);
            var (writer, deleter) = RecordVersionStamp.ReadStamps(bytes.Span);
            if (record.Document is { } document && document.CollectionId == collectionId
                && snapshot.IsVisible(writer) && (deleter == TransactionSequence.None || !snapshot.IsVisible(deleter)))
            {
                result[document.Id] = document;
            }
        }
        return result.Values.OrderBy(document => document.Id, StringComparer.Ordinal).ToArray();
    }

    // Counts physical, snapshot-visible index entries without loading document content.
    // A completed count may still overestimate identities if catalog references are stale.
    internal BTreeRangeCount CountIndexRange(DocumentIndexMetadata definition, IReadOnlyList<object> equalityPrefix,
        object? lower, bool includeLower, object? upper, bool includeUpper, TransactionSnapshot snapshot,
        CancellationToken cancellationToken)
        => ResolveIndex(definition).CountRange(snapshot,
            SeekRange(definition, equalityPrefix, lower, includeLower, upper, includeUpper),
            entryLimit: 512, pageLimit: 32, cancellationToken: cancellationToken);

    // An existence probe over the first varying component's lossy tags, without
    // reading document content. One visible match suffices; an invisible first entry
    // conservatively leaves the domain unproven. No new cross-assembly probe API.
    internal BTreeRangeCount ProbeComplexOrderKeys(DocumentIndexMetadata definition, IReadOnlyList<object> equalityPrefix,
        TransactionSnapshot snapshot, CancellationToken cancellationToken)
    {
        var prefix = DocumentIndexKeys.EncodePrefix(equalityPrefix).Encoded;
        var start = new byte[prefix.Length + 1];
        prefix.Span.CopyTo(start);
        start[^1] = 6;
        var end = (byte[])start.Clone();
        end[^1] = 8;
        return ResolveIndex(definition).CountRange(snapshot, new IndexKeyRange(new IndexKey(start), new IndexKey(end), true, false),
            entryLimit: 1, pageLimit: 32, cancellationToken: cancellationToken);
    }

    private static IndexKeyRange SeekRange(DocumentIndexMetadata definition, IReadOnlyList<object> equalityPrefix,
        object? lower, bool includeLower, object? upper, bool includeUpper)
    {
        if (equalityPrefix.Count > definition.Keys.Count ||
            equalityPrefix.Count == definition.Keys.Count && (lower is not null || upper is not null))
        { throw new ArgumentException(ErrorMessages.SeekBoundsTooWide, nameof(equalityPrefix)); }
        var prefix = DocumentIndexKeys.EncodePrefix(equalityPrefix);
        IndexKey? start = equalityPrefix.Count == 0 ? null : prefix;
        IndexKey? end = equalityPrefix.Count == 0 ? null : DocumentIndexKeys.Successor(prefix);
        if (lower is not null)
        {
            var key = DocumentIndexKeys.EncodePrefix(equalityPrefix, lower);
            start = includeLower ? key : DocumentIndexKeys.Successor(key);
        }
        if (upper is not null)
        {
            var key = DocumentIndexKeys.EncodePrefix(equalityPrefix, upper);
            end = includeUpper ? DocumentIndexKeys.Successor(key) : key;
        }
        return new IndexKeyRange(start, end, true, false);
    }

    /// <summary>Scrubs abandoned writers from every persisted B+Tree before recovery checkpointing.</summary>
    /// <param name="writers">The uncommitted writers identified by shared recovery.</param>
    /// <param name="cancellationToken">Cancels the scrub.</param>
    /// <returns>A task representing the scrub.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="writers"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled; the scrub's storage bracket is rolled back.</exception>
    public async ValueTask RecoverIndexesAsync(IReadOnlySet<TransactionSequence> writers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writers);
        using var bracket = _storage.BeginTransaction();
        await _indexes.PurgeWritersAsync(bracket, writers, cancellationToken).ConfigureAwait(false);
        SaveRegistrations(bracket);
        bracket.Commit();
    }

    private Found? FindIndex(Guid collectionId, string name, TransactionSnapshot snapshot)
    {
        if (!_indexDefinitions.TryGetValue((collectionId, name), out var versions)) { return null; }
        var found = Find(versions, snapshot, record => record.Index is { } index && index.CollectionId == collectionId && index.Name == name);
        if (versions.Count == 0) { Retire(versions); }
        return found;
    }

    private List<IndexChange> PrepareIndexChanges(DocumentCatalogEntry? document, Found? previous, TransactionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var priorDocument = previous?.Record.Document;
        if (document is null && priorDocument is null)
        {
            return [];
        }
        var collectionId = document?.CollectionId ?? priorDocument!.Value.CollectionId;
        var result = new List<IndexChange>();
        foreach (var metadata in GetIndexes(collectionId, snapshot))
        {
            bool distinct = true;
            var newKeys = document is { } current ? KeysFor(current, metadata, out distinct, cancellationToken) : [];
            result.Add(new IndexChange(metadata,
                priorDocument is { } old ? KeysFor(old, metadata, out _, cancellationToken) : [], newKeys,
                previous is { } prior ? DocumentStorage.PackLocation(prior.Reference.PageId, prior.Reference.SlotIndex) : 0,
                metadata.IsUnique && !distinct));
        }
        return result;
    }

    private HashSet<IndexKey> KeysFor(DocumentCatalogEntry document, DocumentIndexMetadata metadata, out bool distinct,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeforeIndexKeysRead?.Invoke(Monitor.IsEntered(_sync));
        return DocumentIndexKeys.Read(_storage.ReadContent(new DocumentContentReference(document.HeadLocation, document.Length, document.Checksum)),
            metadata, out distinct, cancellationToken);
    }

    private DocumentConstraintViolationException Duplicate(DocumentIndexMetadata metadata, TransactionSnapshot snapshot)
        => DocumentConstraintViolationException.Duplicate(metadata.Name,
            GetCollections(snapshot).First(collection => collection.Id == metadata.CollectionId).Name);

    private async ValueTask CheckUniqueAsync(List<IndexChange> changes,
        TransactionContext context, CancellationToken cancellationToken)
    {
        TransactionSnapshot? latest = null;
        foreach (var change in changes)
        {
            if (!change.CheckUnique || change.NewKeys.Count == 0) { continue; }
            var key = change.NewKeys.Single(); // UNIQUE definitions cannot contain an array key.
            // Commit marks the context Committed before releasing the writer lock,
            // but coordinator untracking follows release. Such a writer is already visible.
            latest ??= new TransactionSnapshot(context.Sequence, TransactionSequence.None,
                new TransactionSequence(ulong.MaxValue), _coordinator.GetOpenContexts().Where(item => item.State != TransactionState.Committed).Select(item => item.Sequence));
            await using var cursor = ResolveIndex(change.Metadata).OpenCursor(latest, new IndexKeyRange(key, key, true, true), cancellationToken: cancellationToken);
            while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                if (cursor.CurrentEntryReference != change.OldLocation) { throw Duplicate(change.Metadata, latest); }
            }
        }
    }

    private async ValueTask ApplyIndexChangesAsync(List<IndexChange> changes, (PageId PageId, int SlotIndex) inserted,
        TransactionContext context, CancellationToken cancellationToken)
    {
        int applied = 0;
        foreach (var change in changes)
        {
            var index = ResolveIndex(change.Metadata);
            var undo = new IndexUndo(this, change.Metadata);
            foreach (var oldKey in change.OldKeys)
            {
                await index.DeleteAsync(context, oldKey, change.OldLocation, cancellationToken).ConfigureAwait(false);
                _coordinator.VersionStore.RecordIndexEntryTombstoned(context.Sequence, undo, oldKey.Encoded, change.OldLocation);
                AfterIndexPostingApplied?.Invoke(++applied);
            }
            foreach (var newKey in change.NewKeys)
            {
                ulong location = DocumentStorage.PackLocation(inserted.PageId, inserted.SlotIndex);
                await index.InsertAsync(context, newKey, location, cancellationToken).ConfigureAwait(false);
                _coordinator.VersionStore.RecordIndexEntryCreated(context.Sequence, undo, newKey.Encoded, location);
                AfterIndexPostingApplied?.Invoke(++applied);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private BTreeIndex ResolveIndex(DocumentIndexMetadata metadata)
        => _indexes.TryGetIndex(metadata.ObjectId, metadata.Name, out var index)
            ? index : throw new DocumentCatalogException("CD06105", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CatalogResolveIndexDocumentCatalog, metadata.Name));

    private async ValueTask<T> ApplyAsync<T>(TransactionContext context,
        Func<StorageTransaction, ValueTask<T>> apply, CancellationToken cancellationToken)
    {
        // Lock/latch order: caller's database writer lock -> coordinator apply
        // gate -> B-tree latch -> storage page latch. Key preparation may take
        // the catalog monitor before storage latches, but never spans an await.
        // No catalog monitor is held while entering apply, undo or index cursors;
        // cursors release their tree latch before we read catalog/content pages.
        try
        {
            return await _coordinator.ApplyStatementAsync(context, apply, durable: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Physical rollback restores pages; reattach the manager's in-memory roots
            // from those restored registrations before logical undo or another write.
            OpenIndexes();
            throw;
        }
    }

    internal ValueTask<long> PruneIndexesAsync(TransactionSequence bound, CancellationToken cancellationToken)
        => _indexes.PruneAsync(bound, cancellationToken);

    private void OpenIndexes()
    {
        _registrations.Clear();
        _uniqueIndexes.Clear();
        var registrations = new List<BTreeIndexRegistration>();
        foreach (var (registration, page, slot, isUnique) in ReadRegistrations(_storage))
        {
            registrations.Add(registration);
            _uniqueIndexes.Add(registration.ObjectId, isUnique);
            _registrations.Add(registration.ObjectId, (page, slot, registration.RootPageId));
        }
        _indexes = BTreeIndexManager.Create(new BTreeIndexManagerOptions
        {
            Storage = _storage,
            TransactionSource = ResolveStatementBracket,
            PruneBound = () => _coordinator.PruneBound,
            ExistingIndexes = registrations
        });
    }

    private static List<(BTreeIndexRegistration Registration, PageId Page, int Slot, bool IsUnique)> ReadRegistrations(DocumentStorage storage)
    {
        var registrations = new List<(BTreeIndexRegistration, PageId, int, bool)>();
        var objectIds = new HashSet<ulong>();
        using var iterator = storage.GetUnitIterator(1);
        while (iterator.MoveNext())
        {
            var unit = iterator.Current;
            try
            {
                using var stream = new MemoryStream(unit.Data.ToArray(), writable: false);
                using var reader = new BinaryReader(stream, new UTF8Encoding(false, true));
                if (reader.ReadUInt64() != 0 || reader.ReadUInt64() != 0 || reader.ReadByte() != 5)
                {
                    throw new DocumentCatalogException("CD06105", ErrorMessages.PhysicalRegistrationHeaderInvalid);
                }
                DocumentCatalogCodec.EnsureVersion(5, reader.ReadByte());
                ulong objectId = reader.ReadUInt64();
                long root = reader.ReadInt64();
                string name = DocumentCatalogCodec.ReadString(reader) ?? throw new DocumentCatalogException("CD06105", ErrorMessages.PhysicalRegistrationNameMissing);
                byte flags = reader.ReadByte();
                if (flags > 1 || objectId == 0 || root <= 0 || stream.Position != stream.Length || !objectIds.Add(objectId))
                {
                    throw new DocumentCatalogException("CD06105", ErrorMessages.PhysicalRegistrationInvalid);
                }
                registrations.Add((new BTreeIndexRegistration(objectId, new IndexDefinition(name), root), unit.PageId, unit.SlotIndex, flags == 1));
            }
            catch (Exception error) when (error is DocumentCatalogException or IOException or ArgumentException or OverflowException)
            {
                var malformed = error as DocumentCatalogException
                    ?? new DocumentCatalogException("CD06105", ErrorMessages.PhysicalRegistrationMalformed, error);
                throw DocumentCatalogCodec.At(unit.PageId, unit.SlotIndex, malformed);
            }
        }
        return registrations;
    }

    private void SaveRegistrations(StorageTransaction bracket)
    {
        foreach (var registration in _indexes.ExportRegistrations())
        {
            if (_registrations.TryGetValue(registration.ObjectId, out var prior) && prior.Root == registration.RootPageId)
            {
                continue;
            }
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(0UL);
            writer.Write(0UL);
            writer.Write((byte)5);
            writer.Write(DocumentCatalogCodec.IndexRecordVersion);
            writer.Write(registration.ObjectId);
            writer.Write(registration.RootPageId);
            DocumentCatalogCodec.WriteString(writer, registration.Definition.Name);
            writer.Write(_uniqueIndexes.GetValueOrDefault(registration.ObjectId));
            var bytes = stream.ToArray();
            if (_registrations.ContainsKey(registration.ObjectId))
            {
                _storage.UpdateEntry(bracket, prior.Page, prior.Slot, bytes);
                _registrations[registration.ObjectId] = (prior.Page, prior.Slot, registration.RootPageId);
            }
            else
            {
                var location = _storage.InsertIndexRegistration(bracket, bytes);
                _registrations[registration.ObjectId] = (location.PageId, location.SlotIndex, registration.RootPageId);
            }
        }
    }

    private readonly record struct IndexChange(DocumentIndexMetadata Metadata, HashSet<IndexKey> OldKeys,
        HashSet<IndexKey> NewKeys, ulong OldLocation, bool CheckUnique);

    // The index manager's storage transaction for a context: the shared statement bracket the
    // coordinator owns, or this catalog's own error when there is none.
    private StorageTransaction ResolveStatementBracket(TransactionContext context)
        => _coordinator.TryGetStorageTransaction(context, out var bracket)
            ? bracket : throw new InvalidOperationException(ErrorMessages.CatalogResolveStatementBracketFailure);

    [global::System.Diagnostics.DebuggerDisplay("IndexUndo")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Catalog.DocumentCatalog.IndexUndo.DebugView))]
    private sealed class IndexUndo : RecordVersionIndex
    {
        private readonly DocumentCatalog _catalog;
        private readonly DocumentIndexMetadata _metadata;

        /// <summary>
        /// Initializes a new instance of the <see cref="IndexUndo"/> class.
        /// </summary>
        /// <param name="catalog">The catalog that resolves the index's physical tree when an undo runs.</param>
        /// <param name="metadata">The metadata of the index whose entries are undone.</param>
        public IndexUndo(DocumentCatalog catalog, DocumentIndexMetadata metadata)
        {
            _catalog = catalog;
            _metadata = metadata;
        }

        public override bool Equals(object? obj) => obj is IndexUndo other && ReferenceEquals(_catalog, other._catalog) && _metadata.ObjectId == other._metadata.ObjectId;
        public override int GetHashCode() => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_catalog), _metadata.ObjectId);

        protected override ValueTask EraseCoreAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference,
            TransactionSequence writer, CancellationToken cancellationToken)
            => _catalog.ResolveIndex(_metadata).EraseAsync(transaction, new IndexKey(key), entryReference, writer, cancellationToken);

        protected override ValueTask ClearDeleterCoreAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference,
            TransactionSequence writer, CancellationToken cancellationToken)
            => _catalog.ResolveIndex(_metadata).ClearDeleterAsync(transaction, new IndexKey(key), entryReference, writer, cancellationToken);

        private sealed class DebugView
        {
            public DebugView(global::Assimalign.Cohesion.Database.Documents.Catalog.DocumentCatalog.IndexUndo value)
            {
                _ = value;
            }
        }
    }
}
