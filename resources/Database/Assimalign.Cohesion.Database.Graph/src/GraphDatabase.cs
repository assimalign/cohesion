using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>
/// A property-graph database: labeled nodes connected by typed, directed relationships, both
/// carrying properties.
/// </summary>
/// <remarks>
/// <para>
/// A database composes its graph storage, the transaction coordinator every session binds to,
/// the catalog of labels, relationship types, property keys and indexes
/// (<see cref="GraphCatalog"/>), and the store of nodes, relationships, adjacency and property
/// indexes (<see cref="GraphStore"/>). The typed node and relationship members run as statements
/// of the given session, so they follow its transaction exactly as GQL does: a failure inside an
/// explicit transaction aborts it (#1188).
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 4, #1260).</b> A public sealed leaf of
/// <see cref="DatabaseInstance"/> with an internal constructor, replacing the former
/// <c>IGraphDatabase</c> interface and its internal implementation; the engine creates and opens
/// it. The base owns the name, the owning engine (re-exposed typed with <c>new</c>) and the
/// disposed flag. Disposing it outside the engine closes it for every session; once the close
/// ends the engine forgets it (owner decision 33 of 2026-10-06, #1289), and
/// <see cref="GraphDatabaseEngine.OpenDatabaseAsync(DatabaseName, System.Threading.CancellationToken)"/>
/// opens it again from its files, with its nodes and relationships, as a new instance. Until then
/// its workers skip it, so the engine stays <see cref="EngineState.Running"/>.
/// </para>
/// </remarks>
public sealed partial class GraphDatabase : DatabaseInstance
{
    private readonly GraphDatabaseEngine _engine;

    internal GraphDatabase(DatabaseName name, GraphDatabaseEngine engine, GraphStorage storage, bool recover)
        : base(name, engine)
    {
        _engine = engine;
        DataStorage = storage;
        Coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, storage.Records);

        // A wait for the database writer lock ends when the database goes offline (#1268 review):
        // an offline database undoes nothing, so the writer that holds the lock keeps it until the
        // reopen, and a writer queued behind it would otherwise wait that long.
        storage.OnOffline = Coordinator.AbandonLockWaits;
        if (storage.OfflineError is { } alreadyOffline)
        {
            Coordinator.AbandonLockWaits(alreadyOffline);
        }

        // A deferred undo is retried on its own backoff, from about 100 ms up to the
        // maintenance interval, and the purge worker wakes for it (#1226).
        Coordinator.DeferredUndoRetryLimit = engine.EngineOptions.MaintenanceInterval;
        Coordinator.DeferredUndoRetryDelay = engine.EngineOptions.DeferredUndoRetryDelay;
        Coordinator.OnUndoDeferred = engine.UndoDeferredSignal.Set;

        // Indexing owns the B-tree page format (#1194) and checks each tree's root
        // page as it attaches the tree. That happens inside the store's open, after
        // the recovery scrub has written to the database, so the check runs here
        // first: a database whose indexes this engine cannot read is refused before
        // anything is written to it.
        try
        {
            GraphStore.EnsureIndexFormat(storage);
        }
        catch (Indexing.IndexFormatException exception)
        {
            Coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw new DatabaseException($"Database '{name}' cannot be opened. {exception.Message}", exception);
        }

        var recovery = recover ? Coordinator.AnalyzeAndScrub() : null;
        Catalog = GraphCatalog.Open(storage, Coordinator);
        Store = GraphStore.Open(storage, Coordinator);
        if (recovery is not null)
        {
            // The stop closes the activity the start opened on this flow even when the recovery
            // throws; the root's failed open reports the failure itself.
            long recoveryStarted = GraphDatabaseEventSource.Log.IndexRecoveryStart(name, recovery.Aborted.Count);
            try
            {
                Store.RecoverIndexesAsync(recovery.Aborted).AsTask().GetAwaiter().GetResult();
                Coordinator.CompleteRecovery();
            }
            finally
            {
                GraphDatabaseEventSource.Log.IndexRecoveryStop(name, recoveryStarted);
            }
        }
    }

    /// <summary>
    /// Gets the graph engine that owns this database.
    /// </summary>
    public new GraphDatabaseEngine Engine => _engine;

    /// <summary>
    /// Gets the data storage file set, for the engine's background workers.
    /// </summary>
    internal GraphStorage DataStorage { get; }

    /// <summary>
    /// Gets the database's transaction coordinator (the MVCC composition sessions bind to), for
    /// the engine's background workers and tests.
    /// </summary>
    internal TransactionCoordinator Coordinator { get; }

    /// <summary>
    /// Gets the database's catalog of labels, relationship types, property keys and indexes.
    /// </summary>
    internal GraphCatalog Catalog { get; }

    /// <summary>
    /// Gets the database's store of nodes, relationships, adjacency and property indexes.
    /// </summary>
    internal GraphStore Store { get; }

    /// <summary>
    /// The code that leads the message of every operation refused because the database is
    /// offline (#1243).
    /// </summary>
    internal const string OfflineCode = "COHDBG012";

    /// <summary>
    /// Gets whether a failed durable flush took the database offline.
    /// </summary>
    internal bool IsOffline => DataStorage.IsOffline;

    /// <summary>
    /// Gets whether the database's close has started: by the engine, or by a holder of the
    /// database (a session's <see cref="GraphDatabaseSession.Database"/> is the same instance).
    /// The engine keeps a database its holder is closing registered until the close ends, then
    /// forgets it; its workers skip it meanwhile.
    /// </summary>
    internal bool IsClosed => IsDisposed;

    /// <summary>
    /// Creates a new lightweight graph session scoped to this database.
    /// </summary>
    /// <param name="cancellationToken">Observed before the session is created.</param>
    /// <returns>A new session.</returns>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBG012</c>, #1243).</exception>
    public new async ValueTask<GraphDatabaseSession> CreateSessionAsync(CancellationToken cancellationToken = default)
        => (GraphDatabaseSession)await base.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Refuses an operation on an offline database with <see cref="DatabaseOfflineException"/>
    /// (<see cref="OfflineCode"/>): every operation, in process and over the wire server, until
    /// the database is reopened.
    /// </summary>
    /// <exception cref="DatabaseOfflineException">The database is offline.</exception>
    internal void ThrowIfOffline()
    {
        if (GetOfflineRefusal() is { } refusal)
        {
            throw refusal;
        }
    }

    /// <summary>
    /// Gets the coded refusal (<see cref="OfflineCode"/>) of an operation on the database while it
    /// is offline, or null while it is online: the vocabulary the root transaction base reads
    /// before a commit or rollback, and before each kernel rollback, which an offline database skips.
    /// </summary>
    /// <returns>The refusal, or null.</returns>
    internal DatabaseOfflineException? GetOfflineRefusal()
        => DataStorage.OfflineError is { } error ? DatabaseOfflineException.Create(OfflineCode, Name, error) : null;

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> once the database has been disposed: the base's
    /// check, for the model's sessions, statements and engine.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    internal void EnsureNotDisposed() => ThrowIfDisposed();

    /// <summary>
    /// Translates a failure the storage's offline state caused into the coded refusal
    /// (<see cref="DatabaseOfflineException"/>), or into
    /// <see cref="DatabaseTransactionCommitUnconfirmedException"/> when a storage commit record
    /// was written before its flush failed
    /// (<see cref="Assimalign.Cohesion.Database.Storage.StorageOfflineException.CommitRecordWritten"/>),
    /// so the work may survive the reopen. An unconfirmed commit that already has its own type is
    /// returned unchanged, and so is any other failure.
    /// </summary>
    /// <param name="error">The failure to translate.</param>
    /// <returns>The translated failure, or <paramref name="error"/> itself.</returns>
    internal Exception TranslateOffline(Exception error)
    {
        if (error is DatabaseOfflineException or DatabaseTransactionCommitUnconfirmedException or TransactionCommitUnconfirmedException
            || Assimalign.Cohesion.Database.Storage.StorageOfflineException.Find(error) is not { } offline)
        {
            return error;
        }

        return offline.CommitRecordWritten
            ? DatabaseTransactionCommitUnconfirmedException.Create(OfflineCode, Name, offline)
            : DatabaseOfflineException.Create(OfflineCode, Name, DataStorage.OfflineError ?? offline);
    }

    /// <summary>
    /// Creates a node with the specified labels and properties.
    /// </summary>
    /// <param name="session">The session the write executes in.</param>
    /// <param name="labels">The labels applied to the node.</param>
    /// <param name="properties">The node's properties, or null for none.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The created node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> or <paramref name="labels"/> or one of its labels is null.</exception>
    /// <exception cref="ArgumentException">A label is empty or whitespace.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">
    /// The session belongs to another database (<c>COHDBG005</c>), it is closed, another statement
    /// holds it, its transaction refuses statements (<c>COHDBG007</c>), or the write failed (a
    /// property that does not match the graph definition, <c>COHDBG003</c>).
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBG012</c>, #1243).</exception>
    /// <exception cref="DatabaseTransactionAbortedException">The graph catalog changed since the transaction's snapshot (retryable).</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">An autocommit statement's commit record could not be confirmed durable.</exception>
    public ValueTask<GraphNode> CreateNodeAsync(GraphDatabaseSession session, IReadOnlyList<string> labels,
        IReadOnlyDictionary<string, object?>? properties = null, CancellationToken cancellationToken = default)
    {
        RequireOwnSession(session);
        // Argument validation runs before the statement starts, so it never aborts an explicit
        // transaction. The catalog rejects the same names inside the statement.
        ArgumentNullException.ThrowIfNull(labels);
        foreach (string label in labels)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(label, nameof(labels));
        }
        return RunAsync(session, operation => CreateNodeCoreAsync(operation, labels, properties, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Reads a node by identity.
    /// </summary>
    /// <param name="session">The session the read executes in.</param>
    /// <param name="id">The node identity.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The node, or null when no visible node has the identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">
    /// The session belongs to another database (<c>COHDBG005</c>), it is closed, another statement
    /// holds it, or its transaction refuses statements (<c>COHDBG007</c>).
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBG012</c>, #1243).</exception>
    public ValueTask<GraphNode?> GetNodeAsync(GraphDatabaseSession session, GraphNodeId id, CancellationToken cancellationToken = default)
        => RunAsync(RequireOwnSession(session), operation => new ValueTask<GraphNode?>(
            Store.FindNode(id.Value, operation.Context.Snapshot) is { } node ? Materialize(node) : null), cancellationToken);

    /// <summary>
    /// Deletes a node and its attached relationships.
    /// </summary>
    /// <param name="session">The session the delete executes in.</param>
    /// <param name="id">The node identity.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True when a node was deleted; false when none was visible.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">
    /// The session belongs to another database (<c>COHDBG005</c>), it is closed, another statement
    /// holds it, its transaction refuses statements (<c>COHDBG007</c>), or the delete failed.
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBG012</c>, #1243).</exception>
    /// <exception cref="DatabaseTransactionAbortedException">The node or one of its relationships changed since the transaction's snapshot (retryable).</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">An autocommit statement's commit record could not be confirmed durable.</exception>
    public ValueTask<bool> DeleteNodeAsync(GraphDatabaseSession session, GraphNodeId id, CancellationToken cancellationToken = default)
        => RunAsync(RequireOwnSession(session), async operation =>
        {
            bool found = Store.FindNode(id.Value, operation.Context.Snapshot) is not null;
            await Store.DeleteNodeAsync(id.Value, true, operation.Context, cancellationToken).ConfigureAwait(false);
            return found;
        }, cancellationToken);

    /// <summary>
    /// Creates a typed, directed relationship between two nodes.
    /// </summary>
    /// <param name="session">The session the write executes in.</param>
    /// <param name="from">The origin node.</param>
    /// <param name="to">The target node.</param>
    /// <param name="type">The relationship type.</param>
    /// <param name="properties">The relationship's properties, or null for none.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The created relationship.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> or <paramref name="type"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="type"/> is empty or whitespace.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">
    /// Either endpoint node does not exist, or a property does not match the graph definition
    /// (both <c>COHDBG003</c>); the session belongs to another database (<c>COHDBG005</c>), it is
    /// closed, another statement holds it, or its transaction refuses statements (<c>COHDBG007</c>).
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBG012</c>, #1243).</exception>
    /// <exception cref="DatabaseTransactionAbortedException">The graph catalog or an endpoint changed since the transaction's snapshot (retryable).</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">An autocommit statement's commit record could not be confirmed durable.</exception>
    public ValueTask<GraphRelationship> CreateRelationshipAsync(GraphDatabaseSession session, GraphNodeId from, GraphNodeId to,
        string type, IReadOnlyDictionary<string, object?>? properties = null, CancellationToken cancellationToken = default)
    {
        RequireOwnSession(session);
        // Argument validation runs before the statement starts, so it never aborts an explicit transaction.
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        return RunAsync(session, operation => CreateRelationshipCoreAsync(operation, from, to, type, properties, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Deletes a relationship by identity.
    /// </summary>
    /// <param name="session">The session the delete executes in.</param>
    /// <param name="id">The relationship identity.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True when a relationship was deleted; false when none was visible.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">
    /// The session belongs to another database (<c>COHDBG005</c>), it is closed, another statement
    /// holds it, its transaction refuses statements (<c>COHDBG007</c>), or the delete failed.
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBG012</c>, #1243).</exception>
    /// <exception cref="DatabaseTransactionAbortedException">The relationship changed since the transaction's snapshot (retryable).</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">An autocommit statement's commit record could not be confirmed durable.</exception>
    public ValueTask<bool> DeleteRelationshipAsync(GraphDatabaseSession session, GraphRelationshipId id, CancellationToken cancellationToken = default)
        => RunAsync(RequireOwnSession(session), async operation =>
        {
            bool found = Store.FindRelationship(id.Value, operation.Context.Snapshot) is not null;
            await Store.DeleteRelationshipAsync(id.Value, operation.Context, cancellationToken).ConfigureAwait(false);
            return found;
        }, cancellationToken);

    /// <summary>
    /// Traverses the graph from a starting node, streaming visited nodes. The traversal runs as one
    /// statement in one snapshot when the enumeration starts; the nodes are then yielded.
    /// </summary>
    /// <param name="session">The session the traversal executes in.</param>
    /// <param name="traversal">The traversal specification.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>An async sequence of visited nodes, excluding the start node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was canceled, while the traversal runs or while its
    /// nodes are yielded.
    /// </exception>
    /// <exception cref="DatabaseException">
    /// The traversal's depth or direction is invalid (<c>COHDBG001</c>); the session belongs to
    /// another database (<c>COHDBG005</c>), it is closed (also while the nodes are yielded), another
    /// statement holds it, or its transaction refuses statements (<c>COHDBG007</c>).
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBG012</c>, #1243).</exception>
    public async IAsyncEnumerable<GraphNode> TraverseAsync(GraphDatabaseSession session, GraphTraversal traversal,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        RequireOwnSession(session);
        if (traversal.MaxDepth < 0 || !Enum.IsDefined(traversal.Direction))
        {
            throw new DatabaseException("COHDBG001: Traversal depth and direction must be valid.");
        }
        // Materialize in one statement snapshot, then release the transaction.
        // Each node is visited once; the start is seeded as visited and never emitted.
        var nodes = await RunAsync(session, async operation =>
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
            session.ThrowIfNotOpen();
            yield return node;
        }
    }

    /// <summary>
    /// Checks that a typed operation's session is open and belongs to this database, before the
    /// operation's statement starts.
    /// </summary>
    /// <param name="session">The session the caller passed.</param>
    /// <returns><paramref name="session"/>.</returns>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    /// <exception cref="DatabaseException">The session belongs to another database, or it is closed.</exception>
    internal GraphDatabaseSession RequireOwnSession(GraphDatabaseSession session)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(session);
        if (!ReferenceEquals(session.Database, this))
        {
            throw new DatabaseException("COHDBG005: The session belongs to another database or engine.");
        }
        session.ThrowIfNotOpen();
        return session;
    }

    internal async ValueTask<GraphNode> CreateNodeCoreAsync(GraphOperation operation, IReadOnlyList<string> labels,
        IReadOnlyDictionary<string, object?>? properties, CancellationToken token)
    {
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

    /// <inheritdoc />
    protected override ValueTask<DatabaseSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
    {
        ThrowIfOffline();
        return new ValueTask<DatabaseSession>(new GraphDatabaseSession(this));
    }
}
