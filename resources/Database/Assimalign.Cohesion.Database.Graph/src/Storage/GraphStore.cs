using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Storage;

/// <summary>Transactional graph records, label postings, adjacency, and exact node-property indexes over a shared coordinator.</summary>
/// <remarks>
/// <b>Shape (concrete-types plan, phase 4, #1260).</b> One sealed type with a private constructor
/// behind <see cref="Open"/>: it replaces the former <c>IGraphStore</c> interface, the
/// <c>GraphStore</c> static factory and their internal implementation (the triplet of
/// <c>database-area.md</c> rule 1).
/// </remarks>
public sealed partial class GraphStore
{
    private readonly GraphStorage _storage;
    private readonly TransactionCoordinator _coordinator;
    private readonly object _sync = new();
    private readonly Dictionary<(byte Kind, ulong Id), VersionChain> _records = new();

    // The identities of the index definitions among _records, so reading the definitions costs
    // their count rather than a pass over every node and relationship record.
    private readonly HashSet<ulong> _definitionIds = new();

    private GraphStore(GraphStorage storage, TransactionCoordinator coordinator)
    {
        _storage = storage;
        _coordinator = coordinator;
        _mutationGateFactory = CreateMutationGate;
        using var iterator = storage.GetUnitIterator(2);
        while (iterator.MoveNext())
        {
            var unit = iterator.Current;
            var record = GraphRecordCodec.Decode(unit.Data);
            var (writer, _) = RecordVersionStamp.ReadStamps(unit.Data.Span);
            Add(record, GraphStorage.PackLocation(unit.PageId, unit.SlotIndex), writer, prune: false);
            if (record.Kind == 3) { _definitionIds.Add(record.Id); }
        }
        foreach (var key in _records.Keys.ToArray())
        {
            var versions = _records[key];
            if (versions.Older is null) { continue; }
            var ordered = new List<Reference> { versions.Head };
            for (var older = versions.Older; older is not null; older = older.Next) { ordered.Add(older.Reference); }
            ordered.Sort((left, right) => right.Revision.CompareTo(left.Revision));
            versions = new VersionChain { Head = ordered[0] };
            for (int i = ordered.Count - 1; i > 0; i--) { versions.Older = new History(ordered[i], versions.Older); }
            _records[key] = versions;
        }
        OpenIndexes();
    }

    /// <summary>Loads graph directories and physical B+Tree registrations.</summary>
    /// <param name="storage">Graph file set.</param><param name="coordinator">Coordinator bound to the same file set.</param><returns>The graph store.</returns>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    /// <exception cref="IndexFormatException">An index tree is in another B-tree page format.</exception>
    /// <exception cref="StorageCorruptionException">A graph record is malformed or uses an unsupported format.</exception>
    public static GraphStore Open(GraphStorage storage, TransactionCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(coordinator);
        return new GraphStore(storage, coordinator);
    }

    /// <summary>
    /// Checks graph record format 4 and that every index tree the storage registers is in the B-tree page format
    /// this engine reads (<see cref="BTreeIndexManager.FormatVersion"/>), reading record
    /// headers, registrations and each tree's root page. An engine calls it before the
    /// coordinator's recovery scrub, so a database whose indexes it cannot read is
    /// refused before anything is written to it; <see cref="Open"/> checks again as it
    /// attaches the trees. Registration records carry no MVCC stamps, so they read the
    /// same before and after the scrub.
    /// </summary>
    /// <param name="storage">Graph file set.</param>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
    /// <exception cref="IndexFormatException">An index tree is in another B-tree page format.</exception>
    /// <exception cref="StorageCorruptionException">A graph record or registration has an unsupported format (<c>COHDBG017</c>), or a persisted index registration is malformed.</exception>
    public static void EnsureIndexFormat(GraphStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        using var records = storage.GetUnitIterator(2);
        while (records.MoveNext()) { GraphRecordCodec.EnsureFormat(records.Current.Data.Span); }
        BTreeIndexManager.EnsureFormat(storage, ReadRegistrations(storage).Select(entry => entry.Registration));
    }

    /// <summary>Creates a node and maintains its label and property indexes.</summary>
    /// <param name="labelIds">The nonempty catalog identity for each label. The caller resolves these identities from the catalog.</param>
    /// <param name="node">The complete node with an identity reserved from the backing storage.</param><param name="context">Owning transaction.</param><param name="cancellationToken">Cancellation token.</param><returns>The created node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> has null labels or properties, or <paramref name="labelIds"/> or <paramref name="context"/> is null.</exception>
    /// <exception cref="ArgumentException">The identity already exists, a label or property key is null or whitespace, a label has no nonempty catalog identity, or a property value is not a supported scalar.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The identity is zero.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="context"/> is not active.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="GraphElementTooLargeException">The labels and properties exceed one graph record, or an indexed property value exceeds the index key.</exception>
    /// <exception cref="TransactionAbortedException">The transaction ended while waiting for its writer lock.</exception>
    public async ValueTask<StoredGraphNode> CreateNodeAsync(StoredGraphNode node,
        IReadOnlyDictionary<string, Guid> labelIds, TransactionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(labelIds);
        ArgumentNullException.ThrowIfNull(node.Labels);
        ArgumentNullException.ThrowIfNull(node.Properties);
        ArgumentOutOfRangeException.ThrowIfZero(node.Id);
        foreach (string label in node.Labels) { ArgumentException.ThrowIfNullOrWhiteSpace(label); }
        using var mutation = await EnterMutationAsync(context, cancellationToken).ConfigureAwait(false);
        context = mutation.RefreshSnapshot(context);
        EnsureNew(1, node.Id);
        var proposed = new GraphRecord(1, node.Id, new StoredGraphNode(node.Id,
            node.Labels.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), node.Properties));
        proposed = proposed with { LabelIds = ResolveLabelIds(proposed.Node!.Value.Labels, labelIds) };
        byte[] bytes = await EncodeAsync(proposed, context.Sequence).ConfigureAwait(false);
        var record = GraphRecordCodec.Decode(bytes); // Own immutable copies, never retain caller dictionaries.
        node = record.Node!.Value;
        var indexes = Definitions(Latest(context));
        var changes = indexes.Where(index => node.Labels.Contains(index.Label!, StringComparer.Ordinal)
            && node.Properties.ContainsKey(index.PropertyKey!))
            .Select(index => (index.Id, Key: Composite(GraphRecordCodec.Key(node.Properties[index.PropertyKey!]), node.Id))).ToArray();
        var inserted = await ApplyAsync(context, async bracket =>
        {
            if (record.LabelIds!.Length != 0) { await EnsureLabelsAsync(context, cancellationToken).ConfigureAwait(false); }
            var location = Insert(bracket, bytes, context);
            await InsertLabelsAsync(record, location, context, cancellationToken).ConfigureAwait(false);
            foreach (var change in changes)
            {
                await InsertIndexAsync(change.Id, change.Key, location, context, cancellationToken).ConfigureAwait(false);
            }
            SaveRegistrations(bracket);
            return location;
        }, cancellationToken).ConfigureAwait(false);
        Add(record, inserted, context.Sequence);
        return node;
    }

    /// <summary>Finds a node visible to a snapshot.</summary>
    /// <param name="id">Node identity.</param><param name="snapshot">Visibility snapshot.</param><returns>The visible node or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    public StoredGraphNode? FindNode(ulong id, TransactionSnapshot snapshot) => Find(1, id, snapshot)?.Record.Node;

    /// <summary>Finds a visible relationship.</summary>
    /// <param name="id">Relationship identity.</param><param name="snapshot">Visibility snapshot.</param><returns>The visible relationship or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    public StoredGraphRelationship? FindRelationship(ulong id, TransactionSnapshot snapshot) => Find(2, id, snapshot)?.Record.Relationship;

    /// <summary>Enumerates visible nodes in identity order, seeking MVCC label postings when a label is specified.</summary>
    /// <param name="label">Optional label.</param><param name="snapshot">Visibility snapshot.</param><returns>The visible nodes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="StorageCorruptionException">A selected record or label posting is malformed, or a required tree is missing.</exception>
    public IReadOnlyList<StoredGraphNode> GetNodes(string? label, TransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (label is not null) { return ScanLabel(label, snapshot); }
        return Ids(1).Select(id => FindNode(id, snapshot)).Where(node => node.HasValue)
            .Select(node => node!.Value).OrderBy(node => node.Id).ToArray();
    }

    /// <summary>Atomically creates a relationship and both endpoint adjacency entries.</summary>
    /// <param name="relationship">The complete relationship with an identity reserved from the backing storage.</param>
    /// <param name="typeId">The stable catalog identity of the relationship type; never a name hash. The store does not verify that this identity matches <c>relationship.Type</c>; the engine resolves it from the catalog.</param>
    /// <param name="context">Owning transaction.</param><param name="cancellationToken">Cancellation token.</param><returns>The created relationship.</returns>
    /// <exception cref="InvalidOperationException">An endpoint does not exist in the latest committed view, or <paramref name="context"/> is not active.</exception>
    /// <exception cref="TransactionAbortedException">An endpoint was deleted after the transaction snapshot, or the transaction ended while waiting for its writer lock.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="relationship"/> has null properties, or <paramref name="context"/> is null.</exception>
    /// <exception cref="ArgumentException">The identity already exists, the type identity is empty, the type or a property key is null or whitespace, or a property value is not a supported scalar.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The identity is zero.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="GraphElementTooLargeException">The type and properties exceed one graph record.</exception>
    public async ValueTask<StoredGraphRelationship> CreateRelationshipAsync(StoredGraphRelationship relationship, Guid typeId,
        TransactionContext context, CancellationToken cancellationToken = default)
    {
        if (typeId == Guid.Empty) { throw new ArgumentException(ErrorMessages.RelationshipTypeIdInvalid, nameof(typeId)); }
        ArgumentException.ThrowIfNullOrWhiteSpace(relationship.Type);
        ArgumentNullException.ThrowIfNull(relationship.Properties);
        ArgumentOutOfRangeException.ThrowIfZero(relationship.Id);
        ulong sourceId = relationship.SourceId, targetId = relationship.TargetId;
        using var mutation = await EnterMutationAsync(context, cancellationToken).ConfigureAwait(false);
        context = mutation.RefreshSnapshot(context);
        var latest = Latest(context);
        if (FindNode(sourceId, latest) is null && FindNode(sourceId, context.Snapshot) is not null ||
            FindNode(targetId, latest) is null && FindNode(targetId, context.Snapshot) is not null)
        { throw new TransactionAbortedException(ErrorMessages.EndpointDeleted); }
        if (FindNode(sourceId, latest) is null || FindNode(targetId, latest) is null)
        {
            throw new InvalidOperationException("A relationship requires two visible existing endpoints.");
        }
        ulong id = relationship.Id;
        EnsureNew(2, id);
        byte[] bytes = await EncodeAsync(new GraphRecord(2, id, Relationship: relationship, TypeId: typeId), context.Sequence).ConfigureAwait(false);
        var record = GraphRecordCodec.Decode(bytes);
        var inserted = await ApplyAsync(context, async bracket =>
        {
            await EnsureAdjacencyAsync(context, cancellationToken).ConfigureAwait(false);
            var location = Insert(bracket, bytes, context);
            await InsertAdjacencyAsync(record, location, context, cancellationToken).ConfigureAwait(false);
            SaveRegistrations(bracket);
            return location;
        }, cancellationToken).ConfigureAwait(false);
        Add(record, inserted, context.Sequence);
        return record.Relationship!.Value;
    }

    /// <summary>Seeks the shared B+Tree for a node's incident relationships; self-loops appear once.</summary>
    /// <param name="nodeId">Node identity.</param><param name="snapshot">Visibility snapshot.</param><param name="cancellationToken">Cancellation token.</param><returns>Visible incident relationships ordered by identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async ValueTask<IReadOnlyList<StoredGraphRelationship>> GetIncidentAsync(ulong nodeId, TransactionSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        var result = new List<StoredGraphRelationship>();
        await foreach (var edge in GetIncidentAsync(nodeId, GraphIncidentDirection.Both, null, snapshot, cancellationToken).ConfigureAwait(false))
        { result.Add(edge); }
        result.Sort(static (left, right) => left.Id.CompareTo(right.Id));
        return result;
    }

    // Instance counters for deterministic complexity tests; no process-global telemetry.
    private long _incidentRecordsDecoded;
    internal long IncidentRecordsDecoded => Interlocked.Read(ref _incidentRecordsDecoded);

    // Discover only the types present at this endpoint. Jump past each whole type run
    // instead of enumerating its degree or probing every type in the database catalog.
    internal async IAsyncEnumerable<(Guid Type, ulong HeadId)> GetIncidentTypesAsync(ulong nodeId, GraphIncidentDirection direction,
        TransactionSnapshot snapshot, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_indexes.TryGetIndex(adjacencyId, treeName, out var index)) { yield break; }
        var low = AdjacencyKey(nodeId, direction, Guid.Empty, 0);
        var high = AdjacencyKey(nodeId, direction, _maximumTypeId, ulong.MaxValue);
        bool inclusive = true;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var cursor = index.OpenCursor(snapshot, new IndexKeyRange(low, high, inclusive, true),
                cancellationToken: cancellationToken);
            if (!await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false)) { yield break; }
            var key = cursor.CurrentKey.Encoded.Span;
            if (key.Length != 33) { throw new StorageCorruptionException(ErrorMessages.AdjacencyKeyInvalid); }
            Guid type = new(key.Slice(9, 16), bigEndian: true);
            yield return (type, BinaryPrimitives.ReadUInt64BigEndian(key.Slice(25)));
            if (type == _maximumTypeId) { yield break; }
            low = AdjacencyKey(nodeId, direction, type, ulong.MaxValue);
            inclusive = false;
        }
    }

    /// <summary>Streams visible relationships from indexed endpoint, direction and catalog-type ranges.</summary>
    /// <param name="nodeId">Incident node identity.</param>
    /// <param name="direction">Direction relative to the incident node.</param>
    /// <param name="typeIds">Catalog type identities, or null for all types; an empty collection matches nothing.</param>
    /// <param name="snapshot">Visibility snapshot, retained by the caller throughout enumeration.</param>
    /// <param name="cancellationToken">Cancels seeking and reading, including an empty lookup.</param>
    /// <returns>Visible relationships, each identity once; ordering is unspecified across ranges.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="direction"/> is undefined.</exception>
    /// <exception cref="StorageCorruptionException">A selected relationship record or adjacency key is malformed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <remarks>Enumeration owns only B+Tree cursors. Disposal, cancellation and caller exceptions close them without taking a graph writer lock.</remarks>
    public async IAsyncEnumerable<StoredGraphRelationship> GetIncidentAsync(ulong nodeId, GraphIncidentDirection direction,
        IReadOnlyCollection<Guid>? typeIds, TransactionSnapshot snapshot,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var edge in StreamIncidentRangeAsync(nodeId, direction, typeIds, snapshot, null, cancellationToken).ConfigureAwait(false))
        { yield return edge; }
    }

    internal async IAsyncEnumerable<StoredGraphRelationship> StreamIncidentRangeAsync(ulong nodeId, GraphIncidentDirection direction,
        IReadOnlyCollection<Guid>? typeIds, TransactionSnapshot snapshot, Action<ulong>? reserve,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Enum.IsDefined(direction)) { throw new ArgumentOutOfRangeException(nameof(direction)); }
        cancellationToken.ThrowIfCancellationRequested();
        if (typeIds is { Count: 0 } || !_indexes.TryGetIndex(adjacencyId, treeName, out var index)) { yield break; }
        // Copy the caller's selection once; duplicate type ids must not duplicate visits.
        var types = typeIds?.Distinct().ToArray();
        foreach (var selected in direction == GraphIncidentDirection.Both
            ? new[] { GraphIncidentDirection.Outgoing, GraphIncidentDirection.Incoming } : new[] { direction })
        {
            foreach (Guid? type in types is null ? new Guid?[] { null } : types.Select(id => (Guid?)id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var low = AdjacencyKey(nodeId, selected, type ?? Guid.Empty, 0);
                var high = AdjacencyKey(nodeId, selected, type ?? _maximumTypeId, ulong.MaxValue);
                cancellationToken.ThrowIfCancellationRequested();
                await using var cursor = index.OpenCursor(snapshot, new IndexKeyRange(low, high, true, true), cancellationToken: cancellationToken);
                while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = cursor.CurrentKey.Encoded.Span;
                    if (key.Length != 33) { throw new StorageCorruptionException(ErrorMessages.AdjacencyKeyInvalid); }
                    ulong id = BinaryPrimitives.ReadUInt64BigEndian(key.Slice(25));
                    reserve?.Invoke(id); // Charge the head before record decoding, including merge initialization.
                    cancellationToken.ThrowIfCancellationRequested();
                    var found = ReadAt(cursor.CurrentEntryReference, snapshot,
                        direction == GraphIncidentDirection.Both && selected == GraphIncidentDirection.Incoming ? nodeId : null);
                    if (found?.Record.Relationship is not { } edge) { continue; }
                    Interlocked.Increment(ref _incidentRecordsDecoded);
                    if (id != edge.Id || new Guid(key.Slice(9, 16), bigEndian: true) != found.Value.Record.TypeId ||
                        (selected == GraphIncidentDirection.Outgoing ? edge.SourceId : edge.TargetId) != nodeId) { continue; }
                    yield return edge;
                }
            }
        }
    }

    /// <summary>Deletes a node, atomically cascading incident relationships when requested.</summary>
    /// <param name="id">Node identity.</param><param name="detach">Whether to cascade relationships.</param><param name="context">Owning transaction.</param><param name="cancellationToken">Cancellation token.</param><returns>A task representing deletion.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A connected node is deleted without detach, or <paramref name="context"/> is not active.</exception>
    /// <exception cref="TransactionAbortedException">The node or its incident relationships changed after the snapshot, or the transaction ended while waiting for its writer lock.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async ValueTask DeleteNodeAsync(ulong id, bool detach, TransactionContext context, CancellationToken cancellationToken = default)
    {
        using var mutation = await EnterMutationAsync(context, cancellationToken).ConfigureAwait(false);
        context = mutation.RefreshSnapshot(context);
        EnsureUnchanged(1, id, context);
        var node = Find(1, id, context.Snapshot);
        if (node is null) { return; }
        var incident = await GetIncidentAsync(id, Latest(context), cancellationToken).ConfigureAwait(false);
        foreach (var relationship in incident) { EnsureUnchanged(2, relationship.Id, context); }
        if (incident.Count != 0 && !detach) { throw new InvalidOperationException("Cannot delete a connected node without DETACH DELETE."); }
        var indexes = Definitions(Latest(context));
        await ApplyAsync(context, async bracket =>
        {
            foreach (var relationship in incident)
            {
                await RemoveRelationshipAsync(Find(2, relationship.Id, context.Snapshot)!.Value, bracket, context, cancellationToken).ConfigureAwait(false);
            }
            var stored = node.Value.Record.Node!.Value;
            foreach (var definition in indexes)
            {
                if (stored.Labels.Contains(definition.Label!, StringComparer.Ordinal) && stored.Properties.TryGetValue(definition.PropertyKey!, out var value))
                {
                    await DeleteIndexEntryAsync(definition.Id, Composite(GraphRecordCodec.Key(value), stored.Id), node.Value.Reference.Location, context, cancellationToken).ConfigureAwait(false);
                }
            }
            await DeleteLabelsAsync(node.Value.Record, node.Value.Reference.Location, context, cancellationToken).ConfigureAwait(false);
            Tombstone(bracket, node.Value.Reference, context);
            SaveRegistrations(bracket);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes a relationship and its adjacency entries atomically.</summary>
    /// <param name="id">Relationship identity.</param><param name="context">Owning transaction.</param><param name="cancellationToken">Cancellation token.</param><returns>A task representing deletion.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="context"/> is not active.</exception>
    /// <exception cref="TransactionAbortedException">The relationship changed after the transaction snapshot, or the transaction ended while waiting for its writer lock.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async ValueTask DeleteRelationshipAsync(ulong id, TransactionContext context, CancellationToken cancellationToken = default)
    {
        using var mutation = await EnterMutationAsync(context, cancellationToken).ConfigureAwait(false);
        context = mutation.RefreshSnapshot(context);
        EnsureUnchanged(2, id, context);
        if (Find(2, id, context.Snapshot) is not { } relationship) { return; }
        await ApplyAsync(context, async bracket =>
        {
            await RemoveRelationshipAsync(relationship, bracket, context, cancellationToken).ConfigureAwait(false);
            SaveRegistrations(bracket);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RemoveRelationshipAsync(Found found, StorageTransaction bracket,
        TransactionContext context, CancellationToken cancellationToken)
    {
        var relationship = found.Record.Relationship!.Value;
        await DeleteIndexEntryAsync(adjacencyId, AdjacencyKey(relationship.SourceId, GraphIncidentDirection.Outgoing, found.Record.TypeId, relationship.Id), found.Reference.Location, context, cancellationToken).ConfigureAwait(false);
        await DeleteIndexEntryAsync(adjacencyId, AdjacencyKey(relationship.TargetId, GraphIncidentDirection.Incoming, found.Record.TypeId, relationship.Id), found.Reference.Location, context, cancellationToken).ConfigureAwait(false);
        Tombstone(bracket, found.Reference, context);
    }

    private void EnsureNew(byte kind, ulong id)
    {
        lock (_sync)
        {
            if (_records.ContainsKey((kind, id))) { throw new ArgumentException(ErrorMessages.IdentityAlreadyExists); }
        }
    }

    private ulong Insert(StorageTransaction bracket, byte[] bytes, TransactionContext context)
    {
        var location = _storage.InsertOwned(bracket, 2, bytes);
        _coordinator.VersionStore.RecordCreated(context.Sequence, location.PageId, location.SlotIndex);
        return GraphStorage.PackLocation(location.PageId, location.SlotIndex);
    }

    private void Tombstone(StorageTransaction bracket, Reference reference, TransactionContext context)
    {
        var bytes = _storage.ReadEntry(reference.PageId, reference.SlotIndex);
        _storage.UpdateEntry(bracket, reference.PageId, reference.SlotIndex, RecordVersionStamp.WithDeleter(bytes.Span, context.Sequence));
        _coordinator.VersionStore.RecordTombstoned(context.Sequence, reference.PageId, reference.SlotIndex);
    }

    private void Add(GraphRecord record, ulong location, TransactionSequence writer, bool prune = true)
    {
        var (page, slot) = GraphStorage.UnpackLocation(location);
        lock (_sync)
        {
            RememberLabels(record);
            var next = new Reference(page, slot, writer, record.Revision);
            if (!_records.TryGetValue((record.Kind, record.Id), out var versions))
            { versions = new VersionChain { Head = next }; }
            else
            {
                if (prune) { Sweep(ref versions, record.Kind, record.Id); }
                versions.Older = new History(versions.Head, versions.Older);
                versions.Head = next;
            }
            _records[(record.Kind, record.Id)] = versions;
            if (record.Kind == 3) { _definitionIds.Add(record.Id); }
        }
    }

    private ulong[] Ids(byte kind)
    {
        lock (_sync)
        {
            return kind == 3 ? _definitionIds.ToArray() : _records.Keys.Where(key => key.Kind == kind).Select(key => key.Id).ToArray();
        }
    }

    private Found? Find(byte kind, ulong id, TransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            if (!_records.TryGetValue((kind, id), out var versions)) { return null; }
            History? current = null, previous = null;
            var reference = versions.Head;
            while (true)
            {
                if (TryRead(reference, kind, id, out var bytes))
                {
                    var (writer, deleter) = RecordVersionStamp.ReadStamps(bytes.Span);
                    if (snapshot.IsVisible(writer) && (deleter == TransactionSequence.None || !snapshot.IsVisible(deleter)))
                    { return new Found(reference, DecodeRead(bytes)); }
                    if (current is not null) { previous = current; }
                }
                else
                {
                    versions.SweepPrevious = null;
                    if (current is null)
                    {
                        if (versions.Older is null) { _records.Remove((kind, id)); return null; }
                        versions.Head = versions.Older.Reference;
                        versions.Older = versions.Older.Next;
                        _records[(kind, id)] = versions;
                        reference = versions.Head;
                        continue;
                    }
                    if (previous is null) { versions.Older = current.Next; }
                    else { previous.Next = current.Next; }
                    _records[(kind, id)] = versions;
                }
                current = current is null ? versions.Older : current.Next;
                if (current is null) { return null; }
                reference = current.Reference;
            }
        }
    }

    private bool TryRead(Reference reference, byte kind, ulong id, out ReadOnlyMemory<byte> bytes)
    {
        if (!_storage.TryReadRecord(reference.PageId, reference.SlotIndex, 2, out bytes)) { return false; }
        GraphRecordCodec.EnsureFormat(bytes.Span);
        if (bytes.Length < 34) { throw new StorageCorruptionException(ErrorMessages.RecordTruncated); }
        byte storedKind = bytes.Span[16];
        ulong storedId = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Span.Slice(18, 8));
        if (storedKind is < 1 or > 3 || storedId == 0)
        {
            throw new StorageCorruptionException(reference.PageId, string.Format(
                System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.RecordHeaderInvalid,
                (long)reference.PageId, reference.SlotIndex, storedKind, storedId));
        }
        return RecordVersionStamp.ReadStamps(bytes.Span).Writer == reference.Writer && storedKind == kind && storedId == id;
    }

    private void Sweep(ref VersionChain versions, byte kind, ulong id)
    {
        // Advance a rotating cursor, bounding publication work independently of history length.
        for (int i = 0; i < 4; i++)
        {
            var previous = versions.SweepPrevious;
            var current = previous is null ? versions.Older : previous.Next;
            if (current is null) { versions.SweepPrevious = null; break; }
            if (TryRead(current.Reference, kind, id, out _)) { versions.SweepPrevious = current; }
            else if (previous is null) { versions.Older = current.Next; }
            else { previous.Next = current.Next; }
        }
    }

    // The common single-version identity stores its reference directly in the dictionary.
    private struct VersionChain
    {
        internal Reference Head;
        internal History? Older;
        internal History? SweepPrevious;
    }

    private sealed class History
    {
        internal readonly Reference Reference;
        internal History? Next;
        internal History(Reference reference, History? next) { Reference = reference; Next = next; }
    }

    private Found? ReadAt(ulong location, TransactionSnapshot snapshot, ulong? skipSourceId = null)
    {
        var (page, slot) = GraphStorage.UnpackLocation(location);
        // A reclaimed location reads as a stale reference; CRC and I/O errors propagate (#1342).
        if (!_storage.TryReadRecord(page, slot, 2, out var bytes)) { return null; }
        if (bytes.Length < 26) { throw new StorageCorruptionException("Truncated graph record."); }
        var (writer, deleter) = RecordVersionStamp.ReadStamps(bytes.Span);
        if (!snapshot.IsVisible(writer) || (deleter != TransactionSequence.None && snapshot.IsVisible(deleter))) { return null; }
        GraphRecordCodec.EnsureFormat(bytes.Span);
        // Both already returned self-loops in its outgoing pass. Inspect the fixed source
        // field instead of retaining a set proportional to the outgoing degree or decoding twice.
        if (skipSourceId is { } source && bytes.Length >= 50 && bytes.Span[16] == 2 &&
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.Span.Slice(34, 8)) == source) { return null; }
        var record = DecodeRead(bytes);
        return new Found(new Reference(page, slot, writer, record.Revision), record);
    }

    private async ValueTask LockAsync(TransactionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.State != TransactionState.Active) { throw new InvalidOperationException("Graph mutation requires an active transaction."); }
        await _coordinator.LockManager.AcquireAsync(context.Sequence, LockResource.Database(), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.State != TransactionState.Active)
            {
                throw new TransactionAbortedException("Graph transaction ended while waiting for the writer lock.");
            }
        }
        catch
        {
            _coordinator.LockManager.ReleaseAll(context.Sequence);
            throw;
        }
    }

    private TransactionSnapshot Latest(TransactionContext context) => new(context.Sequence, TransactionSequence.None,
        new TransactionSequence(ulong.MaxValue), _coordinator.GetOpenContexts().Where(item => item.State == TransactionState.Active).Select(item => item.Sequence));

    private void EnsureUnchanged(byte kind, ulong id, TransactionContext context)
    {
        var prior = Find(kind, id, context.Snapshot);
        var latest = Find(kind, id, Latest(context));
        if (prior?.Reference != latest?.Reference)
        {
            throw new TransactionAbortedException("Graph write conflict: the addressed record changed after the transaction snapshot.");
        }
    }

    private async ValueTask<T> ApplyAsync<T>(TransactionContext context, Func<StorageTransaction, ValueTask<T>> apply, CancellationToken cancellationToken)
    {
        try { return await _coordinator.ApplyStatementAsync(context, apply, durable: false, cancellationToken: cancellationToken).ConfigureAwait(false); }
        catch { OpenIndexes(); throw; }
    }

    private readonly record struct Reference(PageId PageId, int SlotIndex, TransactionSequence Writer, ulong Revision)
    {
        internal ulong Location => GraphStorage.PackLocation(PageId, SlotIndex);
    }

    private readonly record struct Found(Reference Reference, GraphRecord Record);
}
