using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Graph.Internal;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>Database-scoped label, relationship-type and index management bound to one session.</summary>
/// <remarks>
/// <para>
/// Every operation runs as a statement of the session it is bound to, so it follows the session's
/// transaction exactly as GQL does: a failed write inside an explicit transaction aborts it
/// (#1188), and a read of a label the database does not have reports a warning instead of
/// failing.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 4, #1260).</b> One sealed type with a private constructor
/// behind <see cref="Open"/>: it replaces the former <c>IGraphSchema</c> interface, the
/// <c>GraphSchema</c> static factory and their internal implementation (the triplet of
/// <c>database-area.md</c> rule 1), and <see cref="Open"/> takes the typed database and session.
/// </para>
/// </remarks>
public sealed class GraphSchema
{
    private readonly GraphDatabase _database;
    private readonly GraphDatabaseSession _session;

    private GraphSchema(GraphDatabase database, GraphDatabaseSession session)
    {
        _database = database;
        _session = session;
    }

    /// <summary>Binds metadata operations to a graph database session.</summary>
    /// <param name="database">The database to address.</param>
    /// <param name="session">A session belonging to that database.</param>
    /// <returns>Session-bound graph metadata operations.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> or <paramref name="session"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="DatabaseException">The session belongs to another database (<c>COHDBG005</c>), or it is closed.</exception>
    public static GraphSchema Open(GraphDatabase database, GraphDatabaseSession session)
    {
        ArgumentNullException.ThrowIfNull(database);
        return new GraphSchema(database, database.RequireOwnSession(session));
    }

    /// <summary>Lists visible labels.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The visible label definitions.</returns>
    public ValueTask<IReadOnlyList<GraphLabelMetadata>> GetLabelsAsync(CancellationToken cancellationToken = default)
        => _database.RunAsync(_session, op => new ValueTask<IReadOnlyList<GraphLabelMetadata>>(_database.Catalog.GetLabels(op.Context.Snapshot)), cancellationToken);

    /// <summary>Lists visible relationship types.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The visible relationship type definitions.</returns>
    public ValueTask<IReadOnlyList<GraphRelationshipTypeMetadata>> GetRelationshipTypesAsync(CancellationToken cancellationToken = default)
        => _database.RunAsync(_session, op => new ValueTask<IReadOnlyList<GraphRelationshipTypeMetadata>>(_database.Catalog.GetRelationshipTypes(op.Context.Snapshot)), cancellationToken);

    /// <summary>Lists property metadata for a label or relationship type.</summary>
    /// <param name="definitionId">The label or relationship type identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The visible property definitions.</returns>
    public ValueTask<IReadOnlyList<GraphPropertyKeyMetadata>> GetPropertyKeysAsync(Guid definitionId, CancellationToken cancellationToken = default)
        => _database.RunAsync(_session, op => new ValueTask<IReadOnlyList<GraphPropertyKeyMetadata>>(_database.Catalog.GetPropertyKeys(definitionId, op.Context.Snapshot)), cancellationToken);

    /// <summary>Lists a label's property indexes.</summary>
    /// <param name="label">The label name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The visible indexes. For a label the database does not have, no indexes and a
    /// <c>COHDBG010</c> warning in <see cref="GraphSchemaResult{T}.Diagnostics"/>: the read does
    /// not fail, so it leaves an explicit transaction active.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="label"/> is null; checked before the read starts.</exception>
    public ValueTask<GraphSchemaResult<GraphIndexMetadata>> GetIndexesAsync(string label, CancellationToken cancellationToken = default)
    {
        // Argument validation runs before the read starts, so it never aborts an explicit transaction.
        ArgumentNullException.ThrowIfNull(label);
        // A read of a label the database does not have is empty with a warning, never a failure
        // (#1228): Neo4j's schema API returns an empty list for a label token that does not exist
        // (kernel/.../coreapi/schema/SchemaImpl.java:142-154).
        return _database.RunAsync(_session, op => new ValueTask<GraphSchemaResult<GraphIndexMetadata>>(
            _database.Catalog.FindLabel(label, op.Context.Snapshot) is { } metadata
                ? new GraphSchemaResult<GraphIndexMetadata>(_database.Catalog.GetIndexes(metadata.Id, op.Context.Snapshot), [])
                : new GraphSchemaResult<GraphIndexMetadata>([], [GraphTokenResolver.UnknownLabel(label, null)])), cancellationToken);
    }

    /// <summary>Creates or alters a label definition.</summary>
    /// <param name="definition">The definition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completion of the mutation.</returns>
    /// <exception cref="DatabaseObjectLockedException">The existing label is schema owned.</exception>
    public ValueTask SaveLabelAsync(GraphLabelMetadata definition, CancellationToken cancellationToken = default)
        => Write(op => _database.Catalog.SaveLabelAsync(definition, op.Context, cancellationToken), cancellationToken);

    /// <summary>Creates or alters a relationship type definition.</summary>
    /// <param name="definition">The definition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completion of the mutation.</returns>
    /// <exception cref="DatabaseObjectLockedException">The existing type is schema owned.</exception>
    public ValueTask SaveRelationshipTypeAsync(GraphRelationshipTypeMetadata definition, CancellationToken cancellationToken = default)
        => Write(op => _database.Catalog.SaveRelationshipTypeAsync(definition, op.Context, cancellationToken), cancellationToken);

    /// <summary>Creates or alters property-key metadata.</summary>
    /// <param name="definition">The property definition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completion of the mutation.</returns>
    /// <exception cref="DatabaseObjectLockedException">The owning definition is schema owned.</exception>
    public ValueTask SavePropertyKeyAsync(GraphPropertyKeyMetadata definition, CancellationToken cancellationToken = default)
        => Write(async op =>
        {
            var latest = _database.LatestSnapshot(op.Context);
            var label = _database.Catalog.GetLabels(latest).FirstOrDefault(item => item.Id == definition.DefinitionId);
            var type = _database.Catalog.GetRelationshipTypes(latest).FirstOrDefault(item => item.Id == definition.DefinitionId);
            if (label.Id != Guid.Empty) { EnsureMutable(label.Name, label.Owner, label.OwningSchema, "ALTER LABEL"); }
            if (type.Id != Guid.Empty) { EnsureMutable(type.Name, type.Owner, type.OwningSchema, "ALTER RELATIONSHIP TYPE"); }
            if (definition.Type is { } scalarType && !GraphDatabase.SupportsPropertyType(scalarType))
            { throw new DatabaseException($"COHDBG003: Property type '{scalarType}' is not supported by graph storage."); }
            foreach (var node in _database.Store.GetNodes(label.Id == Guid.Empty ? null : label.Name, latest))
            {
                if (label.Id != Guid.Empty) { Validate(node.Properties); }
                else if (type.Id != Guid.Empty)
                {
                    foreach (var edge in await _database.Store.GetIncidentAsync(node.Id, latest, cancellationToken).ConfigureAwait(false))
                    { if (edge.Type == type.Name) { Validate(edge.Properties); } }
                }
            }
            await _database.Catalog.SavePropertyKeyAsync(definition, op.Context, cancellationToken).ConfigureAwait(false);
            void Validate(IReadOnlyDictionary<string, object?> properties)
            {
                bool present = properties.TryGetValue(definition.Name, out var value) && value is not null;
                if (definition.Required && !present || present && definition.Type is { } expected && GraphDatabase.TypeOf(value) != expected)
                { throw new DatabaseException($"COHDBG003: Existing graph data violates property '{definition.Name}'."); }
            }
        }, cancellationToken);

    /// <summary>Builds a B+Tree index over a node property, including existing nodes.</summary>
    /// <param name="label">The indexed label.</param>
    /// <param name="name">The index name.</param>
    /// <param name="propertyKey">The property name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completion of index creation.</returns>
    /// <exception cref="DatabaseObjectLockedException">The label is schema owned.</exception>
    public ValueTask CreateIndexAsync(string label, string name, string propertyKey, CancellationToken cancellationToken = default)
        => Write(async op =>
        {
            var metadata = Label(label, op);
            await _database.Catalog.SaveIndexAsync(new GraphIndexMetadata(metadata.Id, name, propertyKey), op.Context, cancellationToken).ConfigureAwait(false);
            await _database.Store.CreateIndexAsync(label, propertyKey, op.Context, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Drops an unused label definition.</summary>
    /// <param name="name">The label name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completion of the mutation.</returns>
    /// <exception cref="DatabaseObjectLockedException">The label is schema owned.</exception>
    /// <exception cref="DatabaseException">The label is missing or in use.</exception>
    public ValueTask DropLabelAsync(string name, CancellationToken cancellationToken = default)
        => Write(async op =>
        {
            var label = Label(name, op);
            EnsureMutable(label.Name, label.Owner, label.OwningSchema, "DROP LABEL");
            if (_database.Store.GetNodes(name, _database.LatestSnapshot(op.Context)).Count != 0) { throw new DatabaseException("COHDBG003: The label is in use."); }
            foreach (var index in _database.Catalog.GetIndexes(label.Id, op.Context.Snapshot))
            {
                await _database.Store.DropIndexAsync(name, index.PropertyKey, op.Context, cancellationToken).ConfigureAwait(false);
            }
            await _database.Catalog.DeleteLabelAsync(label.Id, op.Context, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Drops an unused relationship type definition.</summary>
    /// <param name="name">The type name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completion of the mutation.</returns>
    /// <exception cref="DatabaseObjectLockedException">The type is schema owned.</exception>
    /// <exception cref="DatabaseException">The type is missing or in use.</exception>
    public ValueTask DropRelationshipTypeAsync(string name, CancellationToken cancellationToken = default)
        => Write(async op =>
        {
            var type = _database.Catalog.FindRelationshipType(name, op.Context.Snapshot) ?? throw new DatabaseException("COHDBG002: Unknown relationship type.");
            EnsureMutable(type.Name, type.Owner, type.OwningSchema, "DROP RELATIONSHIP TYPE");
            var latest = _database.LatestSnapshot(op.Context);
            foreach (var node in _database.Store.GetNodes(null, latest))
            {
                if ((await _database.Store.GetIncidentAsync(node.Id, latest, cancellationToken).ConfigureAwait(false)).Any(edge => edge.Type == name))
                { throw new DatabaseException("COHDBG003: The relationship type is in use."); }
            }
            await _database.Catalog.DeleteRelationshipTypeAsync(type.Id, op.Context, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private GraphLabelMetadata Label(string name, GraphOperation operation)
        => _database.Catalog.FindLabel(name, operation.Context.Snapshot) ?? throw new DatabaseException($"COHDBG002: Unknown label '{name}'.");

    private async ValueTask Write(Func<GraphOperation, ValueTask> action, CancellationToken token)
    {
        await _database.RunAsync(_session, async op =>
        {
            await _database.LockWriterAsync(op.Context, token).ConfigureAwait(false);
            await action(op).ConfigureAwait(false);
            return true;
        }, token).ConfigureAwait(false);
    }

    private static void EnsureMutable(string name, DatabaseObjectOwner owner, string? schema, string operation)
    {
        if (owner == DatabaseObjectOwner.Schema) { throw new DatabaseObjectLockedException(name, schema!, operation); }
    }
}
