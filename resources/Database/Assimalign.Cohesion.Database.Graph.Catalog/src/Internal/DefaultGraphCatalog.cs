using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Catalog;

internal sealed class DefaultGraphCatalog : IGraphCatalog
{
    private readonly GraphStorage _storage;
    private readonly TransactionCoordinator _coordinator;
    private readonly object _sync = new();
    private readonly Dictionary<(byte Kind, Guid Parent, string Name), List<Reference>> _versions = new();

    private DefaultGraphCatalog(GraphStorage storage, TransactionCoordinator coordinator)
        => (_storage, _coordinator) = (storage, coordinator);

    internal static DefaultGraphCatalog Open(GraphStorage storage, TransactionCoordinator coordinator)
    {
        var catalog = new DefaultGraphCatalog(storage, coordinator);
        using var iterator = storage.GetUnitIterator(0);
        while (iterator.MoveNext())
        {
            var unit = iterator.Current;
            var record = GraphCatalogCodec.Decode(unit.Data.Span);
            var (writer, _) = RecordVersionStamp.ReadStamps(unit.Data.Span);
            catalog.Add(record, new Reference(unit.PageId, unit.SlotIndex, writer));
        }
        return catalog;
    }

    public GraphLabelMetadata? FindLabel(string name, TransactionSnapshot snapshot)
        => Find(1, Guid.Empty, name, snapshot)?.Record.Label;

    public IReadOnlyList<GraphLabelMetadata> GetLabels(TransactionSnapshot snapshot)
        => List(1, null, snapshot).Select(item => item.Record.Label!.Value).ToArray();

    public GraphRelationshipTypeMetadata? FindRelationshipType(string name, TransactionSnapshot snapshot)
        => Find(2, Guid.Empty, name, snapshot)?.Record.RelationshipType;

    public IReadOnlyList<GraphRelationshipTypeMetadata> GetRelationshipTypes(TransactionSnapshot snapshot)
        => List(2, null, snapshot).Select(item => item.Record.RelationshipType!.Value).ToArray();

    public ValueTask SaveLabelAsync(GraphLabelMetadata label, ITransactionContext context, CancellationToken cancellationToken = default)
        => SaveDefinitionAsync(new CatalogRecord(Label: label), context, cancellationToken);

    public ValueTask SaveRelationshipTypeAsync(GraphRelationshipTypeMetadata relationshipType, ITransactionContext context, CancellationToken cancellationToken = default)
        => SaveDefinitionAsync(new CatalogRecord(RelationshipType: relationshipType), context, cancellationToken);

    public ValueTask DeleteLabelAsync(Guid id, ITransactionContext context, CancellationToken cancellationToken = default)
        => DeleteDefinitionAsync(1, id, context, cancellationToken);

    public ValueTask DeleteRelationshipTypeAsync(Guid id, ITransactionContext context, CancellationToken cancellationToken = default)
        => DeleteDefinitionAsync(2, id, context, cancellationToken);

    public IReadOnlyList<GraphPropertyKeyMetadata> GetPropertyKeys(Guid definitionId, TransactionSnapshot snapshot)
        => List(3, definitionId, snapshot).Select(item => item.Record.Property!.Value).ToArray();

    public ValueTask SavePropertyKeyAsync(GraphPropertyKeyMetadata property, ITransactionContext context, CancellationToken cancellationToken = default)
    {
        EnsureActive(context);
        var record = new CatalogRecord(Property: property);
        GraphCatalogCodec.Validate(record);
        RequireMutableDefinition(property.DefinitionId, context.Snapshot, "ALTER");
        EnsureParentUnchanged(property.DefinitionId, context);
        return SaveAsync(record, EnsureUnchanged(3, property.DefinitionId, property.Name, context), context, cancellationToken);
    }

    public ValueTask DeletePropertyKeyAsync(Guid definitionId, string name, ITransactionContext context, CancellationToken cancellationToken = default)
    {
        EnsureActive(context);
        RequireMutableDefinition(definitionId, context.Snapshot, "ALTER");
        EnsureParentUnchanged(definitionId, context);
        return DeleteAsync([EnsureUnchanged(3, definitionId, name, context)], context, cancellationToken);
    }

    public IReadOnlyList<GraphIndexMetadata> GetIndexes(Guid labelId, TransactionSnapshot snapshot)
        => List(4, labelId, snapshot).Select(item => item.Record.Index!.Value).ToArray();

    public ValueTask SaveIndexAsync(GraphIndexMetadata index, ITransactionContext context, CancellationToken cancellationToken = default)
    {
        EnsureActive(context);
        var record = new CatalogRecord(Index: index);
        GraphCatalogCodec.Validate(record);
        RequireMutableDefinition(index.LabelId, context.Snapshot, "ALTER", labelOnly: true);
        EnsureParentUnchanged(index.LabelId, context);
        var prior = EnsureUnchanged(4, index.LabelId, index.Name, context);
        if (prior?.Record.Index is { } previous && previous.PropertyKey != index.PropertyKey)
        {
            throw new GraphCatalogException($"Index '{index.Name}' already identifies property '{previous.PropertyKey}'.");
        }
        return SaveAsync(record, prior, context, cancellationToken);
    }

    public ValueTask DeleteIndexAsync(Guid labelId, string name, ITransactionContext context, CancellationToken cancellationToken = default)
    {
        EnsureActive(context);
        RequireMutableDefinition(labelId, context.Snapshot, "ALTER", labelOnly: true);
        EnsureParentUnchanged(labelId, context);
        return DeleteAsync([EnsureUnchanged(4, labelId, name, context)], context, cancellationToken);
    }

    private ValueTask SaveDefinitionAsync(CatalogRecord record, ITransactionContext context, CancellationToken cancellationToken)
    {
        EnsureActive(context);
        GraphCatalogCodec.Validate(record);
        var current = Find(record.Kind, Guid.Empty, record.Name, Latest(context));
        if (current is { } latest)
        {
            RefuseSchemaOwner(latest.Record, "ALTER");
        }
        var previous = EnsureUnchanged(record.Kind, Guid.Empty, record.Name, context);
        if (previous is { } found)
        {
            RefuseSchemaOwner(found.Record, "ALTER");
            if (found.Record.Id != record.Id)
            {
                throw new GraphCatalogException($"Definition '{record.Name}' already has a different identity.");
            }
        }
        var latestSnapshot = Latest(context);
        var sameIdentity = List(1, null, latestSnapshot).Concat(List(2, null, latestSnapshot))
            .FirstOrDefault(item => item.Record.Id == record.Id);
        if (sameIdentity.Record is { } same && (same.Kind != record.Kind || same.Name != record.Name))
        {
            throw new GraphCatalogException("A definition identity cannot be reused or renamed.");
        }
        return SaveAsync(record, previous, context, cancellationToken);
    }

    private ValueTask DeleteDefinitionAsync(byte kind, Guid id, ITransactionContext context, CancellationToken cancellationToken)
    {
        EnsureActive(context);
        var previous = List(kind, null, context.Snapshot).FirstOrDefault(item => item.Record.Id == id);
        var latestSnapshot = Latest(context);
        var latest = List(kind, null, latestSnapshot).FirstOrDefault(item => item.Record.Id == id);
        if (latest.Record is not null)
        {
            RefuseSchemaOwner(latest.Record, "DROP");
        }
        if (previous.Record?.Id != latest.Record?.Id || previous.Reference != latest.Reference)
        {
            throw Conflict();
        }
        if (previous.Record is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
        RefuseSchemaOwner(previous.Record, "DROP");
        var records = new List<Found?> { previous };
        foreach (byte childKind in new byte[] { 3, 4 })
        {
            var children = List(childKind, id, context.Snapshot);
            var latestChildren = List(childKind, id, latestSnapshot);
            if (!children.Select(item => item.Reference).SequenceEqual(latestChildren.Select(item => item.Reference)))
            {
                throw Conflict();
            }
            records.AddRange(children.Select(item => (Found?)item));
        }
        return DeleteAsync(records, context, cancellationToken);
    }

    private TransactionSnapshot Latest(ITransactionContext context)
        => new(context.Sequence, TransactionSequence.None, new TransactionSequence(ulong.MaxValue),
            _coordinator.GetOpenContexts().Where(item => item.State == TransactionState.Active).Select(item => item.Sequence));

    private Found? EnsureUnchanged(byte kind, Guid parent, string name, ITransactionContext context)
    {
        var previous = Find(kind, parent, name, context.Snapshot);
        var latest = Find(kind, parent, name, Latest(context));
        if (previous?.Reference != latest?.Reference)
        {
            throw Conflict();
        }
        return previous;
    }

    private void EnsureParentUnchanged(Guid id, ITransactionContext context)
    {
        var latestSnapshot = Latest(context);
        var previous = List(1, null, context.Snapshot).Concat(List(2, null, context.Snapshot))
            .FirstOrDefault(item => item.Record.Id == id);
        var latest = List(1, null, latestSnapshot).Concat(List(2, null, latestSnapshot))
            .FirstOrDefault(item => item.Record.Id == id);
        if (latest.Record is not null)
        {
            RefuseSchemaOwner(latest.Record, "ALTER");
        }
        if (previous.Record?.Id != latest.Record?.Id || previous.Reference != latest.Reference)
        {
            throw Conflict();
        }
    }

    private static GraphCatalogException Conflict()
        => new("Graph catalog write conflict: the definition changed after the transaction snapshot.");

    private void RequireMutableDefinition(Guid id, TransactionSnapshot snapshot, string operation, bool labelOnly = false)
    {
        var definitions = List(1, null, snapshot);
        var found = definitions.FirstOrDefault(item => item.Record.Id == id);
        if (found.Record is null && !labelOnly)
        {
            found = List(2, null, snapshot).FirstOrDefault(item => item.Record.Id == id);
        }
        if (found.Record is null)
        {
            throw new GraphCatalogException("The owning graph definition does not exist.");
        }
        RefuseSchemaOwner(found.Record, operation);
    }

    private static void RefuseSchemaOwner(CatalogRecord record, string operation)
    {
        if (record.Owner == DatabaseObjectOwner.Schema)
        {
            throw new DatabaseObjectLockedException(record.Name, record.OwningSchema!,
                operation + (record.Kind == 1 ? " LABEL" : " RELATIONSHIP TYPE"));
        }
    }

    private async ValueTask SaveAsync(CatalogRecord record, Found? previous, ITransactionContext context, CancellationToken cancellationToken)
    {
        byte[] bytes = GraphCatalogCodec.Encode(record, context.Sequence);
        var location = await _coordinator.ApplyStatementAsync(context, bracket =>
        {
            if (previous is { } old)
            {
                Tombstone(bracket, old.Reference, context.Sequence);
            }
            var inserted = _storage.InsertEntry(bracket, bytes);
            _coordinator.VersionStore.RecordCreated(context.Sequence, inserted.PageId, inserted.SlotIndex);
            return ValueTask.FromResult(inserted);
        }, durable: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            Add(record, new Reference(location.PageId, location.SlotIndex, context.Sequence));
        }
    }

    private async ValueTask DeleteAsync(IEnumerable<Found?> records, ITransactionContext context, CancellationToken cancellationToken)
    {
        await _coordinator.ApplyStatementAsync(context, bracket =>
        {
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (record is { } found)
                {
                    Tombstone(bracket, found.Reference, context.Sequence);
                }
            }
            return ValueTask.FromResult(true);
        }, durable: false, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private void Tombstone(IStorageTransaction bracket, Reference reference, TransactionSequence writer)
    {
        var bytes = _storage.ReadEntry(reference.PageId, reference.SlotIndex);
        _storage.UpdateEntry(bracket, reference.PageId, reference.SlotIndex, RecordVersionStamp.WithDeleter(bytes.Span, writer));
        _coordinator.VersionStore.RecordTombstoned(writer, reference.PageId, reference.SlotIndex);
    }

    private void Add(CatalogRecord record, Reference reference)
    {
        var key = (record.Kind, record.Parent, record.Name);
        if (!_versions.TryGetValue(key, out var versions))
        {
            _versions[key] = versions = [];
        }
        versions.RemoveAll(item => item.PageId == reference.PageId && item.SlotIndex == reference.SlotIndex);
        versions.Add(reference);
    }

    private Found? Find(byte kind, Guid parent, string name, TransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            var key = (kind, parent, name);
            if (!_versions.TryGetValue(key, out var versions))
            {
                return null;
            }
            Found? result = null;
            for (int index = versions.Count - 1; index >= 0; index--)
            {
                var reference = versions[index];
                ReadOnlyMemory<byte> bytes;
                if (!_storage.FreeSpaceMap.IsAllocated(reference.PageId))
                {
                    versions.RemoveAt(index);
                    continue;
                }
                try
                {
                    using var handle = _storage.PageManager.GetPage(reference.PageId);
                    if (handle.Page.Type != PageType.Data || handle.Page.OwnerId != 0)
                    {
                        versions.RemoveAt(index);
                        continue;
                    }
                    bytes = _storage.ReadEntry(reference.PageId, reference.SlotIndex);
                }
                catch (SlottedPageException)
                {
                    versions.RemoveAt(index);
                    continue;
                }
                catch (ArgumentOutOfRangeException)
                {
                    versions.RemoveAt(index);
                    continue;
                }
                catch (StorageIOException) when (!_storage.FreeSpaceMap.IsAllocated(reference.PageId))
                {
                    versions.RemoveAt(index);
                    continue;
                }
                if (bytes.Length < RecordVersionStamp.HeaderSize + 2)
                {
                    throw new GraphCatalogException("Truncated graph catalog record.");
                }
                var (writer, deleter) = RecordVersionStamp.ReadStamps(bytes.Span);
                if (writer != reference.Writer)
                {
                    versions.RemoveAt(index);
                    continue;
                }
                var record = GraphCatalogCodec.Decode(bytes.Span);
                if ((record.Kind, record.Parent, record.Name) != key)
                {
                    versions.RemoveAt(index);
                    continue;
                }
                if (snapshot.IsVisible(writer) && (deleter == TransactionSequence.None || !snapshot.IsVisible(deleter))
                    && (result is null || writer > result.Value.Reference.Writer))
                {
                    result = new Found(reference, record);
                }
            }
            return result;
        }
    }

    private IReadOnlyList<Found> List(byte kind, Guid? parent, TransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            var result = new List<Found>();
            foreach (var key in _versions.Keys.Where(key => key.Kind == kind && (parent is null || key.Parent == parent))
                .OrderBy(key => key.Name, StringComparer.Ordinal).ToArray())
            {
                if (Find(key.Kind, key.Parent, key.Name, snapshot) is { } found)
                {
                    result.Add(found);
                }
            }
            return result;
        }
    }

    private static void EnsureActive(ITransactionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.State != TransactionState.Active)
        {
            throw new InvalidOperationException("Catalog mutations require an active transaction.");
        }
    }

    private readonly record struct Reference(PageId PageId, int SlotIndex, TransactionSequence Writer);
    private readonly record struct Found(Reference Reference, CatalogRecord Record);
}

internal sealed record CatalogRecord(GraphLabelMetadata? Label = null, GraphRelationshipTypeMetadata? RelationshipType = null,
    GraphPropertyKeyMetadata? Property = null, GraphIndexMetadata? Index = null)
{
    internal byte Kind => Label is not null ? (byte)1 : RelationshipType is not null ? (byte)2 : Property is not null ? (byte)3 : (byte)4;
    internal Guid Id => Label?.Id ?? RelationshipType?.Id ?? Property?.DefinitionId ?? Index!.Value.LabelId;
    internal Guid Parent => Kind <= 2 ? Guid.Empty : Id;
    internal string Name => Label?.Name ?? RelationshipType?.Name ?? Property?.Name ?? Index!.Value.Name;
    internal DatabaseObjectOwner Owner => Label?.Owner ?? RelationshipType?.Owner ?? DatabaseObjectOwner.Adhoc;
    internal string? OwningSchema => Label?.OwningSchema ?? RelationshipType?.OwningSchema;
}
