using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Graph.Catalog;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal sealed class GraphSchemaSession : IGraphSchema
{
    private readonly GraphDatabaseInstance _database;
    private readonly GraphDatabaseSession _session;

    /// <summary>Initializes a new instance of the <see cref="GraphSchemaSession"/> class.</summary>
    /// <param name="database">The graph database whose catalog and store the schema surface reads and changes.</param>
    /// <param name="session">The session that every schema operation runs under.</param>
    public GraphSchemaSession(GraphDatabaseInstance database, GraphDatabaseSession session)
    {
        _database = database;
        _session = session;
    }

    public ValueTask<IReadOnlyList<GraphLabelMetadata>> GetLabelsAsync(CancellationToken cancellationToken = default)
        => _database.RunAsync(_session, op => new ValueTask<IReadOnlyList<GraphLabelMetadata>>(_database.Catalog.GetLabels(op.Context.Snapshot)), cancellationToken);
    public ValueTask<IReadOnlyList<GraphRelationshipTypeMetadata>> GetRelationshipTypesAsync(CancellationToken cancellationToken = default)
        => _database.RunAsync(_session, op => new ValueTask<IReadOnlyList<GraphRelationshipTypeMetadata>>(_database.Catalog.GetRelationshipTypes(op.Context.Snapshot)), cancellationToken);
    public ValueTask<IReadOnlyList<GraphPropertyKeyMetadata>> GetPropertyKeysAsync(Guid definitionId, CancellationToken cancellationToken = default)
        => _database.RunAsync(_session, op => new ValueTask<IReadOnlyList<GraphPropertyKeyMetadata>>(_database.Catalog.GetPropertyKeys(definitionId, op.Context.Snapshot)), cancellationToken);
    public ValueTask<IReadOnlyList<GraphIndexMetadata>> GetIndexesAsync(string label, CancellationToken cancellationToken = default)
        => _database.RunAsync(_session, op => new ValueTask<IReadOnlyList<GraphIndexMetadata>>(_database.Catalog.GetIndexes(Label(label, op).Id, op.Context.Snapshot)), cancellationToken);
    public ValueTask SaveLabelAsync(GraphLabelMetadata definition, CancellationToken cancellationToken = default)
        => Write(op => _database.Catalog.SaveLabelAsync(definition, op.Context, cancellationToken), cancellationToken);
    public ValueTask SaveRelationshipTypeAsync(GraphRelationshipTypeMetadata definition, CancellationToken cancellationToken = default)
        => Write(op => _database.Catalog.SaveRelationshipTypeAsync(definition, op.Context, cancellationToken), cancellationToken);
    public ValueTask SavePropertyKeyAsync(GraphPropertyKeyMetadata definition, CancellationToken cancellationToken = default)
        => Write(async op =>
        {
            var latest = _database.LatestSnapshot(op.Context);
            var label = _database.Catalog.GetLabels(latest).FirstOrDefault(item => item.Id == definition.DefinitionId);
            var type = _database.Catalog.GetRelationshipTypes(latest).FirstOrDefault(item => item.Id == definition.DefinitionId);
            if (label.Id != Guid.Empty) { EnsureMutable(label.Name, label.Owner, label.OwningSchema, "ALTER LABEL"); }
            if (type.Id != Guid.Empty) { EnsureMutable(type.Name, type.Owner, type.OwningSchema, "ALTER RELATIONSHIP TYPE"); }
            if (definition.Type is { } scalarType && !GraphDatabaseInstance.SupportsPropertyType(scalarType))
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
                if (definition.Required && !present || present && definition.Type is { } expected && GraphDatabaseInstance.TypeOf(value) != expected)
                { throw new DatabaseException($"COHDBG003: Existing graph data violates property '{definition.Name}'."); }
            }
        }, cancellationToken);
    public ValueTask CreateIndexAsync(string label, string name, string propertyKey, CancellationToken cancellationToken = default)
        => Write(async op =>
        {
            var metadata = Label(label, op);
            await _database.Catalog.SaveIndexAsync(new GraphIndexMetadata(metadata.Id, name, propertyKey), op.Context, cancellationToken).ConfigureAwait(false);
            await _database.Store.CreateIndexAsync(label, propertyKey, op.Context, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
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
