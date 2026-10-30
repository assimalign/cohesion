using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Storage;

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
[global::System.Diagnostics.DebuggerDisplay("GraphSchema")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.GraphSchema.DebugView))]
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
    /// <exception cref="DatabaseException">The session belongs to another database (<c>CG07005</c>), or it is closed.</exception>
    public static GraphSchema Open(GraphDatabase database, GraphDatabaseSession session)
    {
        ArgumentNullException.ThrowIfNull(database);
        return new GraphSchema(database, database.RequireOwnSession(session));
    }

    /// <summary>Lists visible labels.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The visible label definitions.</returns>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">The session is closed, another statement holds it, or its transaction refuses statements (<c>CG04007</c>).</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>CG06012</c>, #1243).</exception>
    public ValueTask<IReadOnlyList<GraphLabelMetadata>> GetLabelsAsync(CancellationToken cancellationToken = default)
        => _database.RunAsync(_session, op => new ValueTask<IReadOnlyList<GraphLabelMetadata>>(_database.Catalog.GetLabels(op.Context.Snapshot)), cancellationToken);

    /// <summary>Lists visible relationship types.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The visible relationship type definitions.</returns>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">The session is closed, another statement holds it, or its transaction refuses statements (<c>CG04007</c>).</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>CG06012</c>, #1243).</exception>
    public ValueTask<IReadOnlyList<GraphRelationshipTypeMetadata>> GetRelationshipTypesAsync(CancellationToken cancellationToken = default)
        => _database.RunAsync(_session, op => new ValueTask<IReadOnlyList<GraphRelationshipTypeMetadata>>(_database.Catalog.GetRelationshipTypes(op.Context.Snapshot)), cancellationToken);

    /// <summary>Lists property metadata for a label or relationship type.</summary>
    /// <param name="definitionId">The label or relationship type identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The visible property definitions.</returns>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">The session is closed, another statement holds it, or its transaction refuses statements (<c>CG04007</c>).</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>CG06012</c>, #1243).</exception>
    public ValueTask<IReadOnlyList<GraphPropertyKeyMetadata>> GetPropertyKeysAsync(Guid definitionId, CancellationToken cancellationToken = default)
        => _database.RunAsync(_session, op => new ValueTask<IReadOnlyList<GraphPropertyKeyMetadata>>(_database.Catalog.GetPropertyKeys(definitionId, op.Context.Snapshot)), cancellationToken);

    /// <summary>Lists a label's property indexes.</summary>
    /// <param name="label">The label name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The visible indexes. For a label the database does not have, no indexes and a
    /// <c>CG01010</c> warning in <see cref="GraphSchemaResult{T}.Diagnostics"/>: the read does
    /// not fail, so it leaves an explicit transaction active.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="label"/> is null; checked before the read starts.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">The session is closed, another statement holds it, or its transaction refuses statements (<c>CG04007</c>).</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>CG06012</c>, #1243).</exception>
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
    /// <exception cref="ArgumentException">The definition is invalid, such as a blank name.</exception>
    /// <exception cref="DatabaseTransactionAbortedException">
    /// The definition changed since the transaction's snapshot (<c>CG04007</c>, retryable).
    /// </exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">
    /// The identity or name conflicts with an existing definition (<c>CG03003</c>, a caller error that a retry repeats); the catalog record exceeds one record (<c>CG02009</c>); the session is closed, another statement holds it, or its transaction refuses statements
    /// (<c>CG04007</c>); or the transaction ended while the statement waited for the writer lock.
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>CG06012</c>, #1243).</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">An autocommit statement's commit record could not be confirmed durable.</exception>
    public ValueTask SaveLabelAsync(GraphLabelMetadata definition, CancellationToken cancellationToken = default)
        => WriteAsync(op => _database.Catalog.SaveLabelAsync(definition, op.Context, cancellationToken), cancellationToken);

    /// <summary>Creates or alters a relationship type definition.</summary>
    /// <param name="definition">The definition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completion of the mutation.</returns>
    /// <exception cref="DatabaseObjectLockedException">The existing type is schema owned.</exception>
    /// <exception cref="ArgumentException">The definition is invalid, such as a blank name.</exception>
    /// <exception cref="DatabaseTransactionAbortedException">
    /// The definition changed since the transaction's snapshot (<c>CG04007</c>, retryable).
    /// </exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">
    /// The identity or name conflicts with an existing definition (<c>CG03003</c>, a caller error that a retry repeats); the catalog record exceeds one record (<c>CG02009</c>); the session is closed, another statement holds it, or its transaction refuses statements
    /// (<c>CG04007</c>); or the transaction ended while the statement waited for the writer lock.
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>CG06012</c>, #1243).</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">An autocommit statement's commit record could not be confirmed durable.</exception>
    public ValueTask SaveRelationshipTypeAsync(GraphRelationshipTypeMetadata definition, CancellationToken cancellationToken = default)
        => WriteAsync(op => _database.Catalog.SaveRelationshipTypeAsync(definition, op.Context, cancellationToken), cancellationToken);

    /// <summary>Creates or alters property-key metadata.</summary>
    /// <param name="definition">The property definition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completion of the mutation.</returns>
    /// <exception cref="DatabaseObjectLockedException">The owning definition is schema owned.</exception>
    /// <exception cref="ArgumentException">The property metadata is invalid, such as a blank name.</exception>
    /// <exception cref="DatabaseTransactionAbortedException">The owning definition changed since the transaction's snapshot (<c>CG04007</c>, retryable).</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">
    /// The owning definition does not exist (<c>CG03003</c>, a caller error that a retry repeats); the property type is not supported by graph storage, or existing graph data violates the
    /// property (both <c>CG03003</c>); the session is closed, another statement holds it, or its
    /// transaction refuses statements (<c>CG04007</c>); or the transaction ended while the
    /// statement waited for the writer lock.
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>CG06012</c>, #1243).</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">An autocommit statement's commit record could not be confirmed durable.</exception>
    public ValueTask SavePropertyKeyAsync(GraphPropertyKeyMetadata definition, CancellationToken cancellationToken = default)
        => WriteAsync(async op =>
        {
            var latest = _database.LatestSnapshot(op.Context);
            var label = _database.Catalog.FindLabel(definition.DefinitionId, latest) ?? default;
            var type = _database.Catalog.FindRelationshipType(definition.DefinitionId, latest) ?? default;
            if (label.Id != Guid.Empty) { EnsureMutable(label.Name, label.Owner, label.OwningSchema, "ALTER LABEL"); }
            if (type.Id != Guid.Empty) { EnsureMutable(type.Name, type.Owner, type.OwningSchema, "ALTER RELATIONSHIP TYPE"); }
            if (definition.Type is { } scalarType && !GraphDatabase.SupportsPropertyType(scalarType))
            { throw new DatabaseException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SchemaSavePropertyKeyAsyncDatabase, scalarType)); }
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
                { throw new DatabaseException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SchemaSavePropertyKeyAsyncDatabase2, definition.Name)); }
            }
        }, cancellationToken);

    /// <summary>Builds a B+Tree index over a node property, including existing nodes.</summary>
    /// <param name="label">The indexed label.</param>
    /// <param name="name">The index name.</param>
    /// <param name="propertyKey">The property name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completion of index creation.</returns>
    /// <exception cref="DatabaseObjectLockedException">The label is schema owned.</exception>
    /// <exception cref="ArgumentException"><paramref name="label"/> is null, or the index name or the property name is null, empty or whitespace.</exception>
    /// <exception cref="DatabaseTransactionAbortedException">The label or index changed since the transaction's snapshot (<c>CG04007</c>, retryable).</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">
    /// The index name is already used in the database (<c>CG03008</c>, a caller error that a retry repeats); the label is unknown (<c>CG01002</c>); the index already exists (<c>CG03003</c>); the
    /// names exceed one graph record, or an existing node's value exceeds the index key
    /// (<c>CG02009</c>); the session is closed,
    /// another statement holds it, or its transaction refuses statements (<c>CG04007</c>); or the
    /// transaction ended while the statement waited for the writer lock.
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>CG06012</c>, #1243).</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">An autocommit statement's commit record could not be confirmed durable.</exception>
    public ValueTask CreateIndexAsync(string label, string name, string propertyKey, CancellationToken cancellationToken = default)
        => WriteAsync(async op =>
        {
            var metadata = Label(label, op);
            if (_database.Catalog.FindIndex(name, op.Context.Snapshot) is not null ||
                _database.Catalog.FindConstraint(name, op.Context.Snapshot) is not null)
            {
                string message = string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SchemaNameDuplicate, name);
                throw new DatabaseException(message, [new Types.Diagnostic
                { Code = "CG03008", Message = message, Severity = Types.DiagnosticSeverity.Error }]);
            }
            await _database.Catalog.SaveIndexAsync(new GraphIndexMetadata(metadata.Id, name, propertyKey), op.Context, cancellationToken).ConfigureAwait(false);
            await _database.Store.CreateIndexAsync(label, propertyKey, op.Context, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Drops an unused label definition.</summary>
    /// <param name="name">The label name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completion of the mutation.</returns>
    /// <exception cref="DatabaseObjectLockedException">The label is schema owned.</exception>
    /// <exception cref="DatabaseTransactionAbortedException">The label or its property or index metadata changed since the transaction's snapshot.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">
    /// The label is unknown (<c>CG01002</c>) or in use (<c>CG03003</c>); the session is closed,
    /// another statement holds it, or its transaction refuses statements (<c>CG04007</c>); or the
    /// transaction ended while the statement waited for the writer lock.
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>CG06012</c>, #1243).</exception>
    /// <exception cref="DatabaseTransactionAbortedException">One of the label's indexes changed since the transaction's snapshot (retryable).</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">An autocommit statement's commit record could not be confirmed durable.</exception>
    public ValueTask DropLabelAsync(string name, CancellationToken cancellationToken = default)
        => WriteAsync(async op =>
        {
            var label = Label(name, op);
            EnsureMutable(label.Name, label.Owner, label.OwningSchema, "DROP LABEL");
            if (_database.Store.GetNodes(name, _database.LatestSnapshot(op.Context)).Count != 0) { throw new DatabaseException(ErrorMessages.GraphSchemaDropLabelAsyncDatabase); }
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
    /// <exception cref="DatabaseTransactionAbortedException">The type or its property metadata changed since the transaction's snapshot.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">
    /// The type is unknown (<c>CG01002</c>) or in use (<c>CG03003</c>); the session is closed,
    /// another statement holds it, or its transaction refuses statements (<c>CG04007</c>); or the
    /// transaction ended while the statement waited for the writer lock.
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>CG06012</c>, #1243).</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">An autocommit statement's commit record could not be confirmed durable.</exception>
    public ValueTask DropRelationshipTypeAsync(string name, CancellationToken cancellationToken = default)
        => WriteAsync(async op =>
        {
            var type = _database.Catalog.FindRelationshipType(name, op.Context.Snapshot) ?? throw new DatabaseException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.RelationshipTypeUnknown, name),
                [new Types.Diagnostic { Code = "CG01002", Message = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    ErrorMessages.RelationshipTypeUnknown, name), Severity = Types.DiagnosticSeverity.Error }]);
            EnsureMutable(type.Name, type.Owner, type.OwningSchema, "DROP RELATIONSHIP TYPE");
            var latest = _database.LatestSnapshot(op.Context);
            foreach (var node in _database.Store.GetNodes(null, latest))
            {
                await foreach (var edge in _database.Store.GetIncidentAsync(node.Id, GraphIncidentDirection.Outgoing, [type.Id], latest, cancellationToken).ConfigureAwait(false))
                { throw new DatabaseException(ErrorMessages.DropRelationshipTypeAsyncDatabase); }
            }
            await _database.Catalog.DeleteRelationshipTypeAsync(type.Id, op.Context, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Lists all database indexes in ordinal name order.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The visible index definitions.</returns>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (CG06012).</exception>
    /// <exception cref="DatabaseException">The session is unavailable or its transaction refuses statements (CG04007).</exception>
    public ValueTask<IReadOnlyList<GraphIndexMetadata>> GetIndexesAsync(CancellationToken cancellationToken = default)
        => _database.RunAsync(_session, op => new ValueTask<IReadOnlyList<GraphIndexMetadata>>(
            _database.Catalog.GetIndexes(op.Context.Snapshot)), cancellationToken);

    /// <summary>Finds an index by its database-wide name.</summary>
    /// <param name="name">The ordinal schema-rule name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The visible index, or null.</returns>
    /// <exception cref="ArgumentNullException">The name is null.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (CG06012).</exception>
    /// <exception cref="DatabaseException">The session is unavailable or its transaction refuses statements (CG04007).</exception>
    public ValueTask<GraphIndexMetadata?> FindIndexAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _database.RunAsync(_session, op => new ValueTask<GraphIndexMetadata?>(
            _database.Catalog.FindIndex(name, op.Context.Snapshot)), cancellationToken);
    }

    /// <summary>Lists the database's independently named constraints in ordinal name order.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The visible constraint definitions.</returns>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (CG06012).</exception>
    /// <exception cref="DatabaseException">The session is unavailable or its transaction refuses statements (CG04007).</exception>
    public ValueTask<IReadOnlyList<GraphConstraintMetadata>> GetConstraintsAsync(CancellationToken cancellationToken = default)
        => _database.RunAsync(_session, op => new ValueTask<IReadOnlyList<GraphConstraintMetadata>>(
            _database.Catalog.GetConstraints(op.Context.Snapshot)), cancellationToken);

    /// <summary>Finds a constraint by its database-wide name.</summary>
    /// <param name="name">The ordinal schema-rule name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The visible constraint, or null.</returns>
    /// <exception cref="ArgumentNullException">The name is null.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (CG06012).</exception>
    /// <exception cref="DatabaseException">The session is unavailable or its transaction refuses statements (CG04007).</exception>
    public ValueTask<GraphConstraintMetadata?> FindConstraintAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _database.RunAsync(_session, op => new ValueTask<GraphConstraintMetadata?>(
            _database.Catalog.FindConstraint(name, op.Context.Snapshot)), cancellationToken);
    }

    /// <summary>Creates a named type constraint, or an existence constraint when Type is null, validating all existing data.</summary>
    /// <param name="constraint">The owning definition, database-wide name, property and optional scalar type.</param>
    /// <param name="cancellationToken">Cancels the mutation.</param>
    /// <returns>Completion of constraint creation.</returns>
    /// <exception cref="ArgumentException">The metadata is invalid.</exception>
    /// <exception cref="DatabaseException">The name is taken (CG03008), a rule conflicts (CG03009), the owner is missing or the type unsupported (CG03003), or existing data violates the constraint (CG03010).</exception>
    /// <exception cref="GraphElementTooLargeException">The encoded metadata exceeds one record.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (CG06012).</exception>
    /// <exception cref="DatabaseException">The session is unavailable or its transaction refuses statements (CG04007).</exception>
    /// <exception cref="DatabaseObjectLockedException">The owning definition is schema-owned.</exception>
    /// <exception cref="DatabaseTransactionAbortedException">The schema changed after the transaction snapshot (CG04007).</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">The autocommit commit could not be confirmed durable.</exception>
    public ValueTask CreateConstraintAsync(GraphConstraintMetadata constraint, CancellationToken cancellationToken = default)
        => WriteAsync(async op =>
        {
            if (constraint.Type is { } scalarType && !GraphDatabase.SupportsPropertyType(scalarType))
            {
                string message = string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ConstraintTypeUnsupported, scalarType);
                throw new DatabaseException(message, [new Types.Diagnostic
                { Code = "CG03003", Message = message, Severity = Types.DiagnosticSeverity.Error }]);
            }
            await _database.Catalog.CreateConstraintAsync(constraint, op.Context, cancellationToken).ConfigureAwait(false);
            var latest = _database.LatestSnapshot(op.Context);
            if (_database.Catalog.FindLabel(constraint.DefinitionId, latest) is { } label)
            {
                foreach (var node in _database.Store.GetNodes(label.Name, latest))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Validate(node.Properties);
                }
            }
            else
            {
                foreach (var node in _database.Store.GetNodes(null, latest))
                {
                    await foreach (var edge in _database.Store.GetIncidentAsync(node.Id, GraphIncidentDirection.Outgoing,
                        [constraint.DefinitionId], latest, cancellationToken).ConfigureAwait(false))
                    { Validate(edge.Properties); }
                }
            }
            void Validate(IReadOnlyDictionary<string, object?> properties)
            {
                bool present = properties.TryGetValue(constraint.PropertyKey, out var value) && value is not null;
                if (constraint.Type is null ? !present : present && GraphDatabase.TypeOf(value) != constraint.Type.Value)
                {
                    string message = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        ErrorMessages.ConstraintDataViolation, constraint.Name, constraint.PropertyKey);
                    throw new DatabaseException(message, [new Types.Diagnostic
                    { Code = "CG03010", Message = message, Severity = Types.DiagnosticSeverity.Error }]);
                }
            }
        }, cancellationToken);

    /// <summary>Drops a constraint by name, preserving every other constraint and property definition.</summary>
    /// <param name="name">The database-wide constraint name.</param>
    /// <param name="cancellationToken">Cancels the mutation.</param>
    /// <returns>Completion of the drop.</returns>
    /// <exception cref="ArgumentException">The name is null or whitespace.</exception>
    /// <exception cref="DatabaseException">The constraint does not exist (CG01021).</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (CG06012).</exception>
    /// <exception cref="DatabaseException">The session is unavailable or its transaction refuses statements (CG04007).</exception>
    /// <exception cref="DatabaseObjectLockedException">The owning definition is schema-owned.</exception>
    /// <exception cref="DatabaseTransactionAbortedException">The schema changed after the transaction snapshot (CG04007).</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">The autocommit commit could not be confirmed durable.</exception>
    public ValueTask DropConstraintAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return WriteAsync(async op =>
        {
            if (_database.Catalog.FindConstraint(name, op.Context.Snapshot) is null) { throw Missing("Constraint", name); }
            await _database.Catalog.DeleteConstraintAsync(name, op.Context, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <summary>Drops an index by its database-wide name, including its physical index.</summary>
    /// <param name="name">The database-wide index name.</param>
    /// <param name="cancellationToken">Cancels the mutation.</param>
    /// <returns>Completion of the drop.</returns>
    /// <exception cref="ArgumentException">The name is null or whitespace.</exception>
    /// <exception cref="DatabaseException">The index does not exist (CG01021).</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (CG06012).</exception>
    /// <exception cref="DatabaseException">The session is unavailable or its transaction refuses statements (CG04007).</exception>
    /// <exception cref="DatabaseObjectLockedException">The owning definition is schema-owned.</exception>
    /// <exception cref="DatabaseTransactionAbortedException">The schema changed after the transaction snapshot (CG04007).</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">The autocommit commit could not be confirmed durable.</exception>
    public ValueTask DropIndexAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return WriteAsync(async op =>
        {
            var index = _database.Catalog.FindIndex(name, op.Context.Snapshot) ?? throw Missing("Index", name);
            // Catalog performs ownership and stale-snapshot checks before the physical drop.
            await _database.Catalog.DeleteIndexAsync(index.LabelId, name, op.Context, cancellationToken).ConfigureAwait(false);
            var label = _database.Catalog.FindLabel(index.LabelId, op.Context.Snapshot)!.Value;
            await _database.Store.DropIndexAsync(label.Name, index.PropertyKey, op.Context, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    private static DatabaseException Missing(string kind, string name)
    {
        string message = string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SchemaNameMissing, kind, name);
        return new DatabaseException(message, [new Types.Diagnostic
        { Code = "CG01021", Message = message, Severity = Types.DiagnosticSeverity.Error }]);
    }

    private GraphLabelMetadata Label(string name, GraphOperation operation)
        => _database.Catalog.FindLabel(name, operation.Context.Snapshot) ?? throw new DatabaseException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.GraphSchemaLabelDatabase, name));

    private async ValueTask WriteAsync(Func<GraphOperation, ValueTask> action, CancellationToken token)
    {
        await _database.RunAsync(_session, async op =>
        {
            await _database.LockWriterAsync(op, token).ConfigureAwait(false);
            await action(op).ConfigureAwait(false);
            return true;
        }, token).ConfigureAwait(false);
    }

    private static void EnsureMutable(string name, DatabaseObjectOwner owner, string? schema, string operation)
    {
        if (owner == DatabaseObjectOwner.Schema) { throw new DatabaseObjectLockedException(name, schema!, operation); }
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Graph.GraphSchema value)
        {
            _ = value;
        }
    }
}
