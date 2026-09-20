using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal sealed partial class GraphDatabaseInstance : IGraphDatabase
{
    private int _disposed;
    internal GraphDatabaseInstance(string name, IDatabaseEngine engine, GraphStorage storage, bool recover)
    {
        Name = name;
        Engine = engine;
        DataStorage = storage;
        Coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, storage.Records);
        var recovery = recover ? Coordinator.AnalyzeAndScrub() : null;
        Catalog = GraphCatalog.Open(storage, Coordinator);
        Store = GraphStore.Open(storage, Coordinator);
        if (recovery is not null)
        {
            Store.RecoverIndexesAsync(recovery.Aborted).AsTask().GetAwaiter().GetResult();
            Coordinator.CompleteRecovery();
        }
    }

    public DatabaseName Name { get; }
    public IDatabaseEngine Engine { get; }
    internal GraphStorage DataStorage { get; }
    internal TransactionCoordinator Coordinator { get; }
    internal IGraphCatalog Catalog { get; }
    internal IGraphStore Store { get; }

    public ValueTask<IDatabaseSession> CreateSessionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<IDatabaseSession>(new GraphDatabaseSession(this));
    }

    internal GraphDatabaseSession RequireSession(IDatabaseSession session)
    {
        ThrowIfDisposed();
        if (session is not GraphDatabaseSession graph || !ReferenceEquals(graph.Instance, this))
        {
            throw new DatabaseException("COHDBG005: The session belongs to another database or engine.");
        }
        graph.ThrowIfNotOpen();
        return graph;
    }

    public ValueTask<GraphNode> CreateNodeAsync(IDatabaseSession session, IReadOnlyList<string> labels,
        IReadOnlyDictionary<string, object?>? properties = null, CancellationToken cancellationToken = default)
        => RunAsync(RequireSession(session), operation => CreateNodeCoreAsync(operation, labels, properties, cancellationToken), cancellationToken);

    internal async ValueTask<GraphNode> CreateNodeCoreAsync(GraphOperation operation, IReadOnlyList<string> labels,
        IReadOnlyDictionary<string, object?>? properties, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(labels);
        await LockWriterAsync(operation.Context, token).ConfigureAwait(false);
        foreach (string label in labels)
        {
            var metadata = Catalog.FindLabel(label, operation.Context.Snapshot);
            if (metadata != Catalog.FindLabel(label, LatestSnapshot(operation.Context))) { ThrowConflict(); }
            if (metadata is null)
            {
                metadata = new GraphLabelMetadata(Guid.NewGuid(), label);
                await Catalog.SaveLabelAsync(metadata.Value, operation.Context, token).ConfigureAwait(false);
            }
            await ValidatePropertiesAsync(metadata.Value.Id, properties, operation, token).ConfigureAwait(false);
        }
        return Materialize(await Store.CreateNodeAsync(labels, properties ?? new Dictionary<string, object?>(), operation.Context, token).ConfigureAwait(false));
    }

    public ValueTask<GraphNode?> GetNodeAsync(IDatabaseSession session, GraphNodeId id, CancellationToken cancellationToken = default)
        => RunAsync(RequireSession(session), operation => new ValueTask<GraphNode?>(
            Store.FindNode(id.Value, operation.Context.Snapshot) is { } node ? Materialize(node) : null), cancellationToken);

    public ValueTask<bool> DeleteNodeAsync(IDatabaseSession session, GraphNodeId id, CancellationToken cancellationToken = default)
        => RunAsync(RequireSession(session), async operation =>
        {
            bool found = Store.FindNode(id.Value, operation.Context.Snapshot) is not null;
            await Store.DeleteNodeAsync(id.Value, true, operation.Context, cancellationToken).ConfigureAwait(false);
            return found;
        }, cancellationToken);

    public ValueTask<GraphRelationship> CreateRelationshipAsync(IDatabaseSession session, GraphNodeId from, GraphNodeId to,
        string type, IReadOnlyDictionary<string, object?>? properties = null, CancellationToken cancellationToken = default)
        => RunAsync(RequireSession(session), operation => CreateRelationshipCoreAsync(operation, from, to, type, properties, cancellationToken), cancellationToken);

    internal async ValueTask<GraphRelationship> CreateRelationshipCoreAsync(GraphOperation operation, GraphNodeId from, GraphNodeId to,
        string type, IReadOnlyDictionary<string, object?>? properties, CancellationToken token)
    {
        await LockWriterAsync(operation.Context, token).ConfigureAwait(false);
        var metadata = Catalog.FindRelationshipType(type, operation.Context.Snapshot);
        if (metadata != Catalog.FindRelationshipType(type, LatestSnapshot(operation.Context))) { ThrowConflict(); }
        if (metadata is null)
        {
            metadata = new GraphRelationshipTypeMetadata(Guid.NewGuid(), type);
            await Catalog.SaveRelationshipTypeAsync(metadata.Value, operation.Context, token).ConfigureAwait(false);
        }
        await ValidatePropertiesAsync(metadata.Value.Id, properties, operation, token).ConfigureAwait(false);
        return Materialize(await Store.CreateRelationshipAsync(from.Value, to.Value, type, properties ?? new Dictionary<string, object?>(), operation.Context, token).ConfigureAwait(false));
    }

    public ValueTask<bool> DeleteRelationshipAsync(IDatabaseSession session, GraphRelationshipId id, CancellationToken cancellationToken = default)
        => RunAsync(RequireSession(session), async operation =>
        {
            bool found = Store.FindRelationship(id.Value, operation.Context.Snapshot) is not null;
            await Store.DeleteRelationshipAsync(id.Value, operation.Context, cancellationToken).ConfigureAwait(false);
            return found;
        }, cancellationToken);

    public async IAsyncEnumerable<GraphNode> TraverseAsync(IDatabaseSession session, GraphTraversal traversal,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var graphSession = RequireSession(session);
        if (traversal.MaxDepth < 0 || !Enum.IsDefined(traversal.Direction))
        {
            throw new DatabaseException("COHDBG001: Traversal depth and direction must be valid.");
        }
        // Materialize in one statement snapshot, then release the transaction.
        // Each node is visited once; the start is seeded as visited and never emitted.
        var nodes = await RunAsync(graphSession, async operation =>
        {
            var result = new List<GraphNode>();
            var visited = new HashSet<ulong> { traversal.Start.Value };
            var queue = new Queue<(ulong Id, int Depth)>();
            if (Store.FindNode(traversal.Start.Value, operation.Context.Snapshot) is not null)
            {
                queue.Enqueue((traversal.Start.Value, 0));
            }
            while (queue.TryDequeue(out var current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (current.Depth >= traversal.MaxDepth) { continue; }
                foreach (var relationship in await Store.GetIncidentAsync(current.Id, operation.Context.Snapshot, cancellationToken).ConfigureAwait(false))
                {
                    if (traversal.RelationshipType is { } type && relationship.Type != type) { continue; }
                    bool outgoing = relationship.SourceId == current.Id;
                    if (traversal.Direction == GraphDirection.Outgoing && !outgoing ||
                        traversal.Direction == GraphDirection.Incoming && relationship.TargetId != current.Id) { continue; }
                    ulong next = outgoing ? relationship.TargetId : relationship.SourceId;
                    if (visited.Add(next) && Store.FindNode(next, operation.Context.Snapshot) is { } node)
                    {
                        result.Add(Materialize(node));
                        queue.Enqueue((next, current.Depth + 1));
                    }
                }
            }
            return result;
        }, cancellationToken).ConfigureAwait(false);
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            graphSession.ThrowIfNotOpen();
            yield return node;
        }
    }

    internal static GraphNode Materialize(StoredGraphNode node) => new(new GraphNodeId(node.Id), node.Labels, node.Properties);
    internal static GraphRelationship Materialize(StoredGraphRelationship relationship)
        => new(new GraphRelationshipId(relationship.Id), relationship.Type, new GraphNodeId(relationship.SourceId), new GraphNodeId(relationship.TargetId), relationship.Properties);

    private async ValueTask ValidatePropertiesAsync(Guid definition, IReadOnlyDictionary<string, object?>? properties, GraphOperation operation, CancellationToken token)
    {
        var keys = Catalog.GetPropertyKeys(definition, operation.Context.Snapshot);
        if (!keys.SequenceEqual(Catalog.GetPropertyKeys(definition, LatestSnapshot(operation.Context)))) { ThrowConflict(); }
        foreach (var key in keys)
        {
            object? value = null;
            bool present = properties?.TryGetValue(key.Name, out value) == true && value is not null;
            if (key.Required && !present || present && key.Type is { } type && type != TypeOf(value))
            {
                throw new DatabaseException($"COHDBG003: Property '{key.Name}' does not match the graph definition.");
            }
        }
        // Discovery records untyped keys; explicitly supplied metadata can constrain them.
        foreach (var property in properties ?? new Dictionary<string, object?>())
        {
            if (!keys.Any(key => key.Name == property.Key))
            {
                // Schema-owned definitions remain closed to ad-hoc metadata changes.
                bool schemaOwned = Catalog.GetLabels(operation.Context.Snapshot).Any(item => item.Id == definition && item.Owner == DatabaseObjectOwner.Schema) ||
                    Catalog.GetRelationshipTypes(operation.Context.Snapshot).Any(item => item.Id == definition && item.Owner == DatabaseObjectOwner.Schema);
                if (!schemaOwned)
                {
                    await Catalog.SavePropertyKeyAsync(new GraphPropertyKeyMetadata(definition, property.Key), operation.Context, token).ConfigureAwait(false);
                }
            }
        }
    }

    internal static DatabaseType TypeOf(object? value) => value switch
    {
        null => DatabaseType.Null, bool => DatabaseType.Boolean, sbyte => DatabaseType.Int8,
        byte or short => DatabaseType.Int16, ushort or int => DatabaseType.Int32, uint or long => DatabaseType.Int64,
        float => DatabaseType.Float32, double => DatabaseType.Float64, decimal or ulong => DatabaseType.Decimal,
        string => DatabaseType.String,
        _ => throw new DatabaseException("COHDBG003: Unsupported graph property value."),
    };

    internal static bool SupportsPropertyType(DatabaseType type) => type is DatabaseType.Boolean or DatabaseType.Int8 or
        DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64 or DatabaseType.Float32 or DatabaseType.Float64 or
        DatabaseType.Decimal or DatabaseType.String;
}
